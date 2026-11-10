using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Search;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Tagging;

/// <summary>
/// A tag the picker offers: <paramref name="Alias"/> the other name it was found by (null: by its own name or words),
/// <paramref name="Parent"/> the parent that tells it apart from a namesake ("Freddy (FNAF)"), <paramref name="Books"/>
/// its books with those under it in every library.
/// </summary>
public sealed record PickedTag(int Id, string Name, string? Color, string? Field, string? Shorthand, string? Alias, string? Parent, long Books);

/// <summary>What the picker found; <paramref name="Exact"/> when a tag has the typed name itself (no "Create" then).</summary>
public sealed record PickedTags(IReadOnlyList<PickedTag> Items, bool Exact);

/// <summary>
/// The shared tag picker's lookup (stage 6, chunk 5; SFE's picker): the tags of one field (null: plain tags) whose name
/// or one of whose other names starts with what is typed, whose shorthand is it, or whose name has its words (the
/// tag-name index); the typed name first, then the most used. Every read is bounded: a prefix range on the normalized
/// name's index, the aliases' index, the shorthand's index, the full-text index capped at
/// <see cref="MaxCandidates"/>; counts read for the candidates only. Tags the caller's filters deny never show.
/// </summary>
public sealed class TagPicker(TenantDb tenantDb)
{
    public const int MaxLimit = 50;

    /// <summary>Candidates read per way of matching before ranking: more would only reorder the tail.</summary>
    public const int MaxCandidates = 100;

    /// <summary>Null for an unknown field.</summary>
    public async Task<PickedTags?> FindAsync(int tenantId, string? fieldKey, string? query, int limit, BookVisibility? visibility = null,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        int? fieldId = null;
        if (fieldKey is not null)
        {
            fieldId = await db.Tags.Where(t => t.FieldKey == fieldKey).Select(t => (int?)t.Id).FirstOrDefaultAsync(cancellationToken);
            if (fieldId is null)
                return null;
        }

        var text = (query ?? "").Trim();
        var inField = db.Tags.Where(t => t.FieldId == fieldId && t.FieldKey == null);
        var aliasOf = new Dictionary<int, string>();
        List<int> ids;
        if (text.Length == 0)
        {
            ids = await inField.OrderBy(t => t.SortName).ThenBy(t => t.Id).Select(t => t.Id).Take(MaxCandidates).ToListAsync(cancellationToken);
        }
        else
        {
            var normalized = TagNames.Normalize(text);
            var named = (await inField.Where(t => string.Compare(t.NormalizedName, normalized) >= 0).OrderBy(t => t.NormalizedName).ThenBy(t => t.Id)
                    .Select(t => new { t.Id, t.NormalizedName }).Take(MaxCandidates).ToListAsync(cancellationToken))
                .TakeWhile(t => t.NormalizedName.StartsWith(normalized, StringComparison.Ordinal)).Select(t => t.Id).ToList();

            // Other names by prefix over the tenant's aliases (their index), then kept to the field.
            var aliases = (await db.TagAliases.Where(a => string.Compare(a.NormalizedName, normalized) >= 0).OrderBy(a => a.NormalizedName).ThenBy(a => a.Id)
                    .Select(a => new { a.TagId, a.Name, a.NormalizedName }).Take(MaxCandidates).ToListAsync(cancellationToken))
                .TakeWhile(a => a.NormalizedName.StartsWith(normalized, StringComparison.Ordinal)).ToList();
            var aliased = aliases.Select(a => a.TagId).Distinct().ToList();
            if (aliased.Count > 0)
                aliased = await inField.Where(t => aliased.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken);
            foreach (var alias in aliases.Where(a => aliased.Contains(a.TagId)))
                aliasOf.TryAdd(alias.TagId, alias.Name);

            // A shorthand is matched whole, as typed or in either case ("sf", "SF").
            var shorthands = new[] { text, text.ToUpperInvariant(), text.ToLowerInvariant() }.Distinct().ToList();
            var shorthand = await inField.Where(t => t.Shorthand != null && shorthands.Contains(t.Shorthand)).Select(t => t.Id).Take(MaxCandidates).ToListAsync(cancellationToken);

            var worded = new List<int>();
            var words = SearchText.Words(text);
            if (words.Count > 0)
            {
                var matched = await SearchIndexes.For(db).Tags(db, new SearchMatch(words, SearchText.Spelled(text), Phrase: false, Prefix: true))
                    .Take(MaxCandidates).ToListAsync(cancellationToken);
                if (matched.Count > 0)
                    worded = await inField.Where(t => matched.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken);
            }

            ids = shorthand.Concat(named).Concat(aliased).Concat(worded).Distinct().ToList();
            // The other name shows only when the tag wasn't found by its own.
            foreach (var id in shorthand.Concat(named).Concat(worded))
                aliasOf.Remove(id);
        }

        var denied = await TagQueries.DeniedAsync(db, null, visibility, cancellationToken);
        ids = ids.Where(id => !denied.Contains(id)).ToList();
        if (ids.Count == 0)
            return new PickedTags([], false);
        var rows = await db.Tags.Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, t.NormalizedName, t.SortName, t.Color, t.Shorthand, t.DisambiguationParentId })
            .ToListAsync(cancellationToken);
        var books = await db.TagCounts.Where(c => ids.Contains(c.TagId)).GroupBy(c => c.TagId).Select(g => new { g.Key, Books = g.Sum(c => (long)c.TotalCount) })
            .ToDictionaryAsync(c => c.Key, c => c.Books, cancellationToken);
        var parentIds = rows.Where(r => r.DisambiguationParentId is not null).Select(r => r.DisambiguationParentId!.Value).Distinct().ToList();
        // Each list read only when it holds ids: an empty one is a query of nothing (SQLite plans it as a scan).
        var parents = parentIds.Count == 0 ? []
            : await db.Tags.Where(t => parentIds.Contains(t.Id)).Select(t => new { t.Id, t.Name }).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var typed = TagNames.Normalize(text);
        var exact = text.Length > 0 && rows.Any(r => r.NormalizedName == typed);
        var ordered = text.Length == 0
            ? rows.OrderBy(r => r.SortName, StringComparer.CurrentCultureIgnoreCase)
            : rows.OrderByDescending(r => r.NormalizedName == typed).ThenByDescending(r => books.GetValueOrDefault(r.Id)).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);
        return new PickedTags(ordered.Take(limit)
            .Select(r => new PickedTag(r.Id, r.Name, r.Color, fieldKey, r.Shorthand, aliasOf.GetValueOrDefault(r.Id),
                r.DisambiguationParentId is { } parent ? parents.GetValueOrDefault(parent) : null, books.GetValueOrDefault(r.Id)))
            .ToList(), exact);
    }
}
