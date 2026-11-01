using Librarion.Core.Catalog;
using Librarion.Core.Containers;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Librarion.Core.Import;

/// <summary>Where a scan is, for the tasks page.</summary>
public sealed record ScanProgress(int FilesSeen, int ArchivesDone, int Added, int Changed, int Removed, string? Current);

/// <summary>What a scan did. <paramref name="Problems"/>: the first files it couldn't read, relative to their root.</summary>
public sealed record ScanResult(
    int Added, int Changed, int Removed, int Unchanged, int ArchivesRead, int ArchivesSkipped, int Unreadable,
    IReadOnlyList<string> MissingRoots, IReadOnlyList<string> Problems);

/// <summary>
/// Brings a library's catalog in step with its roots (agents/docs/features/archives.md): walks every root (links not
/// followed), reads new books from their heads, re-reads changed ones, removes the ones gone. Loose files are matched
/// by path in batches of <see cref="BatchSize"/>; a ZIP container unchanged in size and time is skipped without a read,
/// one only touched (same directory fingerprint) without reading its entries, a changed one diffed by entry name,
/// CRC-32 and size, its new entries read in the order they lie in the file. Memory stays flat: the files a scan saw
/// are bits, never a list of paths. A root that is missing is reported and left alone (an unplugged disk is not a
/// deletion). Resumable: books are written as they are read, an archive is marked done only at its end.
/// </summary>
public sealed class LibraryScanner(TenantDb tenantDb, IMetadataReader reader, TimeProvider time, ILogger<LibraryScanner> logger)
{
    public const int BatchSize = 1000;
    private const int MaxProblems = 100;

    private IMetadataReader Reader => reader;
    private ILogger Logger => logger;

    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Links are not followed: no loops, nothing outside the root reached through one.
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchType = MatchType.Simple,
    };

    public async Task<ScanResult> ScanAsync(int tenantId, int libraryId, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var roots = await db.LibraryRoots.Where(r => r.LibraryId == libraryId).OrderBy(r => r.Id).ToListAsync(cancellationToken);
        var run = new Run(this, db, new CatalogWriter(tenantId, libraryId, await ImportTagResolver.CreateAsync(db, tenantId, cancellationToken), time), progress);
        try
        {
            foreach (var root in roots)
            {
                if (!Directory.Exists(root.Path))
                {
                    run.MissingRoots.Add(root.Path);
                    logger.LogWarning("Library {Library}: root {Root} is missing, its books are kept", libraryId, root.Path);
                    continue;
                }

                await run.ScanRootAsync(root, cancellationToken);
            }
        }
        finally
        {
            // Books already written keep their counts, also when the scan is cancelled or fails.
            await run.Writer.FlushCountsAsync(db, CancellationToken.None);
        }

        return run.Result();
    }

    /// <summary>
    /// A root taken out of its library: its files, archives and the books left without a file removed in batches (one
    /// cascade over millions of rows would be one huge transaction), the counts moved, the root's row last.
    /// </summary>
    public async Task<ScanResult> RemoveRootAsync(int tenantId, int rootId, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var root = await db.LibraryRoots.SingleOrDefaultAsync(r => r.Id == rootId, cancellationToken);
        if (root is null)
            return new ScanResult(0, 0, 0, 0, 0, 0, 0, [], []);
        var run = new Run(this, db, new CatalogWriter(tenantId, root.LibraryId, await ImportTagResolver.CreateAsync(db, tenantId, cancellationToken), time), progress);
        try
        {
            await run.RemoveAllAsync(root, cancellationToken);
        }
        finally
        {
            await run.Writer.FlushCountsAsync(db, CancellationToken.None);
        }

        return run.Result();
    }

    private sealed class Run(LibraryScanner scanner, LibrarionDbContext db, CatalogWriter writer, IProgress<ScanProgress>? progress)
    {
        public CatalogWriter Writer => writer;
        public List<string> MissingRoots { get; } = [];
        private readonly List<string> problems = [];
        private int filesSeen, unchanged, changedFiles, archivesRead, archivesSkipped, unreadable;
        private string? current;

        public ScanResult Result() => new(writer.Added, changedFiles, writer.Removed, unchanged, archivesRead, archivesSkipped, unreadable, MissingRoots, problems);

        public async Task ScanRootAsync(LibraryRoot root, CancellationToken cancellationToken)
        {
            var lastFile = await db.BookFiles.Where(f => f.RootId == root.Id).MaxAsync(f => (int?)f.Id, cancellationToken) ?? 0;
            var lastArchive = await db.Archives.Where(a => a.RootId == root.Id).MaxAsync(a => (int?)a.Id, cancellationToken) ?? 0;
            var seenFiles = new Bits(lastFile);
            var seenArchives = new Bits(lastArchive);
            var batch = new List<(string Path, FileInfo Info, string Format)>(BatchSize);

            foreach (var path in Directory.EnumerateFiles(root.Path, "*", Walk))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root.Path, path).Replace('\\', '/');
                if (relative.Length > BookFile.MaxPathLength)
                    continue;
                if (BookFormats.Of(relative) is { } format)
                {
                    batch.Add((relative, new FileInfo(path), format));
                    if (batch.Count == BatchSize)
                    {
                        await LooseAsync(root, batch, seenFiles, cancellationToken);
                        batch.Clear();
                    }
                }
                else if (BookFormats.IsContainer(relative))
                    await ArchiveAsync(root, relative, new FileInfo(path), seenArchives, cancellationToken);
            }

            await LooseAsync(root, batch, seenFiles, cancellationToken);
            await SweepLooseAsync(root, seenFiles, cancellationToken);
            await SweepArchivesAsync(root, seenArchives, cancellationToken);
            await writer.FlushCountsAsync(db, cancellationToken);
            Report(null);
        }

        public async Task RemoveAllAsync(LibraryRoot root, CancellationToken cancellationToken)
        {
            var lastFile = await db.BookFiles.Where(f => f.RootId == root.Id).MaxAsync(f => (int?)f.Id, cancellationToken) ?? 0;
            var lastArchive = await db.Archives.Where(a => a.RootId == root.Id).MaxAsync(a => (int?)a.Id, cancellationToken) ?? 0;
            // Nothing seen: everything of the root goes.
            await SweepLooseAsync(root, new Bits(lastFile), cancellationToken);
            await SweepArchivesAsync(root, new Bits(lastArchive), cancellationToken);
            await writer.FlushCountsAsync(db, cancellationToken);
            await db.LibraryRoots.Where(r => r.Id == root.Id).ExecuteDeleteAsync(cancellationToken);
            Report(null);
        }

        private async Task LooseAsync(LibraryRoot root, List<(string Path, FileInfo Info, string Format)> batch, Bits seen, CancellationToken cancellationToken)
        {
            if (batch.Count == 0)
                return;
            filesSeen += batch.Count;
            var keys = batch.Select(b => PathKeys.Of(b.Path)).Distinct().ToList();
            var existing = (await db.BookFiles.Where(f => f.RootId == root.Id && keys.Contains(f.PathKey))
                    .Select(f => new { f.Id, f.BookId, f.Path, f.Size, f.Modified }).ToListAsync(cancellationToken))
                .GroupBy(f => f.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var added = new List<ScannedFile>();
            var changed = new List<(int BookId, string Path, BookMetadata? Metadata)>();
            foreach (var (path, info, format) in batch)
            {
                current = path;
                var modified = Moment(info.LastWriteTimeUtc);
                if (existing.TryGetValue(path, out var row))
                {
                    seen.Set(row.Id);
                    if (row.Size == info.Length && row.Modified == modified)
                    {
                        unchanged++;
                        continue;
                    }

                    await db.BookFiles.Where(f => f.Id == row.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.Size, info.Length).SetProperty(f => f.Modified, modified).SetProperty(f => f.Crc32, (long?)null)
                        .SetProperty(f => f.ContentHash, (byte[]?)null), cancellationToken);
                    changed.Add((row.BookId, path, Read(path, format, () => Open(info.FullName), info.Length, cancellationToken)));
                    changedFiles++;
                    continue;
                }

                var file = new BookFile { RootId = root.Id, Path = path, PathKey = PathKeys.Of(path), Format = format, Size = info.Length, Modified = modified };
                added.Add(new ScannedFile(file, Read(path, format, () => Open(info.FullName), info.Length, cancellationToken)));
            }

            await writer.AddAsync(db, added, cancellationToken);
            await writer.RefreshAsync(db, changed, cancellationToken);
            Report(current);
        }

        private async Task ArchiveAsync(LibraryRoot root, string path, FileInfo info, Bits seen, CancellationToken cancellationToken)
        {
            current = path;
            var key = PathKeys.Of(path);
            var row = (await db.Archives.Where(a => a.RootId == root.Id && a.PathKey == key).ToListAsync(cancellationToken)).FirstOrDefault(a => a.Path == path);
            db.ChangeTracker.Clear();
            var modified = Moment(info.LastWriteTimeUtc);
            if (row is not null)
            {
                seen.Set(row.Id);
                if (row.Size == info.Length && row.Modified == modified && row.ScanState is ArchiveScanState.Done or ArchiveScanState.Incomplete)
                {
                    archivesSkipped++;
                    return;
                }
            }

            await using var stream = Open(info.FullName);
            ZipDirectory directory;
            long fingerprint;
            try
            {
                directory = ZipDirectory.Locate(stream);
                fingerprint = directory.Fingerprint(stream, cancellationToken);
            }
            catch (Exception ex) when (ex is ArchiveException or IOException)
            {
                Problem(path, ex.Message);
                await SaveArchiveAsync(root, row, path, key, info.Length, modified, null, 0, ArchiveScanState.Incomplete, seen, cancellationToken);
                return;
            }

            if (row is not null && row.Fingerprint == fingerprint && row.ScanState == ArchiveScanState.Done)
            {
                await db.Archives.Where(a => a.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Size, info.Length).SetProperty(a => a.Modified, modified), cancellationToken);
                archivesSkipped++;
                return;
            }

            var archiveId = await SaveArchiveAsync(root, row, path, key, info.Length, modified, row?.Fingerprint, row?.EntryCount ?? 0, ArchiveScanState.Scanning, seen, cancellationToken);
            var state = ArchiveScanState.Done;
            var books = new List<ArchiveEntry>();
            try
            {
                foreach (var entry in directory.Read(stream, cancellationToken))
                {
                    if (entry.Kind != ArchiveEntryKind.File || BookFormats.Of(entry.Name) is null)
                        continue;
                    if (ArchiveEntryNames.Split(entry.Name, 64, out var reason) is null || entry.Name.Length > BookFile.MaxPathLength)
                    {
                        Problem($"{path}: {Printable(entry.Name)}", reason ?? "the name is too long");
                        continue;
                    }

                    if (entry.Encrypted)
                    {
                        unreadable++;
                        continue;
                    }

                    books.Add(entry);
                }
            }
            catch (ArchiveException ex)
            {
                // What was listed before the damage is kept; the archive is marked incomplete.
                Problem(path, ex.Message);
                state = ArchiveScanState.Incomplete;
            }

            await DiffAsync(archiveId, path, directory, stream, books, cancellationToken);
            await db.Archives.Where(a => a.Id == archiveId).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Fingerprint, fingerprint).SetProperty(a => a.EntryCount, books.Count).SetProperty(a => a.ScanState, state), cancellationToken);
            await writer.FlushCountsAsync(db, cancellationToken);
            archivesRead++;
            Report(path);
        }

        /// <summary>An archive's entries against its rows: kept, moved (a rebuilt archive), changed, new, gone.</summary>
        private async Task DiffAsync(int archiveId, string path, ZipDirectory directory, Stream stream, List<ArchiveEntry> entries, CancellationToken cancellationToken)
        {
            var rows = (await db.BookFiles.Where(f => f.ArchiveId == archiveId)
                    .Select(f => new { f.Id, f.BookId, f.Path, f.Size, f.Crc32, f.Locator, f.Method, f.PackedSize }).ToListAsync(cancellationToken))
                .GroupBy(f => f.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var kept = new HashSet<int>();
            var fresh = new List<ArchiveEntry>();
            var changed = new List<(int FileId, int BookId, ArchiveEntry Entry)>();
            var moved = new List<(int FileId, ArchiveEntry Entry)>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                // A name twice in one zip: the first one is the book (as unzip tools do).
                if (!names.Add(entry.Name))
                    continue;
                if (!rows.TryGetValue(entry.Name, out var row))
                {
                    fresh.Add(entry);
                    continue;
                }

                kept.Add(row.Id);
                if (row.Size != entry.Size || row.Crc32 != entry.Crc)
                    changed.Add((row.Id, row.BookId, entry));
                else if (row.Locator != entry.Locator || row.Method != entry.Method || row.PackedSize != entry.Packed)
                    moved.Add((row.Id, entry));
                else
                    unchanged++;
            }

            filesSeen += names.Count;
            var gone = rows.Values.Where(r => !kept.Contains(r.Id)).ToList();
            foreach (var chunk in gone.Chunk(BatchSize))
            {
                var ids = chunk.Select(r => r.Id).ToList();
                await db.BookFiles.Where(f => ids.Contains(f.Id)).ExecuteDeleteAsync(cancellationToken);
                await writer.RemoveEmptyBooksAsync(db, chunk.Select(r => r.BookId).ToList(), cancellationToken);
            }

            foreach (var chunk in moved.Chunk(BatchSize))
            {
                foreach (var (fileId, entry) in chunk)
                {
                    var file = new BookFile { Id = fileId };
                    db.BookFiles.Attach(file);
                    file.Locator = entry.Locator;
                    file.Method = (short)entry.Method;
                    file.PackedSize = entry.Packed;
                }

                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
                unchanged += chunk.Length;
            }

            // Read in the order the entries lie in the file: the disk (or the NAS) reads forward.
            var reads = fresh.Select(e => (FileId: 0, BookId: 0, Entry: e)).Concat(changed).OrderBy(r => r.Entry.Locator).ToList();
            foreach (var chunk in reads.Chunk(BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var added = new List<ScannedFile>();
                var refreshed = new List<(int BookId, string Path, BookMetadata? Metadata)>();
                foreach (var (fileId, bookId, entry) in chunk)
                {
                    current = $"{path}/{entry.Name}";
                    var format = BookFormats.Of(entry.Name)!;
                    var metadata = Read($"{path}: {Printable(entry.Name)}", format, () => directory.OpenEntry(stream, entry, leaveOpen: true), entry.Size, cancellationToken);
                    if (fileId == 0)
                    {
                        added.Add(new ScannedFile(new BookFile
                        {
                            ArchiveId = archiveId, Path = entry.Name, PathKey = PathKeys.Of(entry.Name), Format = format, Size = entry.Size, Crc32 = entry.Crc,
                            Modified = entry.Time is { } t ? Moment(DateTime.SpecifyKind(t, DateTimeKind.Local).ToUniversalTime()) : null,
                            Locator = entry.Locator, Method = (short)entry.Method, PackedSize = entry.Packed,
                        }, metadata, () => directory.OpenEntry(stream, entry, leaveOpen: true)));
                        continue;
                    }

                    await db.BookFiles.Where(f => f.Id == fileId).ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.Size, entry.Size).SetProperty(f => f.Crc32, (long?)entry.Crc).SetProperty(f => f.Locator, (long?)entry.Locator)
                        .SetProperty(f => f.Method, (short?)entry.Method).SetProperty(f => f.PackedSize, (long?)entry.Packed).SetProperty(f => f.ContentHash, (byte[]?)null), cancellationToken);
                    refreshed.Add((bookId, entry.Name, metadata));
                    changedFiles++;
                }

                await writer.AddAsync(db, added, cancellationToken);
                await writer.RefreshAsync(db, refreshed, cancellationToken);
                Report(current);
            }
        }

        private async Task<int> SaveArchiveAsync(LibraryRoot root, Archive? row, string path, long key, long size, DateTime modified, long? fingerprint, int entries,
            ArchiveScanState state, Bits seen, CancellationToken cancellationToken)
        {
            if (row is not null)
            {
                await db.Archives.Where(a => a.Id == row.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Size, size).SetProperty(a => a.Modified, modified).SetProperty(a => a.Fingerprint, fingerprint)
                    .SetProperty(a => a.EntryCount, entries).SetProperty(a => a.ScanState, state), cancellationToken);
                return row.Id;
            }

            var archive = new Archive
            {
                TenantId = root.TenantId, LibraryId = root.LibraryId, RootId = root.Id, Path = path, PathKey = key, Size = size, Modified = modified,
                Fingerprint = fingerprint, EntryCount = entries, ScanState = state,
            };
            db.Archives.Add(archive);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            seen.Set(archive.Id);
            return archive.Id;
        }

        /// <summary>Loose files of the root this scan didn't see: removed, and their books when they have no other file.</summary>
        private async Task SweepLooseAsync(LibraryRoot root, Bits seen, CancellationToken cancellationToken)
        {
            long afterKey = long.MinValue;
            var afterId = 0;
            while (true)
            {
                var (key, id) = (afterKey, afterId);
                var page = await db.BookFiles.Where(f => f.RootId == root.Id && f.PathKey >= key && (f.PathKey > key || f.Id > id))
                    .OrderBy(f => f.PathKey).ThenBy(f => f.Id).Take(BatchSize).Select(f => new { f.Id, f.BookId, f.PathKey }).ToListAsync(cancellationToken);
                if (page.Count == 0)
                    return;
                var gone = page.Where(f => f.Id <= seen.Last && !seen.Get(f.Id)).ToList();
                if (gone.Count > 0)
                {
                    var ids = gone.Select(f => f.Id).ToList();
                    await db.BookFiles.Where(f => ids.Contains(f.Id)).ExecuteDeleteAsync(cancellationToken);
                    await writer.RemoveEmptyBooksAsync(db, gone.Select(f => f.BookId).ToList(), cancellationToken);
                }

                (afterKey, afterId) = (page[^1].PathKey, page[^1].Id);
            }
        }

        /// <summary>Archives of the root this scan didn't see: removed with their files, and the books left without one.</summary>
        private async Task SweepArchivesAsync(LibraryRoot root, Bits seen, CancellationToken cancellationToken)
        {
            var gone = (await db.Archives.Where(a => a.RootId == root.Id && a.Id <= seen.Last).Select(a => a.Id).ToListAsync(cancellationToken))
                .Where(id => !seen.Get(id)).ToList();
            foreach (var archiveId in gone)
            {
                var afterId = 0;
                while (true)
                {
                    var after = afterId;
                    var page = await db.BookFiles.Where(f => f.ArchiveId == archiveId && f.Id > after).OrderBy(f => f.Id).Take(BatchSize)
                        .Select(f => new { f.Id, f.BookId }).ToListAsync(cancellationToken);
                    if (page.Count == 0)
                        break;
                    var ids = page.Select(f => f.Id).ToList();
                    await db.BookFiles.Where(f => ids.Contains(f.Id)).ExecuteDeleteAsync(cancellationToken);
                    await writer.RemoveEmptyBooksAsync(db, page.Select(f => f.BookId).ToList(), cancellationToken);
                    afterId = page[^1].Id;
                }

                await db.Archives.Where(a => a.Id == archiveId).ExecuteDeleteAsync(cancellationToken);
            }
        }

        private BookMetadata? Read(string what, string format, Func<Stream> open, long size, CancellationToken cancellationToken)
        {
            try
            {
                return scanner.Reader.Read(format, open, size, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A broken book is still a book: kept with its file name, the problem listed.
                Problem(what, ex.Message);
                scanner.Logger.LogDebug(ex, "Metadata of {Book} not read", what);
                return null;
            }
        }

        private void Problem(string what, string message)
        {
            if (problems.Count < MaxProblems)
                problems.Add($"{what}: {message}");
        }

        private void Report(string? item) =>
            progress?.Report(new ScanProgress(filesSeen, archivesRead + archivesSkipped, writer.Added, changedFiles, writer.Removed, item));

        private static FileStream Open(string path) =>
            new(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read | FileShare.Delete, BufferSize = 64 * 1024 });

        /// <summary>A time as both databases keep it (PostgreSQL: microseconds), so an unchanged file compares equal.</summary>
        private static DateTime Moment(DateTime utc) => new(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);

        private static string Printable(string name) => new(name.Select(c => char.IsControl(c) ? '?' : c).ToArray());
    }

    /// <summary>One bit per row id up to the last one that existed when the scan began (rows added since are its own).</summary>
    private sealed class Bits(int last)
    {
        private readonly System.Collections.BitArray bits = new(last + 1);

        public int Last => last;
        public void Set(int id)
        {
            if (id <= last)
                bits[id] = true;
        }

        public bool Get(int id) => id <= last && bits[id];
    }
}
