using Librarion.Core.Catalog;
using Librarion.Core.Covers;
using Librarion.Core.Covers.Generated;
using Librarion.Core.Data;
using Librarion.Core.Formats;
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
    private readonly Dictionary<int, int> tagDeltas = [];
    private int bookDelta;

    public int Added { get; private set; }
    public int Refreshed { get; private set; }
    public int Removed { get; private set; }

    /// <summary>Files that came in as another copy of a book already there (the same bytes), not as a book.</summary>
    public int Copies { get; private set; }

    /// <summary>New books, one per file; a file with the same bytes as one of the library becomes another file of its book (D19).</summary>
    public async Task AddAsync(LibrarionDbContext db, IReadOnlyList<ScannedFile> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return;
        var copies = await CopiesAsync(db, files, cancellationToken);
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
            foreach (var link in links)
            {
                book.Tags.Add(link);
                Move(link.TagId, +1);
            }

            db.Books.Add(book);
            added++;
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
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
        var stored = (await BookDownloads.StoredAsync(db.BookFiles.Where(f => f.LibraryId == libraryId && sizes.Contains(f.Size) && crcs.Contains(f.Crc32)), cancellationToken))
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

    /// <summary>Books whose file changed, read again: title, description, identifiers and imported links replaced.</summary>
    public async Task RefreshAsync(LibrarionDbContext db, IReadOnlyList<(int BookId, string Path, BookMetadata? Metadata)> changed, CancellationToken cancellationToken)
    {
        if (changed.Count == 0)
            return;
        await resolver.ResolveAsync(db, changed.SelectMany(c => Names(c.Metadata)), cancellationToken);
        var ids = changed.Select(c => c.BookId).ToList();
        await db.BookIdentifiers.Where(i => ids.Contains(i.BookId)).ExecuteDeleteAsync(cancellationToken);
        var books = await db.Books.Where(b => ids.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);
        var links = (await db.BookTags.Where(l => ids.Contains(l.BookId)).Select(l => new { l.BookId, l.TagId, l.Source }).ToListAsync(cancellationToken))
            .GroupBy(l => l.BookId).ToDictionary(g => g.Key, g => g.ToList());
        await db.BookTags.Where(l => ids.Contains(l.BookId) && l.Source == TagSource.Imported).ExecuteDeleteAsync(cancellationToken);

        var now = time.GetUtcNow().UtcDateTime;
        foreach (var (bookId, path, metadata) in changed)
        {
            if (!books.TryGetValue(bookId, out var book))
                continue;
            var old = links.GetValueOrDefault(bookId) ?? [];
            var kept = old.Where(l => l.Source != TagSource.Imported).Select(l => l.TagId).ToHashSet();
            var before = old.Select(l => l.TagId).ToHashSet();
            Apply(book, path, metadata, kept, out var fresh);
            db.BookTags.AddRange(fresh.Select(l => { l.BookId = bookId; return l; }));
            var after = kept.Concat(fresh.Select(l => l.TagId)).ToHashSet();
            foreach (var tag in before.Except(after))
                Move(tag, -1);
            foreach (var tag in after.Except(before))
                Move(tag, +1);
            book.Updated = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        Refreshed += changed.Count;
    }

    /// <summary>Of <paramref name="bookIds"/>, the books that have no file any more are removed with their links.</summary>
    public async Task RemoveEmptyBooksAsync(LibrarionDbContext db, IReadOnlyCollection<int> bookIds, CancellationToken cancellationToken)
    {
        foreach (var chunk in bookIds.Distinct().Chunk(1000))
        {
            var empty = await db.Books.Where(b => chunk.Contains(b.Id) && !db.BookFiles.Any(f => f.BookId == b.Id)).Select(b => b.Id).ToListAsync(cancellationToken);
            if (empty.Count == 0)
                continue;
            foreach (var tag in await db.BookTags.Where(l => empty.Contains(l.BookId)).Select(l => new { l.BookId, l.TagId }).Distinct().ToListAsync(cancellationToken))
                Move(tag.TagId, -1);
            await db.Books.Where(b => empty.Contains(b.Id)).ExecuteDeleteAsync(cancellationToken);
            Removed += empty.Count;
            bookDelta -= empty.Count;
        }
    }

    /// <summary>The counts moved since the last flush, written: one upsert per touched tag, one update of the library.</summary>
    public async Task FlushCountsAsync(LibrarionDbContext db, CancellationToken cancellationToken)
    {
        var touched = tagDeltas.Where(d => d.Value != 0).ToList();
        foreach (var chunk in touched.Chunk(1000))
        {
            var ids = chunk.Select(d => d.Key).ToList();
            var rows = await db.TagCounts.Where(c => c.LibraryId == libraryId && ids.Contains(c.TagId)).ToDictionaryAsync(c => c.TagId, cancellationToken);
            foreach (var (tagId, delta) in chunk)
            {
                if (rows.TryGetValue(tagId, out var row))
                    row.BookCount = Math.Max(0, row.BookCount + delta);
                else if (delta > 0)
                    db.TagCounts.Add(new TagCount { TenantId = tenantId, LibraryId = libraryId, TagId = tagId, BookCount = delta });
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        if (touched.Count > 0)
            await db.TagCounts.Where(c => c.LibraryId == libraryId && c.BookCount == 0).ExecuteDeleteAsync(cancellationToken);
        if (bookDelta != 0)
        {
            var delta = bookDelta;
            await db.Libraries.Where(l => l.Id == libraryId).ExecuteUpdateAsync(s => s.SetProperty(l => l.BookCount, l => l.BookCount + delta), cancellationToken);
        }

        tagDeltas.Clear();
        bookDelta = 0;
    }

    private void Move(int tagId, int delta) => tagDeltas[tagId] = tagDeltas.GetValueOrDefault(tagId) + delta;

    private static IEnumerable<ImportTagResolver.Name> Names(BookMetadata? metadata)
    {
        if (metadata is null)
            yield break;
        foreach (var value in metadata.FieldValues)
            yield return new ImportTagResolver.Name(metadata.Source, value.Field, value.Value, value.SortName);
        foreach (var tag in metadata.PlainTags)
            yield return new ImportTagResolver.Name(metadata.Source, null, tag);
    }

    /// <summary>The book's columns and imported links from its metadata (the file name when it has no title).</summary>
    private void Apply(Book book, string path, BookMetadata? metadata, IReadOnlySet<int> kept, out List<BookTag> links)
    {
        var title = metadata?.Title?.Trim() is { Length: > 0 } t ? Cut(t, Book.MaxTitleLength) : null;
        book.Title = title;
        book.SortTitle = BookTitles.SortKey(title ?? FileTitles.FromPath(path));
        book.Description = metadata?.Description?.Trim() is { Length: > 0 } d ? d : null;
        links = [];
        string? firstAuthor = null;
        book.SortAuthor = "";
        book.SeriesId = null;
        book.SeriesNumber = null;
        var seen = new HashSet<int>(kept);
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
        }

        book.WorkKey = PathKeys.Of(TagNames.Normalize(title ?? FileTitles.FromPath(path)), firstAuthor is null ? "" : TagNames.Normalize(firstAuthor));

        // Every book gets its generated cover's design once (kept across re-reads: the same book keeps its look); the
        // cover inside the book is looked for on first view, and again after a re-read.
        if (book.GeneratedTemplate is null || book.GeneratedVariant is null)
            (book.GeneratedTemplate, book.GeneratedVariant) = GeneratedCovers.Assign(book.WorkKey.Value);
        if (book.CoverSource is CoverSource.Book or CoverSource.Generated)
        {
            book.CoverSource = CoverExtractor.HasCovers(BookFormats.Of(path) ?? "") ? CoverSource.Book : CoverSource.Generated;
            book.CoverState = CoverState.Unknown;
        }
    }

    private static string Cut(string text, int length) => text.Length > length ? text[..length] : text;
}
