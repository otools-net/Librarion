using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Search;

/// <param name="Id">The tag; null for a format.</param>
/// <param name="Term">The term that finds its books (<c>category:"Fantasy"</c>, <c>tag:"Favourites"</c>, <c>format:epub</c>).</param>
/// <param name="Count">Books with it, or one under it, among those counted.</param>
public sealed record FacetValue(int? Id, string Name, string Term, int Count);

/// <param name="Field">The field's key; <c>tag</c> for plain tags, <c>format</c> for files' formats.</param>
/// <param name="Values">The most frequent first, at most <see cref="SearchFacets.MaxValues"/>.</param>
public sealed record Facet(string Field, IReadOnlyList<FacetValue> Values);

/// <param name="Counted">The books the counts are over: every match, or the first ones read when <paramref name="Capped"/>.</param>
/// <param name="Capped">More books may match than were counted: counts and <paramref name="Counted"/> are at least that ("1,000+").</param>
public sealed record FacetList(int Counted, bool Capped, IReadOnlyList<Facet> Fields);

/// <summary>
/// The most frequent values of each field among a library's books or a search's matches, with counts (D-5.8,
/// agents/docs/features/search.md): from <see cref="TagCount"/> when nothing narrows the list (a library seen whole);
/// otherwise counted over at most <see cref="MaxCounted"/> of the matches, read unordered from the narrowest term's
/// own index, and marked capped past it. Never a count over the catalog per request.
/// </summary>
public sealed class SearchFacets(TenantDb tenantDb)
{
    public const int MaxValues = 8;

    /// <summary>
    /// The most matches counted over; past it the counts are marked capped. The time grows with it: at 2M books on
    /// SQLite a broad word took 130 ms with 2,000, 290 with 5,000, 480 with 10,000 (D-5.8 had 10,000; budget 300).
    /// </summary>
    public const int MaxCounted = 2000;

    /// <summary>
    /// A field with more values than this (authors, series) has no facet when nothing narrows the list: its most
    /// frequent values can't be found without reading every count. Its A–Z page lists them.
    /// </summary>
    public const int MaxValuesRead = 2000;

    /// <summary>The fields shown first, in this order; the others follow by key, then plain tags.</summary>
    private static readonly string[] Order = ["category", "author", "series", "language", "format", "publisher", "year"];

    /// <summary><see cref="MaxCounted"/>; smaller in tests.</summary>
    internal int Cap { get; set; } = MaxCounted;

    /// <summary>The facets of <paramref name="libraryId"/> (one the caller browses: the route checks), for the search <paramref name="query"/> if any.</summary>
    public async Task<FacetList> ListAsync(int tenantId, BookVisibility visibility, int libraryId, string? query, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var fields = await BookSearch.FieldsAsync(db, cancellationToken);
        var keys = await BookSearch.KeysAsync(db, cancellationToken);
        var search = SearchQuery.Parse(query, keys.ContainsKey);
        if (search.IsEmpty && visibility.IsOpenIn(libraryId))
            return await CountedAsync(db, libraryId, fields, cancellationToken);

        var cap = Cap;
        var visible = await BookQueries.VisibleAsync(db, visibility, libraryId, cancellationToken, cap);
        List<int> books;
        bool capped;
        if (search.IsEmpty)
        {
            // Content filters narrow the library: read from the filters' tags unordered when they are the start (sorting
            // the 1 % group's 20k books by title took SQLite 300 ms), else the first books by title (the sort index walked).
            var ids = visible.Select(b => b.Id);
            if (!await BookQueries.StartsFromTagsAsync(db, visibility, libraryId, cap, cancellationToken))
                ids = visible.OrderBy(b => b.SortTitle).ThenBy(b => b.Id).Select(b => b.Id);
            books = await ids.Take(cap + 1).ToListAsync(cancellationToken);
            capped = books.Count > cap;
            if (capped)
                books.RemoveAt(cap);
        }
        else
        {
            (books, capped) = await BookSearch.SampleAsync(db, visible, search, keys, [libraryId], cap, cancellationToken);
        }

        if (books.Count == 0)
            return new FacetList(0, capped, []);

        var rows = await CountAsync(db, books, cancellationToken);
        var formats = await db.BookFiles.Where(f => EF.Parameter(books).Contains(f.BookId))
            .GroupBy(f => f.Format)
            .Select(g => new { Format = g.Key, Count = g.Select(f => f.BookId).Distinct().Count() })
            .ToListAsync(cancellationToken);

        var facets = await NamedAsync(db, fields, rows.GroupBy(r => r.FieldId).ToDictionary(g => g.Key ?? 0, g => Top(g)), cancellationToken);
        if (formats.Count > 0)
        {
            facets.Add(new Facet("format", formats.OrderByDescending(f => f.Count).ThenBy(f => f.Format, StringComparer.Ordinal).Take(MaxValues)
                .Select(f => new FacetValue(null, f.Format, $"format:{f.Format}", f.Count)).ToList()));
        }

        return new FacetList(books.Count, capped, Sorted(facets));
    }

    private sealed record Counted(int TagId, int? FieldId, int Count);

    /// <summary>The most parent links read; past it a tag over others keeps its own count only.</summary>
    private const int MaxEdges = 10_000;

    /// <summary>
    /// Each tag's books among <paramref name="books"/>, each book once under every tag it has or is under (a category
    /// over its subcategories), as a search finds them. The links are read once and counted here, the parents' edges
    /// (a small table) walked in memory: grouped in the database through the closure, it took 540 ms for 10,000 books
    /// at 2M on SQLite; grouped there by tag and joined to the tags, 210.
    /// </summary>
    private static async Task<List<Counted>> CountAsync(LibrarionDbContext db, List<int> books, CancellationToken cancellationToken)
    {
        var links = await db.BookTags.Where(l => EF.Parameter(books).Contains(l.BookId)).Select(l => new { l.BookId, l.TagId }).ToListAsync(cancellationToken);
        var edges = await db.TagParents.Select(p => new { p.ChildId, p.ParentId }).Take(MaxEdges + 1).ToListAsync(cancellationToken);
        var parents = edges.Count <= MaxEdges ? edges.ToLookup(e => e.ChildId, e => e.ParentId) : null;
        var above = new Dictionary<int, HashSet<int>>();
        var counts = new Dictionary<int, HashSet<int>>();
        foreach (var link in links)
        {
            Add(link.TagId, link.BookId);
            if (parents is null || !parents.Contains(link.TagId))
                continue;
            if (!above.TryGetValue(link.TagId, out var ancestors))
                above[link.TagId] = ancestors = Above(link.TagId, parents);
            foreach (var ancestor in ancestors)
                Add(ancestor, link.BookId);
        }

        void Add(int tag, int book)
        {
            if (!counts.TryGetValue(tag, out var tagged))
                counts[tag] = tagged = [];
            tagged.Add(book);
        }

        var ids = counts.Keys.ToList();
        var shown = await db.Tags.Where(t => EF.Parameter(ids).Contains(t.Id) && t.FieldKey == null && !t.IsHidden && t.BuiltIn == null)
            .Select(t => new { t.Id, t.FieldId })
            .ToListAsync(cancellationToken);
        return shown.Select(t => new Counted(t.Id, t.FieldId, counts[t.Id].Count)).ToList();
    }

    /// <summary>Every tag above <paramref name="tag"/> (the graph has no cycles; a visited set all the same).</summary>
    private static HashSet<int> Above(int tag, ILookup<int, int> parents)
    {
        var seen = new HashSet<int>();
        var next = new Stack<int>(parents[tag]);
        while (next.TryPop(out var parent))
        {
            if (seen.Add(parent))
            {
                foreach (var up in parents[parent])
                    next.Push(up);
            }
        }

        return seen;
    }

    private static List<Counted> Top(IEnumerable<Counted> values) =>
        values.OrderByDescending(v => v.Count).ThenBy(v => v.TagId).Take(MaxValues).ToList();

    /// <summary>A library seen whole: each field's values with the most books from <see cref="TagCount"/> (fields of few values only).</summary>
    private static async Task<FacetList> CountedAsync(LibrarionDbContext db, int libraryId, IReadOnlyDictionary<string, int> fields, CancellationToken cancellationToken)
    {
        var top = new Dictionary<int, List<Counted>>();
        foreach (var fieldId in fields.Values.Append(0))
        {
            int? field = fieldId == 0 ? null : fieldId;
            // Plain tags: the fields' own rows left out by id (with "FieldKey IS NULL" SQLite walked that index, nearly all
            // 660k tags). Values are never fields.
            var fieldIds = fields.Values.ToList();
            var values = field is null ? db.Tags.Where(t => t.FieldId == null && !fieldIds.Contains(t.Id)) : db.Tags.Where(t => t.FieldId == field);
            // Unordered: all of them or "too many", the order changes nothing.
            var ids = await values.Where(t => !t.IsHidden && t.BuiltIn == null).Select(t => t.Id)
                .Take(MaxValuesRead + 1).ToListAsync(cancellationToken);
            if (ids.Count == 0 || ids.Count > MaxValuesRead)
                continue;
            var counts = await db.TagCounts.Where(c => c.LibraryId == libraryId && EF.Parameter(ids).Contains(c.TagId) && c.BookCount > 0)
                .OrderByDescending(c => c.BookCount).ThenBy(c => c.TagId).Take(MaxValues)
                .Select(c => new Counted(c.TagId, field, c.BookCount))
                .ToListAsync(cancellationToken);
            if (counts.Count > 0)
                top[fieldId] = counts;
        }

        var books = await db.Libraries.Where(l => l.Id == libraryId).Select(l => l.BookCount).FirstOrDefaultAsync(cancellationToken);
        return new FacetList(books, false, Sorted(await NamedAsync(db, fields, top, cancellationToken)));
    }

    /// <summary>The values with their names, a facet per field (key 0: plain tags).</summary>
    private static async Task<List<Facet>> NamedAsync(LibrarionDbContext db, IReadOnlyDictionary<string, int> fields, Dictionary<int, List<Counted>> top,
        CancellationToken cancellationToken)
    {
        var ids = top.Values.SelectMany(v => v).Select(v => v.TagId).ToList();
        var names = await db.Tags.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
        var keys = fields.ToDictionary(f => f.Value, f => f.Key);
        return top.Where(t => t.Key == 0 || keys.ContainsKey(t.Key)).Select(t =>
        {
            var field = t.Key == 0 ? "tag" : keys[t.Key];
            return new Facet(field, t.Value.Select(v => new FacetValue(v.TagId, names[v.TagId], Term(field, names[v.TagId]), v.Count)).ToList());
        }).ToList();
    }

    /// <summary>The term finding a value's books: <c>author:"Stephen King"</c>, <c>tag:"Favourites"</c>.</summary>
    internal static string Term(string field, string name) =>
        // A name with quotes can't be quoted: its quotes become spaces (the words still match).
        $"{field}:\"{name.Replace('"', ' ')}\"";

    private static List<Facet> Sorted(IEnumerable<Facet> facets) =>
        facets.OrderBy(f => f.Field == "tag" ? int.MaxValue : Array.IndexOf(Order, f.Field) is var i and >= 0 ? i : Order.Length)
            .ThenBy(f => f.Field, StringComparer.Ordinal)
            .ToList();
}
