using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Import;

/// <summary>A book file found by a scan, with what its head says (null: nothing, the file name is its title).</summary>
public sealed record ScannedFile(BookFile File, BookMetadata? Metadata);

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

    /// <summary>New books, one per file.</summary>
    public async Task AddAsync(LibrarionDbContext db, IReadOnlyList<ScannedFile> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return;
        await resolver.ResolveAsync(db, files.SelectMany(f => Names(f.Metadata)), cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var file in files)
        {
            var book = new Book { TenantId = tenantId, LibraryId = libraryId, Added = now, Updated = now };
            file.File.TenantId = tenantId;
            file.File.LibraryId = libraryId;
            book.Files.Add(file.File);
            Apply(book, file.File.Path, file.Metadata, new HashSet<int>(), out var links);
            foreach (var link in links)
            {
                book.Tags.Add(link);
                Move(link.TagId, +1);
            }

            db.Books.Add(book);
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        Added += files.Count;
        bookDelta += files.Count;
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
    }

    private static string Cut(string text, int length) => text.Length > length ? text[..length] : text;
}
