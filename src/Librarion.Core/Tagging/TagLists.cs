using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Search;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Tagging;

/// <summary>A field in the tag manager; <paramref name="Values"/> counted up to <see cref="TagLists.MaxCounted"/> (<paramref name="Capped"/> past it).</summary>
public sealed record ManagedField(int Id, string Key, string Name, bool BuiltIn, string? Color, bool IsHidden, int Values, bool Capped);

/// <summary>The fields, and the plain tags counted the same way.</summary>
public sealed record ManagedFields(IReadOnlyList<ManagedField> Fields, int PlainTags, bool PlainCapped);

/// <summary>
/// A tag in the tag manager: <paramref name="Books"/> its own links in every library (from the counts),
/// <paramref name="Children"/> the tags directly under it.
/// </summary>
public sealed record ManagedTag(int Id, string Name, string SortName, string? Color, bool IsCategory, bool IsHidden, bool BuiltIn, long Books, int Children);

/// <summary>A page of the manager's list; a search answers one page, <paramref name="Capped"/> when it found more.</summary>
public sealed record ManagedPage(IReadOnlyList<ManagedTag> Items, string? Next, bool Capped);

/// <summary>
/// What the tag manager lists (Administration › Catalog › Tags, stage 6): every tag of the tenant, books or not, never a
/// field loaded whole (650k authors at 2M books): pages by key on <c>(TenantId, FieldId, SortName, Id)</c>, searches by
/// name prefix on the normalized name's index and by word through the full-text index, capped; counts and children
/// read for the page's ids only.
/// </summary>
public sealed class TagLists(TenantDb tenantDb)
{
    public const int MaxCounted = 10_000;
    public const int MaxLimit = 200;

    public async Task<ManagedFields> FieldsAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var rows = await db.Tags.Where(t => t.FieldKey != null).OrderBy(t => t.FieldKey)
            .Select(t => new { t.Id, t.FieldKey, t.Name, t.BuiltIn, t.Color, t.IsHidden }).ToListAsync(cancellationToken);
        var order = BuiltInTags.Fields.Keys.ToList();
        var fields = new List<ManagedField>();
        foreach (var row in rows.OrderBy(f => f.BuiltIn is { } builtIn ? order.IndexOf(builtIn) : order.Count).ThenBy(f => f.FieldKey, StringComparer.Ordinal))
        {
            var id = row.Id;
            var values = await db.Tags.Where(t => t.FieldId == id).Take(MaxCounted + 1).CountAsync(cancellationToken);
            fields.Add(new ManagedField(row.Id, row.FieldKey!, row.Name, row.BuiltIn is not null, row.Color, row.IsHidden, Math.Min(values, MaxCounted), values > MaxCounted));
        }

        var plain = await db.Tags.Where(t => t.FieldId == null && t.FieldKey == null).Take(MaxCounted + 1).CountAsync(cancellationToken);
        return new ManagedFields(fields, Math.Min(plain, MaxCounted), plain > MaxCounted);
    }

    /// <summary>
    /// The tags of field <paramref name="fieldKey"/> (null: plain tags) by sort name, a page at a time; with
    /// <paramref name="query"/>, those whose name starts so or has its words (one capped page). Null for an unknown field.
    /// </summary>
    public async Task<ManagedPage?> ListAsync(int tenantId, string? fieldKey, string? query, string? after, int limit, CancellationToken cancellationToken = default)
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

        var tags = db.Tags.Where(t => t.FieldId == fieldId && t.FieldKey == null);
        List<int> ids;
        string? next = null;
        var capped = false;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var normalized = TagNames.Normalize(query);
            var prefixed = (await tags.Where(t => string.Compare(t.NormalizedName, normalized) >= 0).OrderBy(t => t.NormalizedName).ThenBy(t => t.Id)
                    .Select(t => new { t.Id, t.NormalizedName }).Take(limit + 1).ToListAsync(cancellationToken))
                .TakeWhile(t => t.NormalizedName.StartsWith(normalized, StringComparison.Ordinal)).Select(t => t.Id).ToList();
            var words = SearchText.Words(query);
            var named = new List<int>();
            if (words.Count > 0)
            {
                var matched = await SearchIndexes.For(db).Tags(db, new SearchMatch(words, SearchText.Spelled(query), Phrase: false, Prefix: true))
                    .Take(MaxCounted).ToListAsync(cancellationToken);
                // The index's matches checked against the field by key, a chunk at a time.
                foreach (var chunk in matched.Chunk(1000))
                {
                    named.AddRange(await tags.Where(t => chunk.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken));
                    if (named.Count > limit)
                        break;
                }
            }

            ids = prefixed.Concat(named).Distinct().ToList();
            capped = ids.Count > limit;
            ids = ids.Take(limit).ToList();
        }
        else
        {
            var cursor = KeysetCursor.Decode(after);
            var page = tags;
            if (cursor is { Text: { } name })
                page = page.Where(t => string.Compare(t.SortName, name) >= 0 && (string.Compare(t.SortName, name) > 0 || t.Id > cursor.Id));
            var rows = await page.OrderBy(t => t.SortName).ThenBy(t => t.Id).Take(limit + 1).Select(t => new { t.Id, t.SortName }).ToListAsync(cancellationToken);
            if (rows.Count > limit)
            {
                rows.RemoveAt(limit);
                next = new KeysetCursor(rows[^1].SortName, null, rows[^1].Id).Encode();
            }

            ids = rows.Select(r => r.Id).ToList();
        }

        var items = await db.Tags.Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, t.SortName, t.Color, t.IsCategory, t.IsHidden, t.BuiltIn }).ToListAsync(cancellationToken);
        var books = (await db.TagCounts.Where(c => ids.Contains(c.TagId)).GroupBy(c => c.TagId).Select(g => new { g.Key, Books = g.Sum(c => (long)c.BookCount) })
            .ToListAsync(cancellationToken)).ToDictionary(c => c.Key, c => c.Books);
        var children = (await db.TagParents.Where(p => ids.Contains(p.ParentId)).GroupBy(p => p.ParentId).Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)).ToDictionary(c => c.Key, c => c.Count);
        var byId = items.ToDictionary(t => t.Id);
        var ordered = string.IsNullOrWhiteSpace(query) ? ids : ids.OrderBy(id => byId[id].SortName, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new ManagedPage(ordered.Where(byId.ContainsKey).Select(id => byId[id])
            .Select(t => new ManagedTag(t.Id, t.Name, t.SortName, t.Color, t.IsCategory, t.IsHidden, t.BuiltIn is not null, books.GetValueOrDefault(t.Id), children.GetValueOrDefault(t.Id)))
            .ToList(), next, capped);
    }
}
