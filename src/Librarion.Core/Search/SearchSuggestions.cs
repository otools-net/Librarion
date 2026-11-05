using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Search;

public enum SuggestionKind
{
    /// <summary>A field's key to type a value of (<c>author:</c>).</summary>
    Field,
    /// <summary>A field's value or a plain tag, as a term (<c>author:"Stephen King"</c>, <c>tag:"Favourites"</c>).</summary>
    Value,
}

/// <param name="Insert">The term put in place of the one being typed.</param>
/// <param name="Name">The value's name, or the field's key (the client names the field).</param>
/// <param name="Field">The value's field (its key); null for a plain tag or a field.</param>
/// <param name="BookCount">Its books in the library asked about; null without one.</param>
public sealed record SearchSuggestion(SuggestionKind Kind, string Insert, string Name, string? Field, int? BookCount);

/// <param name="From">Where the term being typed starts in the query: the client replaces from there.</param>
public sealed record SuggestionList(int From, IReadOnlyList<SearchSuggestion> Items);

/// <summary>
/// What the search box offers as you type (agents/docs/features/search.md, D-5.7): for the last term, the fields whose
/// key starts with it, then the field values and tags whose name has its words (the tag-name index, D20); after
/// <c>author:</c>, that field's values by prefix and by word. Bounded reads only; tags the caller's filters deny never
/// show; with a library, the most used there first and those without books there left out.
/// </summary>
public sealed class SearchSuggestions(TenantDb tenantDb)
{
    public const int MaxValues = 8;
    private const int MaxFields = 3;

    /// <summary>Candidates read before ordering by count: more would only reorder the tail.</summary>
    private const int MaxCandidates = 200;

    public async Task<SuggestionList> SuggestAsync(int tenantId, string? query, int? libraryId, BookVisibility visibility, CancellationToken cancellationToken = default)
    {
        var text = query ?? "";
        var from = LastTermStart(text);
        var term = text[from..];
        var negated = term.StartsWith('-');
        if (negated)
            term = term[1..];
        if (term.Length == 0)
            return new SuggestionList(from, []);

        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var fields = await BookSearch.FieldsAsync(db, cancellationToken);
        var keys = fields.ToDictionary(f => f.Value, f => f.Key);
        var sign = negated ? "-" : "";

        var colon = term.IndexOf(':', StringComparison.Ordinal);
        var key = colon > 0 ? term[..colon].ToLowerInvariant() : null;
        var known = key is null || fields.ContainsKey(key) ? fields : await BookSearch.KeysAsync(db, cancellationToken);
        if (key is not null && (key == "tag" || known.ContainsKey(key)))
        {
            var value = term[(colon + 1)..].Trim('"', '*', ' ');
            var fieldId = key == "tag" ? (int?)null : known[key];
            var values = await ValuesAsync(db, fieldId, value, libraryId, visibility, cancellationToken);
            return new SuggestionList(from, values.Select(v => Suggestion(v, keys, sign)).ToList());
        }

        var words = term.Trim('"', '*', ' ');
        if (words.Length < 2)
            return new SuggestionList(from, []);
        var items = fields.Keys.Where(k => k.StartsWith(words.ToLowerInvariant(), StringComparison.Ordinal)).Order(StringComparer.Ordinal).Take(MaxFields)
            .Select(k => new SearchSuggestion(SuggestionKind.Field, $"{sign}{k}:", k, null, null)).ToList();
        var matched = await NamedAsync(db, db.Tags.Where(t => t.FieldKey == null), words, libraryId, visibility, cancellationToken);
        items.AddRange(matched.Select(v => Suggestion(v, keys, sign)));
        return new SuggestionList(from, items);
    }

    private sealed record Candidate(int Id, string Name, int? FieldId, int? BookCount);

    /// <summary>A field's values (null: plain tags): by prefix of the name and by its words; the field's first ones when nothing is typed.</summary>
    private static async Task<List<Candidate>> ValuesAsync(LibrarionDbContext db, int? fieldId, string value, int? libraryId, BookVisibility visibility, CancellationToken cancellationToken)
    {
        var inField = db.Tags.Where(t => t.FieldId == fieldId && t.FieldKey == null);
        if (value.Length == 0)
            return await RankAsync(db, await inField.OrderBy(t => t.SortName).ThenBy(t => t.Id).Take(MaxCandidates).Select(t => new Candidate(t.Id, t.Name, t.FieldId, null))
                .ToListAsync(cancellationToken), libraryId, visibility, byCount: false, cancellationToken);

        var normalized = TagNames.Normalize(value);
        var prefixed = (await inField.Where(t => string.Compare(t.NormalizedName, normalized) >= 0).OrderBy(t => t.NormalizedName).ThenBy(t => t.Id)
                .Select(t => new { Candidate = new Candidate(t.Id, t.Name, t.FieldId, null), t.NormalizedName }).Take(MaxCandidates).ToListAsync(cancellationToken))
            .TakeWhile(t => t.NormalizedName.StartsWith(normalized, StringComparison.Ordinal)).Select(t => t.Candidate).ToList();
        var named = await NamedAsync(db, inField, value, libraryId, visibility, cancellationToken);
        var ranked = await RankAsync(db, prefixed, libraryId, visibility, byCount: true, cancellationToken);
        return ranked.Concat(named).DistinctBy(c => c.Id).Take(MaxValues).ToList();
    }

    /// <summary>Tags of <paramref name="tags"/> with every word of <paramref name="words"/> (the last one as a prefix) in their name.</summary>
    private static async Task<List<Candidate>> NamedAsync(LibrarionDbContext db, IQueryable<Tag> tags, string words, int? libraryId, BookVisibility visibility, CancellationToken cancellationToken)
    {
        var folded = SearchText.Words(words);
        if (folded.Count == 0)
            return [];
        var matched = SearchIndexes.For(db).Tags(db, new SearchMatch(folded, SearchText.Spelled(words), Phrase: false, Prefix: true));
        // The index's first matches (unordered: ordered, SQLite sorted them all), then their rows by key (from the tags,
        // the query walked all of them).
        var ids = await matched.Take(MaxCandidates).ToListAsync(cancellationToken);
        var candidates = await tags.Where(t => ids.Contains(t.Id)).Select(t => new Candidate(t.Id, t.Name, t.FieldId, null)).ToListAsync(cancellationToken);
        return await RankAsync(db, candidates, libraryId, visibility, byCount: true, cancellationToken);
    }

    /// <summary>With their counts in the library (those without books there left out), the denied ones out; the most used first.</summary>
    private static async Task<List<Candidate>> RankAsync(LibrarionDbContext db, List<Candidate> candidates, int? libraryId, BookVisibility visibility, bool byCount,
        CancellationToken cancellationToken)
    {
        var denied = await TagQueries.DeniedAsync(db, libraryId, visibility, cancellationToken);
        candidates = candidates.Where(c => !denied.Contains(c.Id)).ToList();
        if (libraryId is { } library)
        {
            var ids = candidates.Select(c => c.Id).ToList();
            var counts = await db.TagCounts.Where(c => c.LibraryId == library && ids.Contains(c.TagId)).ToDictionaryAsync(c => c.TagId, c => c.BookCount, cancellationToken);
            candidates = candidates.Where(c => counts.GetValueOrDefault(c.Id) > 0).Select(c => c with { BookCount = counts[c.Id] }).ToList();
        }

        return (byCount ? candidates.OrderByDescending(c => c.BookCount).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase) : candidates.AsEnumerable())
            .Take(MaxValues).ToList();
    }

    private static SearchSuggestion Suggestion(Candidate value, IReadOnlyDictionary<int, string> keys, string sign)
    {
        var field = value.FieldId is { } id ? keys.GetValueOrDefault(id) : null;
        return new SearchSuggestion(SuggestionKind.Value, sign + SearchFacets.Term(field ?? "tag", value.Name), value.Name, field, value.BookCount);
    }

    /// <summary>Where the last term starts: after the last space outside quotes.</summary>
    internal static int LastTermStart(string text)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
                quoted = !quoted;
            else if (char.IsWhiteSpace(text[i]) && !quoted)
                start = i + 1;
        }

        return start;
    }
}
