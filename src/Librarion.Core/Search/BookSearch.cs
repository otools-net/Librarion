using System.Linq.Expressions;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Search;

/// <summary>
/// A search as conditions on the books the caller sees (agents/docs/features/search.md): the same keyset pages as
/// every list, never a list of results read whole. Each term is a condition; the narrowest one (by its count, under
/// √(page × books) as for tags, D22) is where the database starts, the others are tested book by book as the sort
/// index is walked. Free words match titles (the full-text index) or names of tags through the tag-name index (D20).
/// Books with a hidden tag are left out unless the search names it.
/// </summary>
internal static class BookSearch
{
    /// <summary>The most tags a word or a name brings as a list; past it they are joined as a query.</summary>
    private const int MaxTags = BookQueries.MaxListedTags;

    /// <param name="Size">About how many books match; null when not known cheaply (then never where the database starts).</param>
    /// <param name="Named">Tags the term names, with those under them: hidden ones among them stay in.</param>
    /// <param name="Source">Up to about n ids of books it may match (unordered; repeats and books not seen possible): where a sample starts.</param>
    /// <param name="Start">Every book it may match, read into a list when under the threshold; null otherwise.</param>
    private sealed record Condition(SearchTerm Term, long? Size, Expression<Func<Book, bool>> From, Expression<Func<Book, bool>> Probe, IReadOnlyList<int> Named,
        Source Source, IReadOnlyList<int>? Start = null);

    private delegate Task<List<int>> Source(int n, CancellationToken cancellationToken);

    private static Source Reading(IQueryable<int> books) => (n, cancellationToken) => books.Take(n).ToListAsync(cancellationToken);

    private static Source Listed(IReadOnlyList<int> books) => (n, _) => Task.FromResult(books.Take(n).ToList());

    /// <summary>Titles read first (as many as were), then books through the links.</summary>
    private static Source Either(IReadOnlyList<int> titled, IQueryable<int> linked) => async (n, cancellationToken) =>
    {
        var books = titled.Take(n).ToList();
        if (books.Count < n)
            books.AddRange(await linked.Take(n - books.Count).ToListAsync(cancellationToken));
        return books;
    };

    /// <summary>The tenant's fields by key: what <c>key:</c> the search language knows.</summary>
    public static async Task<Dictionary<string, int>> FieldsAsync(LibrarionDbContext db, CancellationToken cancellationToken) =>
        // In key order from the index (TenantId, FieldKey): with "IS NOT NULL" alone SQLite scanned all 660k tags.
        (await db.Tags.Where(t => t.FieldKey != null).OrderBy(t => t.FieldKey).Select(t => new { t.FieldKey, t.Id }).ToListAsync(cancellationToken))
            .ToDictionary(t => t.FieldKey!, t => t.Id, StringComparer.Ordinal);

    /// <summary>
    /// <see cref="FieldsAsync"/> and the keys of fields merged into others (<c>genre</c> → the <c>category</c> field's
    /// id): what a query is read with. Listing fields uses <see cref="FieldsAsync"/>, which names each field once.
    /// </summary>
    public static async Task<Dictionary<string, int>> KeysAsync(LibrarionDbContext db, CancellationToken cancellationToken)
    {
        var keys = await FieldsAsync(db, cancellationToken);
        foreach (var alias in await db.FieldKeyAliases.OrderBy(a => a.Key).Select(a => new { a.Key, a.FieldId }).ToListAsync(cancellationToken))
            keys.TryAdd(alias.Key, alias.FieldId);
        return keys;
    }

    public static async Task<IQueryable<Book>> ApplyAsync(LibrarionDbContext db, IQueryable<Book> books, SearchQuery query, IReadOnlyDictionary<string, int> fields,
        IReadOnlyCollection<int> libraries, int limit, CancellationToken cancellationToken)
    {
        if (query.IsEmpty)
            return books;
        var total = await db.Libraries.Where(l => libraries.Contains(l.Id)).SumAsync(l => (long)l.BookCount, cancellationToken);
        var threshold = (long)Math.Sqrt((double)limit * Math.Max(total, 1));
        var conditions = await ResolveAsync(db, query, fields, libraries, threshold, cancellationToken);
        var start = conditions.Where(c => !c.Term.Negated && c.Size < threshold).MinBy(c => c.Size);
        return await FilterAsync(db, books, conditions, start, cancellationToken);
    }

    /// <summary>
    /// Some of the books matching, at most <paramref name="cap"/>, for counts over them (facets,
    /// agents/docs/features/search.md): the narrowest term's books read unordered from its own index, then tested
    /// against every term. <c>Capped</c>: more may match than were read.
    /// </summary>
    public static async Task<(List<int> Books, bool Capped)> SampleAsync(LibrarionDbContext db, IQueryable<Book> books, SearchQuery query,
        IReadOnlyDictionary<string, int> fields, IReadOnlyCollection<int> libraries, int cap, CancellationToken cancellationToken)
    {
        var conditions = await ResolveAsync(db, query, fields, libraries, cap, cancellationToken);
        var matching = await FilterAsync(db, books, conditions, null, cancellationToken);
        var start = conditions.Where(c => !c.Term.Negated).MinBy(c => c.Size ?? long.MaxValue);
        if (start is null)
        {
            // Only terms leaving books out: the first matches by title (the sort index walked, no scan).
            var walked = await matching.OrderBy(b => b.SortTitle).ThenBy(b => b.Id).Select(b => b.Id).Take(cap + 1).ToListAsync(cancellationToken);
            return (walked.Take(cap).ToList(), walked.Count > cap);
        }

        var read = start.Start?.ToList() ?? await start.Source(cap + 1, cancellationToken);
        var candidates = read.Distinct().Take(cap).ToList();
        if (candidates.Count == 0)
            return ([], false);
        var matched = await matching.Where(b => EF.Parameter(candidates).Contains(b.Id)).Select(b => b.Id).ToListAsync(cancellationToken);
        return (matched, read.Count > cap);
    }

    private static async Task<List<Condition>> ResolveAsync(LibrarionDbContext db, SearchQuery query, IReadOnlyDictionary<string, int> fields,
        IReadOnlyCollection<int> libraries, long threshold, CancellationToken cancellationToken)
    {
        var conditions = new List<Condition>();
        foreach (var term in query.Terms)
            conditions.Add(await ResolveAsync(db, term, fields, libraries, threshold, cancellationToken));
        return conditions;
    }

    /// <summary>Every term as a condition on the books (<paramref name="start"/> as where the database starts); hidden tags' books left out.</summary>
    private static async Task<IQueryable<Book>> FilterAsync(LibrarionDbContext db, IQueryable<Book> books, IReadOnlyList<Condition> conditions, Condition? start,
        CancellationToken cancellationToken)
    {
        foreach (var condition in conditions)
            books = books.Where(condition.Term.Negated ? Not(condition.Probe) : condition == start ? condition.From : condition.Probe);

        // Hidden tags (Archived…) keep their books out of searches that don't name them (tags.md); a filtered index finds the few.
        var named = conditions.Where(c => !c.Term.Negated).SelectMany(c => c.Named).ToHashSet();
        // Read whole: the filtered index holds only the hidden ones (ordered and cut, SQLite walked every tag by id instead).
        var hidden = (await db.Tags.Where(t => t.IsHidden).Select(t => t.Id).ToListAsync(cancellationToken))
            .Where(id => !named.Contains(id)).Order().Take(MaxTags).ToList();
        if (hidden.Count > 0)
            books = books.Where(Not(BookQueries.Probe(db, await BookQueries.DescendantsAsync(db, hidden, cancellationToken))));
        return books;
    }

    private static async Task<Condition> ResolveAsync(LibrarionDbContext db, SearchTerm term, IReadOnlyDictionary<string, int> fields, IReadOnlyCollection<int> libraries,
        long threshold, CancellationToken cancellationToken)
    {
        switch (term.Kind)
        {
            case SearchTermKind.Format:
                var format = term.Value.Trim().TrimStart('.').ToLowerInvariant();
                Expression<Func<Book, bool>> hasFormat = b => db.BookFiles.Any(f => f.BookId == b.Id && f.Format == format);
                return new Condition(term, null, hasFormat, hasFormat, [], Reading(db.BookFiles.Where(f => f.Format == format).Select(f => f.BookId)));
            case SearchTermKind.Field:
                return await TagsAsync(db, term, fields[term.Key!], term.Value, libraries, threshold, cancellationToken);
            case SearchTermKind.Tag:
                // tag:"author:Stephen King" is a field's value; tag:Favourites a plain tag.
                var colon = term.Value.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && fields.TryGetValue(term.Value[..colon].Trim().ToLowerInvariant(), out var field))
                    return await TagsAsync(db, term, field, term.Value[(colon + 1)..].Trim(), libraries, threshold, cancellationToken);
                return await TagsAsync(db, term, null, term.Value, libraries, threshold, cancellationToken);
            default:
                return await WordsAsync(db, term, libraries, threshold, cancellationToken);
        }
    }

    /// <summary>Words in titles (<c>title:</c>) or, for free text, in titles or tag names.</summary>
    private static async Task<Condition> WordsAsync(LibrarionDbContext db, SearchTerm term, IReadOnlyCollection<int> libraries, long threshold, CancellationToken cancellationToken)
    {
        var index = SearchIndexes.For(db);
        var match = new SearchMatch(SearchText.Words(term.Value), SearchText.Spelled(term.Value), term.Quoted, term.Prefix);
        // Read up to the threshold, unordered (ordered, SQLite sorted every match): that many are a list the database
        // starts from (seeks, then only they are sorted; from the index's query PostgreSQL computed every title's
        // words while walking the sort index); more are tested on each book walked (TitleTest, Linked).
        var cap = (int)Math.Min(threshold + 1, int.MaxValue);
        var titled = await index.Books(db, match).Take(cap).ToListAsync(cancellationToken);
        long size = titled.Count;
        var title = index.TitleTest(db, match);
        var titles = size <= threshold ? titled : null;
        if (term.Kind == SearchTermKind.Title)
            return new Condition(term, size, b => EF.Parameter(titled).Contains(b.Id), title, [], Listed(titled), titles);

        var tags = await MatchedTagsAsync(index.Tags(db, match), db.Tags.Where(t => t.FieldKey == null), cancellationToken);
        if (tags is null)
        {
            // A word in very many names ("the"): each book's links tested against the index's tags (read once).
            var matched = index.Tags(db, match);
            var named = BookQueries.Or(title, Linked(db, matched));
            return new Condition(term, null, named, named, [], Either(titled, db.BookTags.Where(l => matched.Contains(l.TagId)).Select(l => l.BookId)));
        }

        var either = BookQueries.Or(title, Linked(db, tags));
        if (tags.Count == 0)
            return new Condition(term, size, b => EF.Parameter(titled).Contains(b.Id), title, [], Listed(titled), titles);
        size += await CountAsync(db, tags, libraries, cancellationToken);
        var links = db.BookTags.Where(l => tags.Contains(l.TagId)).Select(l => l.BookId);
        if (size >= threshold)
            return new Condition(term, size, either, either, tags, Either(titled, links));
        // Plain, made distinct below: as for a field's values (DISTINCT read PostgreSQL's whole key of BookTags).
        var linked = await links.Take(cap).ToListAsync(cancellationToken);
        var start = titled.Concat(linked).Distinct().ToList();
        return new Condition(term, size, b => EF.Parameter(start).Contains(b.Id), either, tags, Listed(start), start);
    }

    /// <summary>
    /// A field's values (<paramref name="fieldId"/>; null: plain tags) by name: the name or an alias exactly, else by
    /// prefix (<c>*</c>), else the field's values having the words (<c>author:king</c> finds Stephen King); with the
    /// tags under them. <c>author:</c> alone: any value of the field.
    /// </summary>
    private static async Task<Condition> TagsAsync(LibrarionDbContext db, SearchTerm term, int? fieldId, string value, IReadOnlyCollection<int> libraries,
        long threshold, CancellationToken cancellationToken)
    {
        if (value.Length == 0 && fieldId is { } any)
        {
            var values = new BookQueries.TagSet([any], null);
            return new Condition(term, null, BookQueries.From(db, values), BookQueries.Probe(db, values), [],
                Reading(db.BookTags.Where(l => db.TagClosure.Where(c => c.AncestorId == any).Select(c => c.DescendantId).Contains(l.TagId)).Select(l => l.BookId)));
        }

        var normalized = TagNames.Normalize(value);
        var inField = db.Tags.Where(t => t.FieldId == fieldId && t.FieldKey == null);
        List<int> tags;
        IQueryable<int>? query = null;
        if (term.Prefix)
        {
            // From the prefix on, in the index's order; those still starting with it kept.
            tags = (await inField.Where(t => string.Compare(t.NormalizedName, normalized) >= 0).OrderBy(t => t.NormalizedName).ThenBy(t => t.Id)
                    .Select(t => new { t.Id, t.NormalizedName }).Take(MaxTags + 1).ToListAsync(cancellationToken))
                .TakeWhile(t => t.NormalizedName.StartsWith(normalized, StringComparison.Ordinal)).Select(t => t.Id).ToList();
            if (tags.Count > MaxTags)
                query = inField.Where(t => string.Compare(t.NormalizedName, normalized) >= 0 && t.NormalizedName.StartsWith(normalized)).Select(t => t.Id);
        }
        else
        {
            tags = await inField.Where(t => t.NormalizedName == normalized).OrderBy(t => t.Id).Select(t => t.Id).Take(MaxTags).ToListAsync(cancellationToken);
            // The aliases' tags first, then those of the field among them by key (from the tags, SQLite walked them all).
            var aliased = await db.TagAliases.Where(a => a.NormalizedName == normalized).Select(a => a.TagId).OrderBy(id => id).Take(MaxTags).ToListAsync(cancellationToken);
            if (aliased.Count > 0)
                tags.AddRange(await inField.Where(t => aliased.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken));
            if (tags.Count == 0 && SearchText.Words(value).Count > 0)
            {
                var named = SearchIndexes.For(db).Tags(db, new SearchMatch(SearchText.Words(value), SearchText.Spelled(value), term.Quoted, Prefix: false));
                if (await MatchedTagsAsync(named, inField, cancellationToken) is { } matched)
                    tags = matched;
                else
                    query = inField.Where(t => named.Contains(t.Id)).Select(t => t.Id);
            }

            tags = tags.Distinct().ToList();
        }

        if (query is not null)
        {
            var linked = Linked(db, query);
            return new Condition(term, null, linked, linked, [], Reading(db.BookTags.Where(l => query.Contains(l.TagId)).Select(l => l.BookId)));
        }

        if (tags.Count == 0)
            return new Condition(term, 0, b => false, b => false, [], Listed([]), []);
        var set = await BookQueries.DescendantsAsync(db, tags, cancellationToken);
        var listed = set.Descendants;
        var size = await CountAsync(db, listed ?? tags, libraries, cancellationToken);
        // Few books: read into a list the database starts from (from the links' query, PostgreSQL walked the sort index
        // and sought every listed tag for each book). Many: each book's links tested (descendants past 500 through the closure).
        var probe = listed is null ? BookQueries.Probe(db, set) : Linked(db, listed);
        var ancestors = set.Ancestors;
        var books = listed is null
            ? db.BookTags.Where(l => db.TagClosure.Where(c => ancestors.Contains(c.AncestorId)).Select(c => c.DescendantId).Contains(l.TagId)).Select(l => l.BookId)
            : db.BookTags.Where(l => listed.Contains(l.TagId)).Select(l => l.BookId);
        if (size >= threshold)
            return new Condition(term, size, probe, probe, listed ?? tags, Reading(books));
        // Fewer links than the threshold (their count says so): read plain, made distinct here (DISTINCT in the query read
        // PostgreSQL's whole key of BookTags for ids in order, 1.6 s).
        var start = (await books.Take((int)Math.Min(threshold + 1, int.MaxValue)).ToListAsync(cancellationToken)).Distinct().ToList();
        return new Condition(term, size, b => EF.Parameter(start).Contains(b.Id), probe, listed ?? tags, Listed(start), start);
    }

    /// <summary>Most tag ids read from the name index for one term: past it the term is joined as a query.</summary>
    private const int MaxMatched = 2000;

    /// <summary>
    /// The tags of <paramref name="scope"/> among the name index's matches; null when it has more than
    /// <see cref="MaxMatched"/> (too many to list). The ids come from the index first, then are looked up by key: a query
    /// starting from the tags walked all of them in id order.
    /// </summary>
    internal static async Task<List<int>?> MatchedTagsAsync(IQueryable<int> matched, IQueryable<Tag> scope, CancellationToken cancellationToken)
    {
        // Unordered on purpose (all of them or "too many": the order changes nothing; ordered, SQLite sorted every match).
        var ids = await matched.Take(MaxMatched + 1).ToListAsync(cancellationToken);
        if (ids.Count > MaxMatched)
            return null;
        return await scope.Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken);
    }

    // Lists of thousands of ids go as one parameter (EF.Parameter: SQLite reads it through json_each): EF's default, a
    // parameter per id, made SQLite compile statements of 10,000 parameters anew each time (330 ms for a search).

    /// <summary>
    /// Books linked to one of <paramref name="tags"/>, tested book by book. SQLite: <c>TagId + 0</c> reads the book's
    /// links by its key and tests them (with the plain column it sought every listed tag for each book, 1 s for a common
    /// word). PostgreSQL: the redundant <c>BookId &lt;= b.Id</c> keeps the test on each book: with the equality alone it
    /// turned it into "among all books linked to these tags" and read every one of their links first (BookTags whole
    /// for a common word).
    /// </summary>
    private static Expression<Func<Book, bool>> Linked(LibrarionDbContext db, IReadOnlyList<int> tags) =>
        db.Database.IsSqlite()
            ? b => db.BookTags.Any(l => l.BookId == b.Id && EF.Parameter(tags).Contains(l.TagId + 0))
            : b => db.BookTags.Any(l => l.BookId == b.Id && l.BookId <= b.Id && EF.Parameter(tags).Contains(l.TagId));

    private static Expression<Func<Book, bool>> Linked(LibrarionDbContext db, IQueryable<int> tags) =>
        db.Database.IsSqlite()
            ? b => db.BookTags.Any(l => l.BookId == b.Id && tags.Contains(l.TagId + 0))
            : b => db.BookTags.Any(l => l.BookId == b.Id && l.BookId <= b.Id && tags.Contains(l.TagId));

    /// <summary>The tags' links in the libraries (<see cref="TagCount"/>): a book with two of them counts twice, near enough.</summary>
    private static async Task<long> CountAsync(LibrarionDbContext db, IReadOnlyList<int> tags, IReadOnlyCollection<int> libraries, CancellationToken cancellationToken) =>
        await db.TagCounts.Where(c => tags.Contains(c.TagId) && libraries.Contains(c.LibraryId)).SumAsync(c => (long)c.BookCount, cancellationToken);

    private static Expression<Func<Book, bool>> Not(Expression<Func<Book, bool>> condition) =>
        Expression.Lambda<Func<Book, bool>>(Expression.Not(condition.Body), condition.Parameters);
}
