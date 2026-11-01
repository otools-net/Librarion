using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Librarion.Core.Catalog;
using Librarion.Core.Containers;
using Librarion.Core.Data;
using Librarion.Core.Formats;
using Librarion.Core.Import;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Imports;

/// <summary>
/// Writes what an import source reads into a library (agents/docs/features/imports.md), a batch of
/// <see cref="BatchSize"/> records at a time. A record's book is the one a file of it already belongs to (the folder
/// was scanned, or imported before), else the one with its source id, else a new book; it takes the record's metadata
/// and is owned by the source afterwards. A record whose files aren't found is skipped (never a book without a file).
/// The same record again (its hash) is skipped without writing. Archives the import lists for the first time are
/// written as done at its end, so a later scan skips them unchanged. Nothing is ever removed.
/// </summary>
public sealed class CatalogImporter(TenantDb tenantDb, TimeProvider time)
{
    public const int BatchSize = 1000;
    private const int KeptListings = 2;
    private static readonly JsonSerializerOptions HashJson = new() { IncludeFields = true };

    public async Task<ScanResult> ImportAsync(int tenantId, int libraryId, int rootId, Func<IImportSource> open, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var root = await db.LibraryRoots.SingleOrDefaultAsync(r => r.Id == rootId && r.LibraryId == libraryId, cancellationToken)
            ?? throw new InvalidOperationException($"Root {rootId} is not a root of library {libraryId}.");
        var run = new Run(db, root, new CatalogWriter(tenantId, libraryId, await ImportTagResolver.CreateAsync(db, tenantId, cancellationToken), time), progress);
        using var source = open();
        try
        {
            foreach (var batch in source.Read(run.Report, cancellationToken).Chunk(BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await run.BatchAsync(source, batch, cancellationToken);
            }

            await run.FinishArchivesAsync(cancellationToken);
        }
        finally
        {
            await run.Writer.FlushCountsAsync(db, CancellationToken.None);
        }

        return run.Result();
    }

    /// <summary>A file of a record, relative to the root: loose (<paramref name="Archive"/> null), or an entry of an archive.</summary>
    private readonly record struct Location(string? Archive, string Path);

    private sealed class ArchiveState
    {
        public int Id;
        public bool Present;
        public bool Created;
        public string Full = "";

        /// <summary>The book entries by name, listed when an entry isn't a row yet; dropped for archives used longest ago.</summary>
        public Dictionary<string, ArchiveEntry>? Entries;
    }

    private sealed class Run(LibrarionDbContext db, LibraryRoot root, CatalogWriter writer, IProgress<ScanProgress>? progress)
    {
        private readonly Dictionary<string, ArchiveState> archives = new(StringComparer.Ordinal);
        private readonly LinkedList<ArchiveState> listed = new();
        private int seen, unchanged;

        public CatalogWriter Writer => writer;
        public ImportReport Report { get; } = new();

        public ScanResult Result() => new(writer.Added, writer.Refreshed, 0, unchanged, 0, 0, 0, [], Report.Problems, writer.Missing, writer.Returned, Report.Skipped);

        public async Task BatchAsync(IImportSource source, IReadOnlyList<ImportRecord> records, CancellationToken cancellationToken)
        {
            // Where each file is, relative to the root; files outside it can't be books of this library.
            var locations = new List<Location>[records.Count];
            for (var i = 0; i < records.Count; i++)
            {
                locations[i] = [];
                foreach (var file in records[i].Files)
                    if (Locate(file) is { } location)
                        locations[i].Add(location);
                    else
                        Report.Problem($"{records[i].SourceId}: {file.Path}{(file.Entry is null ? "" : "/" + file.Entry)} is not inside {root.Path}");
            }

            foreach (var name in locations.SelectMany(l => l).Select(l => l.Archive).OfType<string>().Distinct())
                await ArchiveAsync(name, cancellationToken);

            var rows = await RowsAsync(locations.SelectMany(l => l).Distinct().ToList(), cancellationToken);
            var values = records.Select(r => Cut(r.SourceId)).Where(v => v.Length > 0).Distinct().ToList();
            // Two plain lookups, no join: while an import grows the tables, PostgreSQL's statistics lag behind and a join
            // was planned as a walk over every book of the tenant per identifier (30 s at 9,000 books).
            var identified = await db.BookIdentifiers.Where(i => i.Scheme == source.IdScheme && values.Contains(i.Value))
                .Select(i => new { i.Value, i.BookId }).ToListAsync(cancellationToken);
            var candidates = identified.Select(i => i.BookId).Concat(rows.Values.Select(r => r.BookId)).Distinct().ToList();
            var books = (await db.Books.Where(b => candidates.Contains(b.Id)).Select(b => new { b.Id, b.LibraryId, b.MetadataSource, b.ImportHash, b.MergedIntoId })
                    .ToListAsync(cancellationToken))
                .ToDictionary(b => b.Id);
            var bySourceId = identified.Where(i => books.TryGetValue(i.BookId, out var b) && b.LibraryId == root.LibraryId)
                .GroupBy(i => i.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Min(i => books[i.BookId].MergedIntoId ?? i.BookId), StringComparer.Ordinal);

            var written = new List<CatalogWriter.ImportedBook>();
            var claimed = new HashSet<Location>();
            var foundAgain = new List<int>();
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                seen++;
                int? bookId = null;
                var files = new List<BookFile>();
                string? path = null;
                var back = false;
                foreach (var location in locations[i])
                {
                    if (rows.TryGetValue(location, out var row))
                    {
                        var owner = books.TryGetValue(row.BookId, out var b) ? b.MergedIntoId ?? b.Id : row.BookId;
                        bookId ??= owner;
                        path ??= location.Path;
                        if (row.MissingSince is not null && owner == bookId && IsThere(location))
                        {
                            foundAgain.Add(row.Id);
                            back = true;
                        }

                        continue;
                    }

                    if (claimed.Contains(location) || NewFile(location) is not { } file)
                        continue;
                    claimed.Add(location);
                    files.Add(file);
                    path ??= location.Path;
                }

                if (bookId is null && bySourceId.TryGetValue(Cut(record.SourceId), out var known))
                    bookId = known;
                if (path is null || (bookId is null && files.Count == 0))
                {
                    Report.Skip($"{record.SourceId}: {(record.Files.Count == 0 ? "no file" : "file not found")}");
                    continue;
                }

                // The source id first: a book keeps at most 20 identifiers.
                var metadata = record.Metadata with { Identifiers = [(source.IdScheme, Cut(record.SourceId)), .. record.Metadata.Ids] };
                var hash = Hash(metadata, record.Files, record.Cover);
                if (bookId is { } id && files.Count == 0 && !back && books.TryGetValue(id, out var current) && current.MetadataSource == source.Owner && current.ImportHash == hash)
                {
                    unchanged++;
                    continue;
                }

                written.Add(new CatalogWriter.ImportedBook(bookId, files, path, metadata, source.Owner, hash, record.Cover));
            }

            if (foundAgain.Count > 0)
                await db.BookFiles.Where(f => foundAgain.Contains(f.Id)).ExecuteUpdateAsync(s => s.SetProperty(f => f.MissingSince, (DateTime?)null), cancellationToken);
            await writer.ImportAsync(db, written, cancellationToken);
            await writer.FlushCountsAsync(db, cancellationToken);
            progress?.Report(new ScanProgress(seen, 0, writer.Added, writer.Refreshed, 0, records[^1].SourceId, writer.Missing, writer.Returned, Report.Skipped));
        }

        /// <summary>The archives this import listed first, written as done: a later scan skips them while they are unchanged.</summary>
        public async Task FinishArchivesAsync(CancellationToken cancellationToken)
        {
            foreach (var archive in archives.Values.Where(a => a.Created))
            {
                var id = archive.Id;
                var count = await db.BookFiles.CountAsync(f => f.ArchiveId == id, cancellationToken);
                await db.Archives.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ScanState, ArchiveScanState.Done)
                    .SetProperty(a => a.EntryCount, count), cancellationToken);
            }
        }

        private Location? Locate(ImportFile file)
        {
            string full;
            try
            {
                full = Path.GetFullPath(file.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            if (!Path.IsPathFullyQualified(file.Path) || !LibraryRoots.IsWithin(full, root.Path) || full.Length == root.Path.Length)
                return null;
            var relative = Path.GetRelativePath(root.Path, full).Replace('\\', '/');
            if (relative.Length > BookFile.MaxPathLength)
                return null;
            if (file.Entry is null)
                return new Location(null, relative);
            var entry = file.Entry.Replace('\\', '/');
            return entry.Length is 0 or > BookFile.MaxPathLength ? null : new Location(relative, entry);
        }

        /// <summary>The archive's row, made (and its entries listed) when the library doesn't have it yet.</summary>
        private async Task<ArchiveState> ArchiveAsync(string name, CancellationToken cancellationToken)
        {
            if (archives.TryGetValue(name, out var state))
                return state;
            state = archives[name] = new ArchiveState { Full = Path.Combine(root.Path, name) };
            var key = PathKeys.Of(name);
            var row = (await db.Archives.Where(a => a.RootId == root.Id && a.PathKey == key).Select(a => new { a.Id, a.Path, a.MissingSince }).ToListAsync(cancellationToken))
                .FirstOrDefault(a => a.Path == name);
            var info = new FileInfo(state.Full);
            if (row is not null)
            {
                state.Id = row.Id;
                state.Present = row.MissingSince is null && info.Exists;
                return state;
            }

            if (!info.Exists || List(state) is not { } fingerprint)
                return state;
            var archive = new Archive
            {
                TenantId = root.TenantId, LibraryId = root.LibraryId, RootId = root.Id, Path = name, PathKey = key, Size = info.Length,
                Modified = Moment(info.LastWriteTimeUtc), Fingerprint = fingerprint, ScanState = ArchiveScanState.Scanning,
            };
            db.Archives.Add(archive);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            (state.Id, state.Present, state.Created) = (archive.Id, true, true);
            return state;
        }

        /// <summary>The archive's book entries by name; its fingerprint, or null when it can't be read.</summary>
        private long? List(ArchiveState state)
        {
            try
            {
                using var stream = new FileStream(state.Full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024);
                var directory = ZipDirectory.Locate(stream);
                var entries = new Dictionary<string, ArchiveEntry>(StringComparer.Ordinal);
                foreach (var entry in directory.Read(stream, CancellationToken.None))
                    if (entry.Kind == ArchiveEntryKind.File && !entry.Encrypted && BookFormats.Of(entry.Name) is not null && entry.Name.Length <= BookFile.MaxPathLength
                        && ArchiveEntryNames.Split(entry.Name, 64, out _) is not null)
                        entries.TryAdd(entry.Name, entry);
                state.Entries = entries;
                listed.Remove(state);
                listed.AddFirst(state);
                while (listed.Count > KeptListings)
                {
                    listed.Last!.Value.Entries = null;
                    listed.RemoveLast();
                }

                return directory.Fingerprint(stream, CancellationToken.None);
            }
            catch (Exception ex) when (ex is ArchiveException or IOException or UnauthorizedAccessException)
            {
                Report.Problem($"{state.Full}: {ex.Message}");
                return null;
            }
        }

        private async Task<Dictionary<Location, (int Id, int BookId, DateTime? MissingSince)>> RowsAsync(List<Location> locations, CancellationToken cancellationToken)
        {
            var rows = new Dictionary<Location, (int, int, DateTime?)>();
            var loose = locations.Where(l => l.Archive is null).ToList();
            if (loose.Count > 0)
            {
                var keys = loose.Select(l => PathKeys.Of(l.Path)).Distinct().ToList();
                var wanted = loose.Select(l => l.Path).ToHashSet(StringComparer.Ordinal);
                foreach (var row in await db.BookFiles.Where(f => f.RootId == root.Id && keys.Contains(f.PathKey))
                             .Select(f => new { f.Id, f.BookId, f.Path, f.MissingSince }).ToListAsync(cancellationToken))
                    if (wanted.Contains(row.Path))
                        rows.TryAdd(new Location(null, row.Path), (row.Id, row.BookId, row.MissingSince));
            }

            foreach (var group in locations.Where(l => l.Archive is not null).GroupBy(l => l.Archive!))
            {
                var archive = archives[group.Key];
                if (archive.Id == 0)
                    continue;
                var id = archive.Id;
                var keys = group.Select(l => PathKeys.Of(l.Path)).Distinct().ToList();
                var wanted = group.Select(l => l.Path).ToHashSet(StringComparer.Ordinal);
                foreach (var row in await db.BookFiles.Where(f => f.ArchiveId == id && keys.Contains(f.PathKey))
                             .Select(f => new { f.Id, f.BookId, f.Path, f.MissingSince }).ToListAsync(cancellationToken))
                    if (wanted.Contains(row.Path))
                        rows.TryAdd(new Location(group.Key, row.Path), (row.Id, row.BookId, row.MissingSince));
            }

            return rows;
        }

        /// <summary>A file of a record the library doesn't have: a row for it when it is there; null when it isn't.</summary>
        private BookFile? NewFile(Location location)
        {
            if (BookFormats.Of(location.Path) is not { } format)
                return null;
            if (location.Archive is null)
            {
                var info = new FileInfo(Path.Combine(root.Path, location.Path));
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return null;
                return new BookFile { RootId = root.Id, Path = location.Path, PathKey = PathKeys.Of(location.Path), Format = format, Size = info.Length, Modified = Moment(info.LastWriteTimeUtc) };
            }

            var archive = archives[location.Archive];
            if (!archive.Present)
                return null;
            if (archive.Entries is null && List(archive) is null)
                return null;
            if (!archive.Entries!.TryGetValue(location.Path, out var entry))
                return null;
            return new BookFile
            {
                ArchiveId = archive.Id, Path = entry.Name, PathKey = PathKeys.Of(entry.Name), Format = format, Size = entry.Size, Crc32 = entry.Crc,
                Modified = entry.Time is { } t ? Moment(DateTime.SpecifyKind(t, DateTimeKind.Local).ToUniversalTime()) : null,
                Locator = entry.Locator, Method = (short)entry.Method, PackedSize = entry.Packed,
            };
        }

        /// <summary>A missing file of a record is there again (a scan would find it too; the import doesn't wait for one).</summary>
        private bool IsThere(Location location)
        {
            if (location.Archive is null)
                return File.Exists(Path.Combine(root.Path, location.Path));
            var archive = archives[location.Archive];
            return archive.Present && (archive.Entries is not null || List(archive) is not null) && archive.Entries!.ContainsKey(location.Path);
        }
    }

    /// <summary>What a record says, hashed: the same record imported again is skipped.</summary>
    private static long Hash(BookMetadata metadata, IReadOnlyList<ImportFile> files, bool cover)
    {
        var text = JsonSerializer.Serialize(new { metadata, files, cover }, HashJson);
        return unchecked((long)XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text)));
    }

    private static string Cut(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > BookIdentifier.MaxValueLength ? trimmed[..BookIdentifier.MaxValueLength] : trimmed;
    }

    /// <summary>A time as both databases keep it (microseconds).</summary>
    private static DateTime Moment(DateTime utc) => new(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
}
