using System.Linq.Expressions;
using Librarion.Core.Data;
using Librarion.Core.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Librarion.Core.Catalog;

public enum BookSort
{
    Title,
    Author,
    /// <summary>Newest first.</summary>
    Added,
    /// <summary>By the number on the tag's link (a series' order); only with a tag, books without a number left out.</summary>
    Number,
}

/// <param name="LibraryId">One library; null = every library the account browses.</param>
/// <param name="TagId">Only books with this tag or a descendant (a field value's page: an author, a category).</param>
/// <param name="Query">A search in the search language (<see cref="Search.SearchQuery"/>); null or blank: no search.</param>
public sealed record BookPageRequest(int? LibraryId = null, BookSort Sort = BookSort.Title, string? After = null, int Limit = 50, int? TagId = null, string? Query = null);

/// <param name="Title">From the metadata; null when it gave none (<see cref="FileName"/> is shown).</param>
/// <param name="FileName">The first file's name without its book extensions, for a book without a title.</param>
/// <param name="Authors">In order; the first is the one on cards.</param>
public sealed record BookListItem(
    int Id, int LibraryId, string? Title, string? FileName, IReadOnlyList<string> Authors, string? Series, double? SeriesNumber,
    IReadOnlyList<string> Formats, CoverSource CoverSource);

/// <param name="Field">The field's key; null for a plain tag.</param>
public sealed record BookTagItem(int Id, string? Field, string Name, double? Number);

/// <param name="Name">The file's own name (an archive entry's, not the archive's).</param>
/// <param name="Archive">The name of the ZIP holding it; null for a loose file.</param>
/// <param name="Missing">Not where the last scan found it: listed, not downloadable (agents/docs/features/missing-books.md).</param>
public sealed record BookFileItem(int Id, string Format, long Size, string Name, bool InArchive, string? Archive = null, bool Missing = false);

/// <param name="MergedIntoId">Set when the book was merged into another: the client goes there.</param>
/// <param name="Properties">Imported values that are neither tags nor columns (Calibre custom columns), by name.</param>
/// <param name="CanDownload">Whether the caller may download in the book's library (set by the route).</param>
public sealed record BookDetail(
    int Id, int LibraryId, string? Title, string? FileName, string? Description, byte? Rating, DateTime Added, CoverSource CoverSource,
    IReadOnlyList<BookTagItem> Tags, IReadOnlyList<BookFileItem> Files, IReadOnlyList<KeyValuePair<string, string>> Identifiers, int? MergedIntoId,
    IReadOnlyList<KeyValuePair<string, string>>? Properties = null, bool CanDownload = false);

/// <summary>Why another book may be the same one, strongest first.</summary>
public enum DuplicateReason
{
    /// <summary>A file of the same size and CRC-32: the same bytes, almost surely (another library's: copies within one are merged at import).</summary>
    File,
    /// <summary>The same identifier (ISBN, EPUB UUID, FB2 id, Calibre's…).</summary>
    Identifier,
    /// <summary>The same title (else file name) and first author, normalized (<see cref="Book.WorkKey"/>).</summary>
    Work,
}

/// <param name="Scheme">For <see cref="DuplicateReason.Identifier"/>: which one (<c>isbn</c>, <c>uuid</c>…).</param>
public sealed record BookDuplicate(BookListItem Book, DuplicateReason Reason, string? Scheme);

/// <summary>
/// Book lists by key (agents/docs/development/scale.md): the account's visible books (<see cref="BookVisibility"/>) as
/// part of the query, ordered by an index ending in the id, a page plus one row read to know whether more follow.
/// </summary>
public sealed class BookQueries(TenantDb tenantDb)
{
    public const int MaxLimit = 200;

    public async Task<Page<BookListItem>> ListAsync(int tenantId, BookVisibility visibility, BookPageRequest request, CancellationToken cancellationToken = default)
    {
        if (request.LibraryId is { } only && !visibility.Libraries.ContainsKey(only))
            return new Page<BookListItem>([], null);
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var limit = Math.Clamp(request.Limit, 1, MaxLimit);
        var query = await VisibleAsync(db, visibility, request.LibraryId, cancellationToken, limit);
        var libraries = request.LibraryId is { } library ? [library] : visibility.Libraries.Keys.ToList();
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var fields = await BookSearch.FieldsAsync(db, cancellationToken);
            query = await BookSearch.ApplyAsync(db, query, SearchQuery.Parse(request.Query, fields.ContainsKey), fields, libraries, limit, cancellationToken);
        }

        if (request is { Sort: BookSort.Number, TagId: { } numbered })
            return await NumberedAsync(db, query, numbered, request.After, limit, cancellationToken);
        if (request.TagId is { } tagId)
        {
            var tags = await DescendantsAsync(db, [tagId], cancellationToken);
            query = query.Where(await NarrowAsync(db, [tagId], libraries, limit, cancellationToken) ? From(db, tags) : Probe(db, tags));
        }

        var after = KeysetCursor.Decode(request.After);
        var rows = await Order(query, request.Sort, after)
            .Take(limit + 1)
            .Select(b => new BookRow(b.Id, b.LibraryId, b.Title, b.SortTitle, b.SortAuthor, b.Added, b.SeriesId, b.SeriesNumber, b.CoverSource))
            .ToListAsync(cancellationToken);
        var more = rows.Count > limit;
        if (more)
            rows.RemoveAt(limit);

        var items = await ItemsAsync(db, rows, cancellationToken);
        string? next = null;
        if (more)
        {
            var last = rows[^1];
            next = (request.Sort switch
            {
                BookSort.Author => new KeysetCursor(last.SortAuthor, null, last.Id),
                BookSort.Added => new KeysetCursor(null, last.Added.Ticks, last.Id),
                _ => new KeysetCursor(last.SortTitle, null, last.Id),
            }).Encode();
        }

        return new Page<BookListItem>(items, next);
    }

    private sealed record BookRow(int Id, int LibraryId, string? Title, string SortTitle, string SortAuthor, DateTime Added, int? SeriesId, double? SeriesNumber, CoverSource CoverSource);

    /// <summary>The page's rows with their authors, series and formats: one query each for the whole page.</summary>
    private static async Task<List<BookListItem>> ItemsAsync(LibrarionDbContext db, IReadOnlyList<BookRow> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.Id).ToList();
        // By key: the unique index (TenantId, FieldKey) finds it; BuiltIn has no index (650k tags at 2M books).
        var authorField = await db.Tags.Where(t => t.FieldKey == "author").Select(t => t.Id).FirstOrDefaultAsync(cancellationToken);
        var authors = (await db.BookTags.Where(l => ids.Contains(l.BookId) && l.Tag!.FieldId == authorField)
                .Select(l => new { l.BookId, l.Ordinal, l.Tag!.Name })
                .ToListAsync(cancellationToken))
            .GroupBy(a => a.BookId).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Ordinal).Select(a => a.Name).ToList());
        var files = (await db.BookFiles.Where(f => ids.Contains(f.BookId)).Select(f => new { f.BookId, f.Id, f.Format, f.Path }).ToListAsync(cancellationToken))
            .GroupBy(f => f.BookId).ToDictionary(g => g.Key, g => g.OrderBy(f => f.Id).ToList());
        var seriesIds = rows.Where(r => r.SeriesId != null).Select(r => r.SeriesId!.Value).Distinct().ToList();
        var series = seriesIds.Count == 0 ? [] : await db.Tags.Where(t => seriesIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var items = rows.Select(r =>
        {
            var bookFiles = files.GetValueOrDefault(r.Id) ?? [];
            return new BookListItem(
                r.Id, r.LibraryId, r.Title, r.Title is null && bookFiles.Count > 0 ? FileTitles.FromPath(bookFiles[0].Path) : null,
                authors.GetValueOrDefault(r.Id) ?? [], r.SeriesId is { } s ? series.GetValueOrDefault(s) : null, r.SeriesNumber,
                bookFiles.Select(f => f.Format).Distinct().ToList(), r.CoverSource);
        }).ToList();
        return items;
    }

    /// <summary>
    /// A tag's books by the number on their link (a series), walking the partial index (TagId, Number, BookId) in order
    /// and keeping the visible ones: as fast for a series of 20k books as for one of 3.
    /// </summary>
    private static async Task<Page<BookListItem>> NumberedAsync(LibrarionDbContext db, IQueryable<Book> visible, int tagId, string? after, int limit, CancellationToken cancellationToken)
    {
        var links = db.BookTags.Where(l => l.TagId == tagId && l.Number != null);
        if (KeysetCursor.Decode(after) is { Real: { } number } cursor)
            links = links.Where(l => l.Number >= number && (l.Number > number || l.BookId > cursor.Id));
        var rows = await links.OrderBy(l => l.Number).ThenBy(l => l.BookId)
            .Where(l => visible.Any(b => b.Id == l.BookId))
            .Select(l => new { l.BookId, l.Number })
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var more = rows.Count > limit;
        if (more)
            rows.RemoveAt(limit);
        var ids = rows.Select(r => r.BookId).ToList();
        var books = await db.Books.Where(b => ids.Contains(b.Id))
            .Select(b => new BookRow(b.Id, b.LibraryId, b.Title, b.SortTitle, b.SortAuthor, b.Added, b.SeriesId, b.SeriesNumber, b.CoverSource))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        var page = await ItemsAsync(db, ids.Select(id => books[id]).ToList(), cancellationToken);
        return new Page<BookListItem>(page, more ? new KeysetCursor(null, null, rows[^1].BookId, rows[^1].Number).Encode() : null);
    }

    /// <summary>One book with its tags by field, files and identifiers; null when it doesn't exist or the account can't see it.</summary>
    public async Task<BookDetail?> GetAsync(int tenantId, BookVisibility visibility, int id, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var merged = await db.Books.Where(b => b.Id == id && b.MergedIntoId != null).Select(b => new { b.LibraryId, b.MergedIntoId }).FirstOrDefaultAsync(cancellationToken);
        var visible = await VisibleAsync(db, visibility, merged?.LibraryId, cancellationToken);
        if (merged is not null)
        {
            // A redirect, only for those who could see it (visibility ignores merged books, so check the library alone).
            return visibility.Libraries.ContainsKey(merged.LibraryId)
                ? new BookDetail(id, merged.LibraryId, null, null, null, null, default, default, [], [], [], merged.MergedIntoId)
                : null;
        }

        var book = await visible.Where(b => b.Id == id).FirstOrDefaultAsync(cancellationToken);
        if (book is null)
            return null;

        var tags = await db.BookTags.Where(l => l.BookId == id)
            .OrderBy(l => l.Tag!.FieldId).ThenBy(l => l.Ordinal).ThenBy(l => l.Tag!.SortName)
            .Select(l => new BookTagItem(l.TagId, l.Tag!.Field!.FieldKey, l.Tag.Name, l.Number))
            .ToListAsync(cancellationToken);
        var files = (await db.BookFiles.Where(f => f.BookId == id).OrderBy(f => f.Id)
                .Select(f => new { f.Id, f.Format, f.Size, f.Path, f.ArchiveId, Archive = f.Archive!.Path, f.MissingSince })
                .ToListAsync(cancellationToken))
            .Select(f => new BookFileItem(f.Id, f.Format, f.Size, LastSegment(f.Path), f.ArchiveId != null,
                f.ArchiveId != null ? LastSegment(f.Archive) : null, f.MissingSince != null)).ToList();
        var identifiers = await db.BookIdentifiers.Where(i => i.BookId == id).OrderBy(i => i.Scheme).ThenBy(i => i.Value)
            .Select(i => new KeyValuePair<string, string>(i.Scheme, i.Value)).ToListAsync(cancellationToken);
        var properties = await db.BookProperties.Where(p => p.BookId == id).OrderBy(p => p.Name)
            .Select(p => new KeyValuePair<string, string>(p.Name, p.Value)).ToListAsync(cancellationToken);
        return new BookDetail(
            book.Id, book.LibraryId, book.Title, book.Title is null && files.Count > 0 ? FileTitles.FromPath(files[0].Name) : null, book.Description,
            book.Rating, book.Added, book.CoverSource, tags.DistinctBy(t => t.Id).ToList(), files, identifiers, null, properties);
    }

    private static string LastSegment(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    /// <summary>The most possible duplicates listed for a book.</summary>
    public const int MaxDuplicates = 20;

    /// <summary>A key shared by more books than this is noise ("Untitled" by "Unknown"), never a suggestion (agents/docs/features/merging.md).</summary>
    public const int MaxGroup = 50;

    /// <summary>The most files or identifiers of one book looked up.</summary>
    private const int MaxKeys = 20;

    /// <summary>Identifiers naming a record of one source, not a book: INPX numbers collide between collections.</summary>
    private static readonly string[] SourceSchemes = ["inpx"];

    /// <summary>
    /// Other books the caller can see that may be this one (agents/docs/features/merging.md; read only until merging is
    /// built): a file of the same size and CRC, a shared identifier, the same work key. Each key is one seek on its
    /// index reading at most <see cref="MaxGroup"/> books (a bigger group is noise, dropped): bounded by the book,
    /// never by the catalog. Null when the caller can't see the book.
    /// </summary>
    public async Task<IReadOnlyList<BookDuplicate>?> DuplicatesAsync(int tenantId, BookVisibility visibility, int id, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var visible = await VisibleAsync(db, visibility, null, cancellationToken);
        var book = await visible.Where(b => b.Id == id).Select(b => new { b.WorkKey }).FirstOrDefaultAsync(cancellationToken);
        if (book is null)
            return null;

        var found = new Dictionary<int, (DuplicateReason Reason, string? Scheme)>();
        void Add(List<int> books, DuplicateReason reason, string? scheme)
        {
            foreach (var other in books)
                found.TryAdd(other, (reason, scheme));
        }

        var files = await db.BookFiles.Where(f => f.BookId == id && f.Crc32 != null).Select(f => new { f.Size, f.Crc32 })
            .Distinct().Take(MaxKeys).ToListAsync(cancellationToken);
        foreach (var file in files)
            Add(await GroupAsync(db.BookFiles.Where(f => f.Size == file.Size && f.Crc32 == file.Crc32 && f.BookId != id).Select(f => f.BookId), cancellationToken),
                DuplicateReason.File, null);
        var identifiers = await db.BookIdentifiers.Where(i => i.BookId == id && !SourceSchemes.Contains(i.Scheme))
            .OrderBy(i => i.Scheme).ThenBy(i => i.Value).Select(i => new { i.Scheme, i.Value }).Take(MaxKeys).ToListAsync(cancellationToken);
        foreach (var identifier in identifiers)
            Add(await GroupAsync(db.BookIdentifiers.Where(i => i.Scheme == identifier.Scheme && i.Value == identifier.Value && i.BookId != id).Select(i => i.BookId), cancellationToken),
                DuplicateReason.Identifier, identifier.Scheme);
        if (book.WorkKey is { } work)
            Add(await GroupAsync(db.Books.Where(b => b.WorkKey == work && b.Id != id).Select(b => b.Id), cancellationToken), DuplicateReason.Work, null);
        if (found.Count == 0)
            return [];

        var ids = found.Keys.ToList();
        var rows = (await visible.Where(b => ids.Contains(b.Id))
                .Select(b => new BookRow(b.Id, b.LibraryId, b.Title, b.SortTitle, b.SortAuthor, b.Added, b.SeriesId, b.SeriesNumber, b.CoverSource))
                .ToListAsync(cancellationToken))
            .OrderBy(r => found[r.Id].Reason).ThenBy(r => r.SortTitle, StringComparer.Ordinal).ThenBy(r => r.Id).Take(MaxDuplicates).ToList();
        return (await ItemsAsync(db, rows, cancellationToken)).Select(item => new BookDuplicate(item, found[item.Id].Reason, found[item.Id].Scheme)).ToList();
    }

    /// <summary>The other books sharing a key, unless more than <see cref="MaxGroup"/> share it (this one counted).</summary>
    private static async Task<List<int>> GroupAsync(IQueryable<int> others, CancellationToken cancellationToken)
    {
        var ids = await others.Distinct().Take(MaxGroup).ToListAsync(cancellationToken);
        return ids.Count >= MaxGroup ? [] : ids;
    }

    /// <summary>The books the account may see, not merged away: one library or all, with the groups' content filters.</summary>
    /// <param name="limit">The page size: it decides how a content filter is joined (<see cref="NarrowAsync"/>).</param>
    public static async Task<IQueryable<Book>> VisibleAsync(LibrarionDbContext db, BookVisibility visibility, int? libraryId, CancellationToken cancellationToken, int limit = 1)
    {
        // Missing books are kept, not shown (agents/docs/features/missing-books.md).
        var books = db.Books.Where(b => b.MergedIntoId == null && b.MissingSince == null);
        var libraries = libraryId is { } one
            ? visibility.Libraries.Where(l => l.Key == one).ToList()
            : visibility.Libraries.ToList();
        if (libraries.Count == 0)
            return books.Where(b => false);

        // Every library of the tenant, all open: the tenant's filter alone (an index on (TenantId, sort, Id) serves it).
        if (libraryId is null && libraries.All(l => visibility.IsOpenIn(l.Key))
            && await db.Libraries.CountAsync(cancellationToken) == libraries.Count)
            return books;

        Expression<Func<Book, bool>>? any = null;
        foreach (var (library, profiles) in libraries)
        {
            Expression<Func<Book, bool>> inLibrary = b => b.LibraryId == library;
            Expression<Func<Book, bool>>? admitted = null;
            if (!profiles.Any(p => p.IsOpen))
            {
                foreach (var profile in profiles)
                {
                    var narrow = profile.Allowed.Count > 0 && await NarrowAsync(db, profile.Allowed, [library], limit, cancellationToken);
                    admitted = Or(admitted, await AdmitsAsync(db, profile, narrow, cancellationToken));
                }
            }

            any = Or(any, admitted is null ? inLibrary : And(inLibrary, admitted));
        }

        return books.Where(any!);
    }

    private static async Task<Expression<Func<Book, bool>>> AdmitsAsync(LibrarionDbContext db, FilterProfile profile, bool narrow, CancellationToken cancellationToken)
    {
        Expression<Func<Book, bool>>? result = null;
        if (profile.Allowed.Count > 0)
        {
            var allowed = await DescendantsAsync(db, profile.Allowed, cancellationToken);
            result = narrow ? From(db, allowed) : Probe(db, allowed);
        }

        if (profile.Denied.Count > 0)
        {
            var denied = await DescendantsAsync(db, profile.Denied, cancellationToken);
            var none = Probe(db, denied);
            none = Expression.Lambda<Func<Book, bool>>(Expression.Not(none.Body), none.Parameters);
            result = result is null ? none : And(result, none);
        }

        return result ?? (b => true);
    }

    /// <summary>The largest descendant list given to a query as values; a tag with more (a field: every author) is joined through the closure instead.</summary>
    internal const int MaxListedTags = 500;

    /// <summary>
    /// The tags and their descendants, resolved once: a book's test is then one seek on the key (BookId, TagId) of
    /// <see cref="BookTag"/>, not a join of each of its tags with the closure (the probe of a 1 % filter at 2M books).
    /// </summary>
    internal static async Task<TagSet> DescendantsAsync(LibrarionDbContext db, IReadOnlyList<int> tagIds, CancellationToken cancellationToken)
    {
        var listed = await db.TagClosure.Where(c => tagIds.Contains(c.AncestorId)).Select(c => c.DescendantId).Take(MaxListedTags + 1).ToListAsync(cancellationToken);
        return listed.Count > MaxListedTags ? new TagSet(tagIds, null) : new TagSet(tagIds, listed.Distinct().ToList());
    }

    /// <param name="Descendants">Every tag of the set with its descendants, when few enough to list; else null (joined through the closure).</param>
    internal sealed record TagSet(IReadOnlyList<int> Ancestors, IReadOnlyList<int>? Descendants);

    /// <summary>
    /// Whether books with one of <paramref name="tagIds"/> (or a descendant) are few enough to start from them. Two ways
    /// to fill a page of books having a tag (D22, measured at 2M books by the stage 2 benchmark):
    /// <list type="bullet">
    /// <item>walk the sort index and test each book (<see cref="Probe"/>): about page ÷ share tests, cheap for a broad tag,
    /// 780 ms for a 0.1 % filter;</item>
    /// <item>start from the tag's books and sort them (<see cref="From"/>): share × books rows sorted, cheap for a narrow
    /// tag, 2.9 s for category Fiction.</item>
    /// </list>
    /// They cost the same near share = √(page ÷ books); the counts come from <see cref="TagCount"/> (with descendants).
    /// </summary>
    private static async Task<bool> NarrowAsync(LibrarionDbContext db, IReadOnlyList<int> tagIds, IReadOnlyCollection<int> libraryIds, int limit, CancellationToken cancellationToken)
    {
        var tagged = await db.TagCounts.Where(c => tagIds.Contains(c.TagId) && libraryIds.Contains(c.LibraryId)).SumAsync(c => (long)c.BookCount, cancellationToken);
        var books = await db.Libraries.Where(l => libraryIds.Contains(l.Id)).SumAsync(l => (long)l.BookCount, cancellationToken);
        return tagged < Math.Sqrt((double)limit * Math.Max(books, 1));
    }

    /// <summary>Books having one of the tags or a descendant, tested book by book (the outer query walks its sort index).</summary>
    internal static Expression<Func<Book, bool>> Probe(LibrarionDbContext db, TagSet tags)
    {
        if (tags.Descendants is { } listed)
            return b => db.BookTags.Any(l => l.BookId == b.Id && listed.Contains(l.TagId));
        var ancestors = tags.Ancestors;
        return b => db.BookTags.Any(l => l.BookId == b.Id && db.TagClosure.Any(c => c.DescendantId == l.TagId && ancestors.Contains(c.AncestorId)));
    }

    /// <summary>Books having one of the tags or a descendant, gathered from the tags' links (index (TagId, BookId)).</summary>
    internal static Expression<Func<Book, bool>> From(LibrarionDbContext db, TagSet tags)
    {
        IQueryable<int> tagged;
        if (tags.Descendants is { } listed)
        {
            tagged = db.BookTags.Where(l => listed.Contains(l.TagId)).Select(l => l.BookId);
        }
        else
        {
            var ancestors = tags.Ancestors;
            tagged = db.BookTags.Where(l => db.TagClosure.Where(c => ancestors.Contains(c.AncestorId)).Select(c => c.DescendantId).Contains(l.TagId)).Select(l => l.BookId);
        }

        return b => tagged.Contains(b.Id);
    }

    private static IQueryable<Book> Order(IQueryable<Book> books, BookSort sort, KeysetCursor? after)
    {
        switch (sort)
        {
            case BookSort.Author:
                if (after?.Text is { } author)
                    books = books.Where(b => string.Compare(b.SortAuthor, author) >= 0 && (string.Compare(b.SortAuthor, author) > 0 || b.Id > after.Id));
                return books.OrderBy(b => b.SortAuthor).ThenBy(b => b.Id);
            case BookSort.Added:
                if (after?.Number is { } ticks)
                {
                    var added = new DateTime(ticks, DateTimeKind.Utc);
                    books = books.Where(b => b.Added <= added && (b.Added < added || b.Id < after.Id));
                }

                return books.OrderByDescending(b => b.Added).ThenByDescending(b => b.Id);
            default:
                if (after?.Text is { } title)
                    books = books.Where(b => string.Compare(b.SortTitle, title) >= 0 && (string.Compare(b.SortTitle, title) > 0 || b.Id > after.Id));
                return books.OrderBy(b => b.SortTitle).ThenBy(b => b.Id);
        }
    }

    internal static Expression<Func<Book, bool>> Or(Expression<Func<Book, bool>>? left, Expression<Func<Book, bool>> right) =>
        left is null ? right : Combine(left, right, Expression.OrElse);

    internal static Expression<Func<Book, bool>> And(Expression<Func<Book, bool>> left, Expression<Func<Book, bool>> right) =>
        Combine(left, right, Expression.AndAlso);

    private static Expression<Func<Book, bool>> Combine(Expression<Func<Book, bool>> left, Expression<Func<Book, bool>> right, Func<Expression, Expression, BinaryExpression> join)
    {
        var body = ReplacingExpressionVisitor.Replace(right.Parameters[0], left.Parameters[0], right.Body);
        return Expression.Lambda<Func<Book, bool>>(join(left.Body, body), left.Parameters);
    }
}
