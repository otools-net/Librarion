using System.Linq.Expressions;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Librarion.Core.Catalog;

public enum BookSort
{
    Title,
    Author,
    /// <summary>Newest first.</summary>
    Added,
}

/// <param name="LibraryId">One library; null = every library the account browses.</param>
/// <param name="TagId">Only books with this tag or a descendant (a field value's page: an author, a category).</param>
public sealed record BookPageRequest(int? LibraryId = null, BookSort Sort = BookSort.Title, string? After = null, int Limit = 50, int? TagId = null);

/// <param name="Title">From the metadata; null when it gave none (<see cref="FileName"/> is shown).</param>
/// <param name="FileName">The first file's name without its book extensions, for a book without a title.</param>
/// <param name="Authors">In order; the first is the one on cards.</param>
public sealed record BookListItem(
    int Id, int LibraryId, string? Title, string? FileName, IReadOnlyList<string> Authors, string? Series, double? SeriesNumber,
    IReadOnlyList<string> Formats, CoverSource CoverSource);

/// <param name="Field">The field's key; null for a plain tag.</param>
public sealed record BookTagItem(int Id, string? Field, string Name, double? Number);

public sealed record BookFileItem(int Id, string Format, long Size, string Name, bool InArchive);

/// <param name="MergedIntoId">Set when the book was merged into another: the client goes there.</param>
public sealed record BookDetail(
    int Id, int LibraryId, string? Title, string? FileName, string? Description, byte? Rating, DateTime Added, CoverSource CoverSource,
    IReadOnlyList<BookTagItem> Tags, IReadOnlyList<BookFileItem> Files, IReadOnlyList<KeyValuePair<string, string>> Identifiers, int? MergedIntoId);

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
        var query = await VisibleAsync(db, visibility, request.LibraryId, cancellationToken);
        if (request.TagId is { } tagId)
        {
            var tagged = db.BookTags.Where(l => db.TagClosure.Where(c => c.AncestorId == tagId).Select(c => c.DescendantId).Contains(l.TagId)).Select(l => l.BookId);
            query = query.Where(b => tagged.Contains(b.Id));
        }

        var limit = Math.Clamp(request.Limit, 1, MaxLimit);
        var after = KeysetCursor.Decode(request.After);
        var rows = await Order(query, request.Sort, after)
            .Take(limit + 1)
            .Select(b => new { b.Id, b.LibraryId, b.Title, b.SortTitle, b.SortAuthor, b.Added, b.SeriesId, b.SeriesNumber, b.CoverSource })
            .ToListAsync(cancellationToken);
        var more = rows.Count > limit;
        if (more)
            rows.RemoveAt(limit);

        var ids = rows.Select(r => r.Id).ToList();
        var authorField = await db.Tags.Where(t => t.BuiltIn == BuiltInTag.Author).Select(t => t.Id).FirstOrDefaultAsync(cancellationToken);
        var authors = (await db.BookTags.Where(l => ids.Contains(l.BookId) && l.Tag!.FieldId == authorField)
                .Select(l => new { l.BookId, l.Ordinal, l.Tag!.Name })
                .ToListAsync(cancellationToken))
            .GroupBy(a => a.BookId).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Ordinal).Select(a => a.Name).ToList());
        var files = (await db.BookFiles.Where(f => ids.Contains(f.BookId)).Select(f => new { f.BookId, f.Id, f.Format, f.Path }).ToListAsync(cancellationToken))
            .GroupBy(f => f.BookId).ToDictionary(g => g.Key, g => g.OrderBy(f => f.Id).ToList());
        var seriesIds = rows.Where(r => r.SeriesId != null).Select(r => r.SeriesId!.Value).Distinct().ToList();
        var series = await db.Tags.Where(t => seriesIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var items = rows.Select(r =>
        {
            var bookFiles = files.GetValueOrDefault(r.Id) ?? [];
            return new BookListItem(
                r.Id, r.LibraryId, r.Title, r.Title is null && bookFiles.Count > 0 ? FileTitles.FromPath(bookFiles[0].Path) : null,
                authors.GetValueOrDefault(r.Id) ?? [], r.SeriesId is { } s ? series.GetValueOrDefault(s) : null, r.SeriesNumber,
                bookFiles.Select(f => f.Format).Distinct().ToList(), r.CoverSource);
        }).ToList();

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
        var files = (await db.BookFiles.Where(f => f.BookId == id).OrderBy(f => f.Id).ToListAsync(cancellationToken))
            .Select(f => new BookFileItem(f.Id, f.Format, f.Size, f.Path[(f.Path.LastIndexOfAny(['/', '\\']) + 1)..], f.ArchiveId != null)).ToList();
        var identifiers = await db.BookIdentifiers.Where(i => i.BookId == id).OrderBy(i => i.Scheme).ThenBy(i => i.Value)
            .Select(i => new KeyValuePair<string, string>(i.Scheme, i.Value)).ToListAsync(cancellationToken);
        return new BookDetail(
            book.Id, book.LibraryId, book.Title, book.Title is null && files.Count > 0 ? FileTitles.FromPath(files[0].Name) : null, book.Description,
            book.Rating, book.Added, book.CoverSource, tags.DistinctBy(t => t.Id).ToList(), files, identifiers, null);
    }

    /// <summary>The books the account may see, not merged away: one library or all, with the groups' content filters.</summary>
    public static async Task<IQueryable<Book>> VisibleAsync(LibrarionDbContext db, BookVisibility visibility, int? libraryId, CancellationToken cancellationToken)
    {
        var books = db.Books.Where(b => b.MergedIntoId == null);
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
                    admitted = Or(admitted, Admits(db, profile));
            }

            any = Or(any, admitted is null ? inLibrary : And(inLibrary, admitted));
        }

        return books.Where(any!);
    }

    private static Expression<Func<Book, bool>> Admits(LibrarionDbContext db, FilterProfile profile)
    {
        Expression<Func<Book, bool>>? result = null;
        if (profile.Allowed.Count > 0)
        {
            var allowed = profile.Allowed;
            result = b => db.BookTags.Any(l => l.BookId == b.Id && db.TagClosure.Any(c => c.DescendantId == l.TagId && allowed.Contains(c.AncestorId)));
        }

        if (profile.Denied.Count > 0)
        {
            var denied = profile.Denied;
            Expression<Func<Book, bool>> none = b => !db.BookTags.Any(l => l.BookId == b.Id && db.TagClosure.Any(c => c.DescendantId == l.TagId && denied.Contains(c.AncestorId)));
            result = result is null ? none : And(result, none);
        }

        return result ?? (b => true);
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

    private static Expression<Func<Book, bool>> Or(Expression<Func<Book, bool>>? left, Expression<Func<Book, bool>> right) =>
        left is null ? right : Combine(left, right, Expression.OrElse);

    private static Expression<Func<Book, bool>> And(Expression<Func<Book, bool>> left, Expression<Func<Book, bool>> right) =>
        Combine(left, right, Expression.AndAlso);

    private static Expression<Func<Book, bool>> Combine(Expression<Func<Book, bool>> left, Expression<Func<Book, bool>> right, Func<Expression, Expression, BinaryExpression> join)
    {
        var body = ReplacingExpressionVisitor.Replace(right.Parameters[0], left.Parameters[0], right.Body);
        return Expression.Lambda<Func<Book, bool>>(join(left.Body, body), left.Parameters);
    }
}
