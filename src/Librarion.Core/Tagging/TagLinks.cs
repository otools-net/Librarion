using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Tagging;

/// <summary>
/// What a change of links (a delete, a merge, an undo) keeps up for a batch of books: <see cref="TagCount"/> by delta
/// from the books' tags before and after (never recounted whole, agents/docs/features/tags.md#scale) and the books'
/// sort columns that copy a link (the first author's sort name, the first series).
/// </summary>
public static class TagLinks
{
    /// <summary>The tags of each present book (not missing) of <paramref name="books"/>, with its library: a side of a change.</summary>
    public static async Task<Dictionary<int, (int Library, int[] Tags)>> SnapshotAsync(LibrarionDbContext db, IReadOnlyCollection<int> books,
        CancellationToken cancellationToken)
    {
        var rows = await db.BookTags.Where(l => books.Contains(l.BookId) && l.Book!.MissingSince == null)
            .Select(l => new { l.BookId, l.Book!.LibraryId, l.TagId }).ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.BookId).ToDictionary(g => g.Key, g => (g.First().LibraryId, g.Select(r => r.TagId).Distinct().ToArray()));
    }

    /// <summary>The counts moved from <paramref name="before"/> to <paramref name="after"/> (both from <see cref="SnapshotAsync"/>), written per library.</summary>
    public static async Task CountAsync(LibrarionDbContext db, int tenantId, Dictionary<int, (int Library, int[] Tags)> before,
        Dictionary<int, (int Library, int[] Tags)> after, CancellationToken cancellationToken)
    {
        var changes = new TagCountChanges();
        foreach (var book in before.Keys.Union(after.Keys))
        {
            var was = before.GetValueOrDefault(book);
            var now = after.GetValueOrDefault(book);
            changes.Add(was.Tags is null ? now.Library : was.Library, was.Tags ?? [], now.Tags ?? []);
        }

        await changes.WriteAsync(db, tenantId, new TagAncestors(), cancellationToken);
    }

    /// <summary>
    /// Sets <see cref="Book.SortAuthor"/>, <see cref="Book.SeriesId"/> / <see cref="Book.SeriesNumber"/> and the work key
    /// (<see cref="Book.WorkKey"/>: title, else file name, and first author) of <paramref name="books"/> from their links
    /// (the first by ordinal), writing only the books whose values change.
    /// </summary>
    public static async Task SortColumnsAsync(LibrarionDbContext db, IReadOnlyCollection<int> books, CancellationToken cancellationToken)
    {
        var fields = await db.Tags.Where(t => t.FieldKey == "author" || t.FieldKey == "series").Select(t => new { t.Id, t.FieldKey }).ToListAsync(cancellationToken);
        var author = fields.FirstOrDefault(f => f.FieldKey == "author")?.Id;
        var series = fields.FirstOrDefault(f => f.FieldKey == "series")?.Id;
        var keysMoved = new List<int>();
        foreach (var chunk in books.Distinct().Chunk(1000))
        {
            var links = await db.BookTags.Where(l => chunk.Contains(l.BookId) && (l.Tag!.FieldId == author || l.Tag.FieldId == series))
                .Select(l => new { l.BookId, l.TagId, l.Tag!.FieldId, l.Tag.Name, l.Tag.SortName, l.Ordinal, l.Number }).ToListAsync(cancellationToken);
            var byBook = links.ToLookup(l => l.BookId);
            var rows = await db.Books.Where(b => chunk.Contains(b.Id)).Select(b => new { b.Id, b.Title, b.SortAuthor, b.SeriesId, b.SeriesNumber, b.WorkKey }).ToListAsync(cancellationToken);
            var paths = (await db.BookFiles.Where(f => chunk.Contains(f.BookId)).Select(f => new { f.BookId, f.Id, f.Path }).ToListAsync(cancellationToken))
                .GroupBy(f => f.BookId).ToDictionary(g => g.Key, g => g.MinBy(f => f.Id)!.Path);
            foreach (var row in rows)
            {
                var first = byBook[row.Id].Where(l => l.FieldId == author).OrderBy(l => l.Ordinal ?? int.MaxValue).ThenBy(l => l.TagId).FirstOrDefault();
                var sortAuthor = first is null ? "" : first.SortName.Length > Book.MaxSortLength ? first.SortName[..Book.MaxSortLength] : first.SortName;
                var inSeries = byBook[row.Id].Where(l => l.FieldId == series).OrderBy(l => l.Ordinal ?? int.MaxValue).ThenBy(l => l.TagId).FirstOrDefault();
                var seriesId = inSeries?.TagId;
                var number = inSeries?.Number;
                var workKey = PathKeys.Of(TagNames.Normalize(row.Title ?? FileTitles.FromPath(paths.GetValueOrDefault(row.Id) ?? "")),
                    first is null ? "" : TagNames.Normalize(first.Name));
                if (sortAuthor == row.SortAuthor && seriesId == row.SeriesId && number == row.SeriesNumber && workKey == row.WorkKey)
                    continue;
                await db.Books.Where(b => b.Id == row.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.SortAuthor, sortAuthor).SetProperty(b => b.SeriesId, seriesId).SetProperty(b => b.SeriesNumber, number)
                    .SetProperty(b => b.WorkKey, workKey), cancellationToken);
                if (sortAuthor != row.SortAuthor || workKey != row.WorkKey)
                    keysMoved.Add(row.Id);
            }
        }

        // The work key and first author are duplicate keys (stage 6 chunk 11): their groups follow an edit or a merge.
        if (keysMoved.Count > 0 && await Merging.DuplicateIndex.BuiltAsync(db, cancellationToken))
            await Merging.DuplicateIndex.RefreshAsync(db, keysMoved, cancellationToken);
    }
}
