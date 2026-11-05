using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Tagging;

/// <summary>
/// What a change of links (a delete, a merge, an undo) keeps up for a batch of books: <see cref="TagCount"/> by delta
/// (never recounted whole, agents/docs/features/tags.md#scale) and the books' sort columns that copy a link (the first
/// author's sort name, the first series).
/// </summary>
public static class TagLinks
{
    /// <summary>The (library, tag, book) of the books present (not missing) among <paramref name="books"/> that have one of <paramref name="tags"/>.</summary>
    public static async Task<HashSet<(int Library, int Tag, int Book)>> PresentAsync(LibrarionDbContext db, IReadOnlyCollection<int> tags, IReadOnlyCollection<int> books,
        CancellationToken cancellationToken)
    {
        var rows = await db.BookTags.Where(l => tags.Contains(l.TagId) && books.Contains(l.BookId) && l.Book!.MissingSince == null)
            .Select(l => new { l.Book!.LibraryId, l.TagId, l.BookId }).Distinct().ToListAsync(cancellationToken);
        return rows.Select(r => (r.LibraryId, r.TagId, r.BookId)).ToHashSet();
    }

    /// <summary>The counts moved from <paramref name="before"/> to <paramref name="after"/> (both from <see cref="PresentAsync"/>), written per library.</summary>
    public static async Task CountAsync(LibrarionDbContext db, int tenantId, HashSet<(int Library, int Tag, int Book)> before, HashSet<(int Library, int Tag, int Book)> after,
        CancellationToken cancellationToken)
    {
        var deltas = new Dictionary<(int Library, int Tag), int>();
        foreach (var gone in before.Except(after))
            deltas[(gone.Library, gone.Tag)] = deltas.GetValueOrDefault((gone.Library, gone.Tag)) - 1;
        foreach (var come in after.Except(before))
            deltas[(come.Library, come.Tag)] = deltas.GetValueOrDefault((come.Library, come.Tag)) + 1;
        foreach (var library in deltas.Where(d => d.Value != 0).GroupBy(d => d.Key.Library))
        {
            var changes = library.Select(d => new KeyValuePair<int, int>(d.Key.Tag, d.Value)).ToList();
            await BulkRows.AddCountsAsync(db, tenantId, library.Key, changes, cancellationToken);
            var ids = changes.Select(c => c.Key).ToList();
            await db.TagCounts.Where(c => c.LibraryId == library.Key && ids.Contains(c.TagId) && c.BookCount <= 0).ExecuteDeleteAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Sets <see cref="Book.SortAuthor"/> and <see cref="Book.SeriesId"/> / <see cref="Book.SeriesNumber"/> of
    /// <paramref name="books"/> from their links (the first by ordinal), writing only the books whose values change.
    /// </summary>
    public static async Task SortColumnsAsync(LibrarionDbContext db, IReadOnlyCollection<int> books, CancellationToken cancellationToken)
    {
        var fields = await db.Tags.Where(t => t.FieldKey == "author" || t.FieldKey == "series").Select(t => new { t.Id, t.FieldKey }).ToListAsync(cancellationToken);
        var author = fields.FirstOrDefault(f => f.FieldKey == "author")?.Id;
        var series = fields.FirstOrDefault(f => f.FieldKey == "series")?.Id;
        foreach (var chunk in books.Distinct().Chunk(1000))
        {
            var links = await db.BookTags.Where(l => chunk.Contains(l.BookId) && (l.Tag!.FieldId == author || l.Tag.FieldId == series))
                .Select(l => new { l.BookId, l.TagId, l.Tag!.FieldId, l.Tag.SortName, l.Ordinal, l.Number }).ToListAsync(cancellationToken);
            var byBook = links.ToLookup(l => l.BookId);
            var rows = await db.Books.Where(b => chunk.Contains(b.Id)).Select(b => new { b.Id, b.SortAuthor, b.SeriesId, b.SeriesNumber }).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                var first = byBook[row.Id].Where(l => l.FieldId == author).OrderBy(l => l.Ordinal ?? int.MaxValue).ThenBy(l => l.TagId).FirstOrDefault();
                var sortAuthor = first is null ? "" : first.SortName.Length > Book.MaxSortLength ? first.SortName[..Book.MaxSortLength] : first.SortName;
                var inSeries = byBook[row.Id].Where(l => l.FieldId == series).OrderBy(l => l.Ordinal ?? int.MaxValue).ThenBy(l => l.TagId).FirstOrDefault();
                var seriesId = inSeries?.TagId;
                var number = inSeries?.Number;
                if (sortAuthor == row.SortAuthor && seriesId == row.SeriesId && number == row.SeriesNumber)
                    continue;
                await db.Books.Where(b => b.Id == row.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.SortAuthor, sortAuthor).SetProperty(b => b.SeriesId, seriesId).SetProperty(b => b.SeriesNumber, number), cancellationToken);
            }
        }
    }
}
