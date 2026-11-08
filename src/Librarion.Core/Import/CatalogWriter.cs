using Librarion.Core.Catalog;
using Librarion.Core.Covers;
using Librarion.Core.Covers.Generated;
using Librarion.Core.Data;
using Librarion.Core.Formats;
using Librarion.Core.Merging;
using Librarion.Core.Reading;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Import;

/// <summary>A book file found by a scan, with what its head says (null: nothing, the file name is its title).</summary>
/// <param name="Open">Its bytes again, while the scan holds its archive open: hashed when its size and CRC match another file.</param>
public sealed record ScannedFile(BookFile File, BookMetadata? Metadata, Func<Stream>? Open = null);

/// <summary>
/// Writes what a scan found into the catalog, a batch at a time: new books with their files, field values and plain
/// tags (<see cref="ImportTagResolver"/>), books whose file changed read again (only their <see cref="TagSource.Imported"/>
/// links are replaced: manual and rule links stay), books left without a file removed. <see cref="TagCount"/> and
/// <see cref="Library.BookCount"/> move by the differences, never by a count over the catalog
/// (agents/docs/development/scale.md).
/// </summary>
public sealed class CatalogWriter(int tenantId, int libraryId, ImportTagResolver resolver, TimeProvider time)
{
    private const int MaxIdentifiers = 20;
    private const int MaxProperties = 50;
    private static readonly string[] BookColumns =
    [
        "Id", "TenantId", "LibraryId", "Title", "SortTitle", "SearchText", "SortAuthor", "SeriesId", "SeriesNumber", "Description", "Rating", "Added", "Updated", "CoverSource",
        "GeneratedTemplate", "GeneratedVariant", "CoverState", "WorkKey", "MetadataSource", "ImportHash", "Edited",
    ];

    private static object?[] BookRow(Book b) =>
    [
        b.Id, b.TenantId, b.LibraryId, b.Title, b.SortTitle, b.SearchText, b.SortAuthor, b.SeriesId, b.SeriesNumber, b.Description, b.Rating, b.Added, b.Updated, (int)b.CoverSource,
        b.GeneratedTemplate, b.GeneratedVariant, (int)b.CoverState, b.WorkKey, (byte)b.MetadataSource, b.ImportHash, (byte)b.Edited,
    ];

    private static readonly string[] FileColumns =
        ["TenantId", "LibraryId", "BookId", "RootId", "ArchiveId", "FolderId", "Path", "PathKey", "Format", "Size", "Crc32", "Modified", "Locator", "Method", "PackedSize"];
    private readonly TagCountChanges counts = new();
    private readonly Folders folders = new(tenantId, libraryId);

    /// <summary>The library's default tags (stage 6 chunk 8c): its own (key 0) and each folder's; null until read, empty when none.</summary>
    private Dictionary<int, int[]>? defaults;
    private readonly TagAncestors ancestors = new();
    private int bookDelta;

    /// <summary>The books whose duplicate keys this writer may have changed, for the end of the run (<see cref="DuplicateIndex.AfterRunAsync"/>).</summary>
    public TouchedBooks Touched { get; } = new();

    /// <summary>
    /// A new file with the same bytes as one of the library becomes another file of that book (D19; the tenant setting
    /// <c>import.mergeSameFiles</c>, on by default). Off: it is a book of its own, a duplicate of the same file.
    /// </summary>
    public bool MergeCopies { get; init; } = true;

    /// <summary>The tenant's cover templates turned off (stage 6 chunk 10b), read once before the first new book; null until then.</summary>
    private int? templatesOff;

    public int Added { get; private set; }
    public int Refreshed { get; private set; }
    public int Removed { get; private set; }

    /// <summary>Files that came in as another copy of a book already there (the same bytes), not as a book.</summary>
    public int Copies { get; private set; }

    /// <summary>The library's folders, as this writer's files find them (<see cref="Import.Folders"/>).</summary>
    public Folders Folders => folders;

    /// <summary>New books, one per file; a file with the same bytes as one of the library becomes another file of its book (D19).</summary>
    public async Task AddAsync(LibrarionDbContext db, IReadOnlyList<ScannedFile> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return;
        await folders.AssignAsync(db, files.Select(f => f.File), cancellationToken);
        foreach (var file in files)
            folders.Count(file.File.FolderId, 1);
        await DefaultsAsync(db, files.Select(f => f.File.FolderId), cancellationToken);
        templatesOff ??= await TemplatesOffAsync(db, cancellationToken);
        var copies = MergeCopies ? await CopiesAsync(db, files, cancellationToken) : new (int? BookId, int? Earlier)[files.Count];
        await resolver.ResolveAsync(db, files.Where((_, i) => copies[i] == default).SelectMany(f => Names(f.Metadata)), cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var books = new Book?[files.Count];
        var added = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            file.File.TenantId = tenantId;
            file.File.LibraryId = libraryId;
            if (copies[i].BookId is { } bookId)
            {
                file.File.BookId = bookId;
                db.BookFiles.Add(file.File);
                continue;
            }

            if (copies[i].Earlier is { } earlier)
            {
                books[earlier]!.Files.Add(file.File);
                continue;
            }

            var book = books[i] = new Book { TenantId = tenantId, LibraryId = libraryId, Added = now, Updated = now };
            book.Files.Add(file.File);
            Apply(book, file.File.Path, file.Metadata, new HashSet<int>(), out var links);
            GiveDefaults(links, [file.File.FolderId]);
            foreach (var link in links)
                book.Tags.Add(link);
            counts.Add(libraryId, [], links.Select(l => l.TagId));

            db.Books.Add(book);
            added++;
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        Touched.Add(books.Where(b => b is not null).Select(b => b!.Id).Concat(copies.Where(c => c.BookId is not null).Select(c => c.BookId!.Value)));
        Added += added;
        Copies += files.Count - added;
        bookDelta += added;
    }

    /// <summary>
    /// For each file, the book it is a copy of: one of the library's (<c>BookId</c>) or an earlier file of the batch
    /// (<c>Earlier</c>); default for a new book. Candidates share size and CRC-32 (the index
    /// <c>BookFiles (TenantId, Size, Crc32)</c>; zip entries give both for free), and only then are both hashed (SHA-256,
    /// kept in <see cref="BookFile.ContentHash"/>) and compared. Loose files have no CRC at import: never matched here.
    /// </summary>
    private async Task<(int? BookId, int? Earlier)[]> CopiesAsync(LibrarionDbContext db, IReadOnlyList<ScannedFile> files, CancellationToken cancellationToken)
    {
        var result = new (int? BookId, int? Earlier)[files.Count];
        var candidates = Enumerable.Range(0, files.Count).Where(i => files[i].File.Crc32 is not null && files[i].Open is not null).ToList();
        if (candidates.Count == 0)
            return result;
        var sizes = candidates.Select(i => files[i].File.Size).Distinct().ToList();
        var crcs = candidates.Select(i => files[i].File.Crc32).Distinct().ToList();
        var stored = (await BookDownloads.StoredAsync(db.BookFiles.Where(f => f.LibraryId == libraryId && f.MissingSince == null && sizes.Contains(f.Size) && crcs.Contains(f.Crc32)), cancellationToken))
            .GroupBy(f => (f.Target.Size, (long?)f.Target.Crc)).ToDictionary(g => g.Key, g => g.ToList());
        // Files of this batch that became books, by size and CRC: a later copy in the batch joins one of them.
        var earlier = new Dictionary<(long, long?), List<int>>();

        foreach (var i in candidates)
        {
            var file = files[i].File;
            var key = (file.Size, file.Crc32);
            var others = stored.GetValueOrDefault(key);
            var mates = earlier.GetValueOrDefault(key);
            if ((others is not null || mates is not null) && (file.ContentHash = Hash(files[i].Open!)) is { } hash)
            {
                if (await SameAsAsync(db, hash, others, cancellationToken) is { } bookId)
                {
                    result[i] = (bookId, null);
                    continue;
                }

                if (mates?.FindIndex(j => (files[j].File.ContentHash ??= Hash(files[j].Open!))?.AsSpan().SequenceEqual(hash) == true) is >= 0 and var found)
                {
                    result[i] = (null, mates[found]);
                    continue;
                }
            }

            if (mates is null)
                earlier[key] = mates = [];
            mates.Add(i);
        }

        return result;
    }

    /// <summary>The book of a stored file with these bytes; a stored file's hash is computed and kept the first time it is needed.</summary>
    private static async Task<int?> SameAsAsync(LibrarionDbContext db, byte[] hash, List<StoredBookFile>? others, CancellationToken cancellationToken)
    {
        for (var at = 0; at < (others?.Count ?? 0); at++)
        {
            var other = others![at];
            if (other.ContentHash is null)
            {
                var (content, _) = BookDownloads.Open(other.Target);
                if (content is null || Hash(() => content.Stream) is not { } computed)
                    continue;
                others[at] = other = other with { ContentHash = computed };
                var fileId = other.Target.FileId;
                await db.BookFiles.Where(f => f.Id == fileId).ExecuteUpdateAsync(s => s.SetProperty(f => f.ContentHash, computed), cancellationToken);
            }

            if (other.ContentHash.AsSpan().SequenceEqual(hash))
                return other.BookId;
        }

        return null;
    }

    /// <summary>SHA-256 of the bytes; null when they can't be read (a damaged entry is never called a copy).</summary>
    private static byte[]? Hash(Func<Stream> open)
    {
        try
        {
            using var stream = open();
            return System.Security.Cryptography.SHA256.HashData(stream);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Books whose file changed, read again: title, description, identifiers and imported links replaced. A book whose
    /// metadata came from an import keeps it (agents/docs/features/imports.md): only its file changed.
    /// </summary>
    public async Task RefreshAsync(LibrarionDbContext db, IReadOnlyList<(int BookId, string Path, BookMetadata? Metadata)> changed, CancellationToken cancellationToken)
    {
        if (changed.Count == 0)
            return;
        var all = changed.Select(c => c.BookId).Distinct().ToList();
        var books = await db.Books.Where(b => all.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var book in books.Values.Where(b => b.MetadataSource != MetadataSource.Heads))
            book.Updated = now;
        var heads = changed.Where(c => books.TryGetValue(c.BookId, out var b) && b.MetadataSource == MetadataSource.Heads)
            .Select(c => (c.BookId, c.Path, c.Metadata, (Action<Book>?)null)).ToList();
        await ReplaceAsync(db, heads, books, cancellationToken);
        Refreshed += changed.Count;
    }

    /// <summary>
    /// Of each book of <paramref name="changed"/> (tracked in <paramref name="books"/>): the columns, identifiers and
    /// <see cref="TagSource.Imported"/> links replaced from the metadata (manual and rule links stay), counts moved by
    /// the differences, everything saved.
    /// </summary>
    private async Task ReplaceAsync(LibrarionDbContext db, IReadOnlyList<(int BookId, string Path, BookMetadata? Metadata, Action<Book>? Then)> changed,
        Dictionary<int, Book> books, CancellationToken cancellationToken)
    {
        if (changed.Count == 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return;
        }

        await resolver.ResolveAsync(db, changed.SelectMany(c => Names(c.Metadata)), cancellationToken);
        var ids = changed.Select(c => c.BookId).ToList();
        await db.BookIdentifiers.Where(i => ids.Contains(i.BookId)).ExecuteDeleteAsync(cancellationToken);
        await db.BookProperties.Where(p => ids.Contains(p.BookId)).ExecuteDeleteAsync(cancellationToken);
        var links = (await db.BookTags.Where(l => ids.Contains(l.BookId)).Select(l => new { l.BookId, l.TagId, l.Source }).ToListAsync(cancellationToken))
            .GroupBy(l => l.BookId).ToDictionary(g => g.Key, g => g.ToList());
        await db.BookTags.Where(l => ids.Contains(l.BookId) && l.Source == TagSource.Imported).ExecuteDeleteAsync(cancellationToken);
        // What users took off (a removed imported tag stays off) and the books with links of their own, whose sort
        // columns follow those links too (stage 6, D-6.3).
        var suppressed = (await db.BookTagSuppressions.Where(s => ids.Contains(s.BookId)).Select(s => new { s.BookId, s.TagId }).ToListAsync(cancellationToken))
            .ToLookup(s => s.BookId, s => s.TagId);
        var edited = new List<int>();

        var now = time.GetUtcNow().UtcDateTime;
        foreach (var (bookId, path, metadata, then) in changed)
        {
            if (!books.TryGetValue(bookId, out var book))
                continue;
            var old = links.GetValueOrDefault(bookId) ?? [];
            var kept = old.Where(l => l.Source != TagSource.Imported).Select(l => l.TagId).ToHashSet();
            var before = old.Select(l => l.TagId).ToHashSet();
            Apply(book, path, metadata, kept, out var fresh, suppressed[bookId].ToHashSet());
            if (old.Any(l => l.Source == TagSource.Manual))
                edited.Add(bookId);
            db.BookTags.AddRange(fresh.Select(l => { l.BookId = bookId; return l; }));
            counts.Add(libraryId, before, kept.Concat(fresh.Select(l => l.TagId)));
            book.Updated = now;
            then?.Invoke(book);
        }

        Touched.Add(changed.Select(c => c.BookId));

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        if (edited.Count > 0)
            await TagLinks.SortColumnsAsync(db, edited, cancellationToken);
    }

    /// <summary>A record of an import, for <see cref="ImportAsync"/>.</summary>
    /// <param name="BookId">The book the record matched (by a file of it, or by its source id); null for a new book.</param>
    /// <param name="Files">Files of the record not in the catalog yet: added to the book (a new book has at least one).</param>
    /// <param name="Path">A path of the book's files: its title when the record has none.</param>
    /// <param name="Cover">The source has the book's cover (<see cref="CoverSource.Calibre"/>).</param>
    public sealed record ImportedBook(int? BookId, IReadOnlyList<BookFile> Files, string Path, BookMetadata Metadata, MetadataSource Owner, long Hash, bool Cover = false);

    /// <summary>
    /// Records of an import (agents/docs/features/imports.md): a matched book takes the record's metadata (its imported
    /// links replaced, manual ones kept) and its new files, and comes back if it was missing; a record without a book
    /// becomes one with all its files. The book is owned by the source afterwards (<see cref="Book.MetadataSource"/>).
    /// No copy detection: the source says which files are one book.
    /// </summary>
    public async Task ImportAsync(LibrarionDbContext db, IReadOnlyList<ImportedBook> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
            return;
        var now = time.GetUtcNow().UtcDateTime;
        var fresh = records.Where(r => r.BookId is null && r.Files.Count > 0).ToList();
        if (fresh.Count > 0)
        {
            using (StepTimes.Step("write: tag names"))
                await resolver.ResolveAsync(db, fresh.SelectMany(r => Names(r.Metadata)), cancellationToken);
            using (StepTimes.Step("write: folders"))
                await folders.AssignAsync(db, fresh.SelectMany(r => r.Files), cancellationToken);
            foreach (var file in fresh.SelectMany(r => r.Files))
                folders.Count(file.FolderId, 1);
            using (StepTimes.Step("write: default tags"))
                await DefaultsAsync(db, fresh.SelectMany(r => r.Files).Select(f => f.FolderId), cancellationToken);
            templatesOff ??= await TemplatesOffAsync(db, cancellationToken);
            var planning = StepTimes.Step("write: books planned");
            // The books through EF (their ids come back), everything hanging on them in bulk: EF's per-row cost was
            // most of an import's time (agents/docs/features/imports.md#scale).
            var made = new List<(Book Book, ImportedBook Record, List<BookTag> Links, List<BookIdentifier> Ids, List<BookProperty> Properties)>(fresh.Count);
            foreach (var record in fresh)
            {
                var book = new Book { TenantId = tenantId, LibraryId = libraryId, Added = now, Updated = now, MetadataSource = record.Owner, ImportHash = record.Hash };
                Apply(book, record.Path, record.Metadata, new HashSet<int>(), out var links);
                GiveDefaults(links, record.Files.Select(f => f.FolderId));
                counts.Add(libraryId, [], links.Select(l => l.TagId));
                SourceCover(book, record);
                made.Add((book, record, links, book.Identifiers, book.Properties));
                book.Identifiers = [];
                book.Properties = [];
            }

            planning.Dispose();
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                var inserting = StepTimes.Step("write: books inserted");
                if (await BulkRows.ReserveIdsAsync(db, "Books", made.Count, cancellationToken) is { } reserved)
                {
                    // PostgreSQL: ids from the table's sequence (safe beside other writers), the books copied too.
                    for (var i = 0; i < made.Count; i++)
                        made[i].Book.Id = checked((int)reserved[i]);
                    await BulkRows.InsertAsync(db, "Books", BookColumns, made.Select(m => BookRow(m.Book)).ToList(), cancellationToken);
                }
                else
                {
                    db.Books.AddRange(made.Select(m => m.Book));
                    await db.SaveChangesAsync(cancellationToken);
                }

                inserting.Dispose();
                inserting = StepTimes.Step("write: files inserted");
                await BulkRows.InsertAsync(db, "BookFiles", FileColumns, made.SelectMany(m => m.Record.Files.Select(f => new object?[]
                {
                    tenantId, libraryId, m.Book.Id, f.RootId, f.ArchiveId, f.FolderId, f.Path, f.PathKey, f.Format, f.Size, f.Crc32, f.Modified, f.Locator, f.Method, f.PackedSize,
                })).ToList(), cancellationToken);
                inserting.Dispose();
                inserting = StepTimes.Step("write: links inserted");
                await BulkRows.InsertAsync(db, "BookTags", ["TenantId", "BookId", "TagId", "Source", "Ordinal", "Number"],
                    made.SelectMany(m => m.Links.Select(l => new object?[] { tenantId, m.Book.Id, l.TagId, (byte)l.Source, l.Ordinal, l.Number })).ToList(), cancellationToken);
                inserting.Dispose();
                inserting = StepTimes.Step("write: identifiers and properties inserted");
                await BulkRows.InsertAsync(db, "BookIdentifiers", ["BookId", "TenantId", "Scheme", "Value"],
                    made.SelectMany(m => m.Ids.Select(i => new object?[] { m.Book.Id, tenantId, i.Scheme, i.Value })).ToList(), cancellationToken);
                await BulkRows.InsertAsync(db, "BookProperties", ["BookId", "TenantId", "Name", "Value"],
                    made.SelectMany(m => m.Properties.Select(p => new object?[] { m.Book.Id, tenantId, p.Name, p.Value })).ToList(), cancellationToken);
                inserting.Dispose();
                using (StepTimes.Step("write: commit"))
                    await transaction.CommitAsync(cancellationToken);
            }

            db.ChangeTracker.Clear();
            Touched.Add(made.Select(m => m.Book.Id));
            Added += fresh.Count;
            bookDelta += fresh.Count;
        }

        var matched = records.Where(r => r.BookId is not null).GroupBy(r => r.BookId!.Value).Select(g => g.First()).ToList();
        if (matched.Count == 0)
            return;
        var ids = matched.Select(r => r.BookId!.Value).ToList();
        // Before the books are tracked: making folders clears the tracker.
        await folders.AssignAsync(db, matched.SelectMany(r => r.Files), cancellationToken);
        foreach (var file in matched.SelectMany(r => r.Files))
            folders.Count(file.FolderId, 1);
        var books = await db.Books.Where(b => ids.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);
        foreach (var record in matched)
            foreach (var file in record.Files)
            {
                file.TenantId = tenantId;
                file.LibraryId = libraryId;
                file.BookId = record.BookId!.Value;
                db.BookFiles.Add(file);
            }

        await ReplaceAsync(db, matched.Select<ImportedBook, (int, string, BookMetadata?, Action<Book>?)>(r => (r.BookId!.Value, r.Path, r.Metadata,
            book => { book.MetadataSource = r.Owner; book.ImportHash = r.Hash; SourceCover(book, r); })).ToList(), books, cancellationToken);
        Refreshed += matched.Count;
        await ReturnAsync(db, ids, cancellationToken);
    }

    /// <summary>Of <paramref name="bookIds"/>, the books that have no file any more are removed with their links.</summary>
    /// <param name="undoAdd">The books were added by this run (a moved file relinked to its old book): they are taken off
    /// <see cref="Added"/> instead of counted as removed.</param>
    public async Task RemoveEmptyBooksAsync(LibrarionDbContext db, IReadOnlyCollection<int> bookIds, CancellationToken cancellationToken, bool undoAdd = false)
    {
        // Called after files of these books went: those left lost a file key.
        Touched.Add(bookIds);
        foreach (var chunk in bookIds.Distinct().Chunk(1000))
        {
            var empty = await db.Books.Where(b => chunk.Contains(b.Id) && !db.BookFiles.Any(f => f.BookId == b.Id))
                .Select(b => new { b.Id, Missing = b.MissingSince != null }).ToListAsync(cancellationToken);
            if (empty.Count == 0)
                continue;
            // A missing book's counts went down when it went missing.
            var counted = empty.Where(b => !b.Missing).Select(b => b.Id).ToList();
            await CountAsync(db, counted, gone: true, cancellationToken);
            var ids = empty.Select(b => b.Id).ToList();
            await db.Books.Where(b => ids.Contains(b.Id)).ExecuteDeleteAsync(cancellationToken);
            Touched.Shrunk = true;
            if (undoAdd)
                Added -= empty.Count;
            else
                Removed += empty.Count;
            bookDelta -= counted.Count;
        }
    }

    /// <summary>Books that became missing in this run (every file of theirs missing).</summary>
    public int Missing { get; private set; }

    /// <summary>Missing books that have a file again (found where it was, or moved and relinked).</summary>
    public int Returned { get; private set; }

    /// <summary>
    /// Of <paramref name="bookIds"/>, the books none of whose files is present any more become missing (kept, hidden from
    /// lists, their counts down) until a file comes back or a user accepts their removal
    /// (agents/docs/features/missing-books.md).
    /// </summary>
    public async Task MarkMissingAsync(LibrarionDbContext db, IReadOnlyCollection<int> bookIds, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var chunk in bookIds.Distinct().Chunk(1000))
        {
            var gone = await db.Books.Where(b => chunk.Contains(b.Id) && b.MissingSince == null && !db.BookFiles.Any(f => f.BookId == b.Id && f.MissingSince == null))
                .Select(b => b.Id).ToListAsync(cancellationToken);
            if (gone.Count == 0)
                continue;
            await CountAsync(db, gone, gone: true, cancellationToken);
            await db.Books.Where(b => gone.Contains(b.Id)).ExecuteUpdateAsync(s => s.SetProperty(b => b.MissingSince, now), cancellationToken);
            Missing += gone.Count;
            bookDelta -= gone.Count;
        }
    }

    /// <summary>Of <paramref name="bookIds"/>, the missing books that have a present file again come back: listed and counted.</summary>
    public async Task ReturnAsync(LibrarionDbContext db, IReadOnlyCollection<int> bookIds, CancellationToken cancellationToken)
    {
        foreach (var chunk in bookIds.Distinct().Chunk(1000))
        {
            var back = await db.Books.Where(b => chunk.Contains(b.Id) && b.MissingSince != null && db.BookFiles.Any(f => f.BookId == b.Id && f.MissingSince == null))
                .Select(b => b.Id).ToListAsync(cancellationToken);
            if (back.Count == 0)
                continue;
            await CountAsync(db, back, gone: false, cancellationToken);
            await db.Books.Where(b => back.Contains(b.Id)).ExecuteUpdateAsync(s => s.SetProperty(b => b.MissingSince, (DateTime?)null), cancellationToken);
            // A relinked file may have come back with other bytes.
            Touched.Add(back);
            Returned += back.Count;
            bookDelta += back.Count;
        }
    }

    /// <summary>The counts moved since the last flush, written: one upsert per thousand touched tags, one update of the library.</summary>
    public async Task FlushCountsAsync(LibrarionDbContext db, CancellationToken cancellationToken)
    {
        using (StepTimes.Step("counts: tags"))
            await counts.WriteAsync(db, tenantId, ancestors, cancellationToken);
        using (StepTimes.Step("counts: folders"))
            await folders.FlushAsync(db, cancellationToken);
        if (bookDelta != 0)
        {
            var delta = bookDelta;
            await db.Libraries.Where(l => l.Id == libraryId).ExecuteUpdateAsync(s => s.SetProperty(l => l.BookCount, l => l.BookCount + delta), cancellationToken);
        }

        bookDelta = 0;
    }

    /// <summary>
    /// The library's default tags read once (a few rows), and the folders above <paramref name="folderIds"/> into the
    /// folder cache when there are folder defaults: nothing more when the library has none.
    /// </summary>
    private async Task DefaultsAsync(LibrarionDbContext db, IEnumerable<int> folderIds, CancellationToken cancellationToken)
    {
        defaults ??= (await db.DefaultTags.Where(d => d.LibraryId == libraryId).Select(d => new { d.FolderId, d.TagId }).ToListAsync(cancellationToken))
            .GroupBy(d => d.FolderId ?? 0).ToDictionary(g => g.Key, g => g.Select(d => d.TagId).ToArray());
        if (defaults.Keys.Any(k => k != 0))
            await folders.ParentsAsync(db, folderIds, cancellationToken);
    }

    private async Task<int> TemplatesOffAsync(LibrarionDbContext db, CancellationToken cancellationToken) =>
        await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.CoverTemplatesOff).FirstOrDefaultAsync(cancellationToken);

    /// <summary>The default tags of the library and of <paramref name="folderIds"/>' folders and those above them.</summary>
    private HashSet<int> DefaultsOf(IEnumerable<int> folderIds)
    {
        // Read by DefaultsAsync first, for the same books.
        var known = defaults ?? throw new InvalidOperationException("The default tags weren't read before they were given.");
        var given = new HashSet<int>(known.GetValueOrDefault(0) ?? []);
        if (known.Keys.Any(k => k != 0))
            foreach (var folder in folderIds.Distinct())
                foreach (var id in folders.Chain(folder))
                    if (known.TryGetValue(id, out var tags))
                        given.UnionWith(tags);
        return given;
    }

    /// <summary>A new book's default tags as rule links (those its metadata gave already left as they are).</summary>
    private void GiveDefaults(List<BookTag> links, IEnumerable<int> folderIds)
    {
        if (defaults is not { Count: > 0 })
            return;
        var have = links.Select(l => l.TagId).ToHashSet();
        foreach (var tagId in DefaultsOf(folderIds))
            if (have.Add(tagId))
                links.Add(new BookTag { TenantId = tenantId, TagId = tagId, Source = TagSource.Rule });
    }

    /// <summary>
    /// Books whose file moved to another folder (a relink): the default tags the new place gives and the old one didn't,
    /// as rule links, unless the book has the tag or had it taken off (SFE: a rename gives nothing).
    /// </summary>
    public async Task MovedAsync(LibrarionDbContext db, IReadOnlyList<(int BookId, int From, int To)> moves, CancellationToken cancellationToken)
    {
        if (moves.Count == 0)
            return;
        await DefaultsAsync(db, moves.SelectMany(m => new[] { m.From, m.To }), cancellationToken);
        if (defaults is not { Count: > 0 })
            return;
        foreach (var (bookId, from, to) in moves)
        {
            var gained = DefaultsOf([to]);
            gained.ExceptWith(DefaultsOf([from]));
            if (gained.Count == 0)
                continue;
            var before = await db.BookTags.Where(l => l.BookId == bookId).Select(l => l.TagId).Distinct().ToListAsync(cancellationToken);
            var off = await db.BookTagSuppressions.Where(s => s.BookId == bookId).Select(s => s.TagId).ToListAsync(cancellationToken);
            var fresh = gained.Except(before).Except(off).ToList();
            if (fresh.Count == 0)
                continue;
            db.BookTags.AddRange(fresh.Select(t => new BookTag { TenantId = tenantId, BookId = bookId, TagId = t, Source = TagSource.Rule }));
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            counts.Add(libraryId, before, before.Concat(fresh));
        }
    }

    /// <summary>
    /// Of <paramref name="fileIds"/>, the present ones (<paramref name="present"/>) or the missing ones leave or join
    /// their folders' counts: called before they go missing, are removed, or are found again.
    /// </summary>
    public async Task CountFilesAsync(LibrarionDbContext db, IReadOnlyCollection<int> fileIds, bool present, int delta, CancellationToken cancellationToken)
    {
        foreach (var chunk in fileIds.Chunk(1000))
            foreach (var folder in await db.BookFiles.Where(f => chunk.Contains(f.Id) && (f.MissingSince == null) == present)
                         .GroupBy(f => f.FolderId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
                folders.Count(folder.Key, folder.Count * delta);
    }

    /// <summary><paramref name="books"/>' tags leave the counts (<paramref name="gone"/>) or come back into them.</summary>
    private async Task CountAsync(LibrarionDbContext db, IReadOnlyCollection<int> books, bool gone, CancellationToken cancellationToken)
    {
        var links = await db.BookTags.Where(l => books.Contains(l.BookId)).Select(l => new { l.BookId, l.TagId }).ToListAsync(cancellationToken);
        foreach (var book in links.GroupBy(l => l.BookId))
        {
            var tags = book.Select(l => l.TagId);
            counts.Add(libraryId, gone ? tags : [], gone ? [] : tags);
        }
    }

    private static IEnumerable<ImportTagResolver.Name> Names(BookMetadata? metadata)
    {
        if (metadata is null)
            yield break;
        foreach (var value in metadata.FieldValues)
            yield return new ImportTagResolver.Name(metadata.Source, value.Field, value.Value, value.SortName);
        foreach (var tag in metadata.PlainTags)
            yield return new ImportTagResolver.Name(metadata.Source, null, tag);
    }

    /// <summary>
    /// The book's columns and imported links from its metadata (the file name when it has no title); the columns a user
    /// edited (<see cref="Book.Edited"/>) and the tags taken off (<paramref name="suppressed"/>) left as they are.
    /// </summary>
    private void Apply(Book book, string path, BookMetadata? metadata, IReadOnlySet<int> kept, out List<BookTag> links, IReadOnlySet<int>? suppressed = null)
    {
        var title = metadata?.Title?.Trim() is { Length: > 0 } t ? Cut(t, Book.MaxTitleLength) : null;
        if (book.Edited.HasFlag(BookEdits.Title))
        {
            title = book.Title;
        }
        else
        {
            book.Title = title;
            book.SortTitle = BookTitles.SortKey(metadata?.SortTitle?.Trim() is { Length: > 0 } sortTitle ? sortTitle : title ?? FileTitles.FromPath(path));
            book.SearchText = Search.SearchText.ForBook(title, FileTitles.FromPath(path));
        }

        if (metadata?.Source is ImportTagSourceKind.Calibre or ImportTagSourceKind.Inpx && !book.Edited.HasFlag(BookEdits.Rating))
            book.Rating = metadata.Rating is { } rating ? Math.Min(rating, (byte)10) : null;
        if (!book.Edited.HasFlag(BookEdits.Description))
            book.Description = metadata?.Description?.Trim() is { Length: > 0 } d ? d : null;
        links = [];
        string? firstAuthor = null;
        book.SortAuthor = "";
        book.SeriesId = null;
        book.SeriesNumber = null;
        var seen = new HashSet<int>(kept);
        if (suppressed is not null)
            seen.UnionWith(suppressed);
        if (metadata is not null)
        {
            foreach (var value in metadata.FieldValues)
            {
                if (resolver.Get(new ImportTagResolver.Name(metadata.Source, value.Field, value.Value, value.SortName)) is not { } tagId || !seen.Add(tagId))
                    continue;
                links.Add(new BookTag { TenantId = tenantId, TagId = tagId, Source = TagSource.Imported, Ordinal = value.Ordinal, Number = value.Number });
                if (value.Field == "author" && firstAuthor is null)
                {
                    firstAuthor = value.Value.Trim();
                    book.SortAuthor = Cut(value.SortName?.Trim() is { Length: > 0 } sort ? sort : TagNames.PersonSortName(firstAuthor), Book.MaxSortLength);
                }
                else if (value.Field == "series" && book.SeriesId is null)
                {
                    book.SeriesId = tagId;
                    book.SeriesNumber = value.Number;
                }
            }

            foreach (var tag in metadata.PlainTags)
                if (resolver.Get(new ImportTagResolver.Name(metadata.Source, null, tag)) is { } tagId && seen.Add(tagId))
                    links.Add(new BookTag { TenantId = tenantId, TagId = tagId, Source = TagSource.Imported });

            foreach (var (scheme, value) in metadata.Ids.DistinctBy(i => (i.Scheme, i.Value)).Take(MaxIdentifiers))
                if (value.Trim() is { Length: > 0 and <= BookIdentifier.MaxValueLength } trimmed)
                    book.Identifiers.Add(new BookIdentifier { TenantId = tenantId, Scheme = scheme, Value = trimmed });

            foreach (var (name, value) in (metadata.Properties ?? []).DistinctBy(p => p.Name.Trim()).Take(MaxProperties))
                if (name.Trim() is { Length: > 0 and <= BookProperty.MaxNameLength } key && value.Trim() is { Length: > 0 } text)
                    book.Properties.Add(new BookProperty { TenantId = tenantId, Name = key, Value = Cut(text, BookProperty.MaxValueLength) });
        }

        book.WorkKey = PathKeys.Of(TagNames.Normalize(title ?? FileTitles.FromPath(path)), firstAuthor is null ? "" : TagNames.Normalize(firstAuthor));

        // Every book gets its generated cover's design once (kept across re-reads: the same book keeps its look); the
        // cover inside the book is looked for on first view, and again after a re-read.
        if (book.GeneratedTemplate is null || book.GeneratedVariant is null)
            (book.GeneratedTemplate, book.GeneratedVariant) = GeneratedCovers.Assign(book.WorkKey.Value, templatesOff ?? 0);
        // A cover a user chose (an upload, a style over the book's own) stays (stage 6 chunk 10).
        if (book.CoverSource is CoverSource.Book or CoverSource.Generated && !book.Edited.HasFlag(BookEdits.Cover))
        {
            book.CoverSource = CoverExtractor.HasCovers(BookFormats.Of(path) ?? "") ? CoverSource.Book : CoverSource.Generated;
            book.CoverState = CoverState.Unknown;
        }
    }

    /// <summary>The source's cover when it has one; else the book's own (or generated) again, looked for anew.</summary>
    private static void SourceCover(Book book, ImportedBook record)
    {
        if (book.Edited.HasFlag(BookEdits.Cover))
            return;
        if (record.Cover)
            book.CoverSource = CoverSource.Calibre;
        else if (book.CoverSource == CoverSource.Calibre)
            book.CoverSource = CoverExtractor.HasCovers(BookFormats.Of(record.Path) ?? "") ? CoverSource.Book : CoverSource.Generated;
        if (record.Cover || book.CoverSource is CoverSource.Book or CoverSource.Generated)
            book.CoverState = CoverState.Unknown;
    }

    private static string Cut(string text, int length) => text.Length > length ? text[..length] : text;
}
