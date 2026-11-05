using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Catalog;

/// <param name="BookCount">Books of the library with it or a tag under it (<see cref="TagCount.TotalCount"/>); null when no library was named.</param>
public sealed record FieldValue(int Id, string Name, string SortName, int? BookCount);

/// <param name="BuiltIn">One of the fields every tenant has (its name is translated by the client from its key).</param>
public sealed record FieldInfo(int Id, string Key, string Name, bool BuiltIn);

/// <param name="Field">The field's key; null for a plain tag.</param>
public sealed record TagRef(int Id, string Name, string? Field);

/// <param name="Field">The field it is a value of (its key); null for a plain tag.</param>
/// <param name="FieldName">That field's own name (a custom field's; a built-in one is translated from its key).</param>
/// <param name="BookCount">Books of the library with it or a tag under it; null when no library was named.</param>
/// <param name="Parents">The tags it is under ("Horror" under "Fiction"), by name.</param>
/// <param name="Children">The tags under it, by name, at most <see cref="TagQueries.MaxChildren"/>.</param>
/// <param name="MoreChildren">More tags are under it than <paramref name="Children"/> holds (those are then any of them).</param>
public sealed record TagDetail(
    int Id, string Name, string SortName, string? Field, string? FieldName, bool IsCategory, int? BookCount,
    IReadOnlyList<TagRef> Parents, IReadOnlyList<TagRef> Children, bool MoreChildren);

/// <summary>
/// Field values (authors A–Z, series, categories…) and plain tags by sort name, keyset on the index (TenantId, FieldId,
/// SortName, Id); counts read from <see cref="TagCount"/>, never counted (agents/docs/features/metadata-tags.md,
/// agents/docs/features/browse.md). With a library, values without a book there are skipped, a bounded number of rows
/// read per request (<see cref="MaxScanned"/>): a sparse library's list comes in short pages, never in one long scan.
/// </summary>
public sealed class TagQueries(TenantDb tenantDb)
{
    public const int MaxLimit = 200;

    /// <summary>Rows read at most for one page when values without books in the library are skipped.</summary>
    public const int MaxScanned = 2000;

    public const int MaxChildren = 50;

    /// <summary>Denied tags listed with their descendants at most; past it the list is cut (a broad deny filter).</summary>
    private const int MaxDenied = 500;

    /// <summary>The tenant's fields: the built-in ones in their order, then the others by name.</summary>
    public async Task<IReadOnlyList<FieldInfo>> FieldsAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        // In key order: the index (TenantId, FieldKey) is walked; with "IS NOT NULL" alone SQLite scanned every tag.
        var fields = await db.Tags.Where(t => t.FieldKey != null)
            .OrderBy(t => t.FieldKey)
            .Select(t => new { t.Id, t.FieldKey, t.Name, t.BuiltIn })
            .ToListAsync(cancellationToken);
        var order = BuiltInTags.Fields.Keys.ToList();
        return fields
            .OrderBy(f => f.BuiltIn is { } builtIn ? order.IndexOf(builtIn) : order.Count)
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new FieldInfo(f.Id, f.FieldKey!, f.Name, f.BuiltIn is not null))
            .ToList();
    }

    /// <summary>The values of the field <paramref name="fieldKey"/> by sort name.</summary>
    /// <param name="from">Where to start when there is no <paramref name="after"/>: the first value sorted at or after it (its first letter upper case).</param>
    /// <returns>Null when the tenant has no field with this key.</returns>
    public async Task<Page<FieldValue>?> ValuesAsync(int tenantId, string fieldKey, int? libraryId, string? after, int limit,
        CancellationToken cancellationToken = default, string? from = null, BookVisibility? visibility = null)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var fieldId = await db.Tags.Where(t => t.FieldKey == fieldKey).Select(t => (int?)t.Id).FirstOrDefaultAsync(cancellationToken);
        if (fieldId is null)
            return null;
        return await PageAsync(db, db.Tags.Where(t => t.FieldId == fieldId), libraryId, after, from, limit, visibility, cancellationToken);
    }

    /// <summary>The plain tags (no field, not a field) by sort name; as <see cref="ValuesAsync"/>.</summary>
    public async Task<Page<FieldValue>> PlainTagsAsync(int tenantId, int? libraryId, string? after, int limit,
        CancellationToken cancellationToken = default, string? from = null, BookVisibility? visibility = null)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        return await PageAsync(db, db.Tags.Where(t => t.FieldId == null && t.FieldKey == null), libraryId, after, from, limit, visibility, cancellationToken);
    }

    /// <summary>One tag with its field, parents, children and count; null when there is none or the caller's filters deny it.</summary>
    public async Task<TagDetail?> GetAsync(int tenantId, int tagId, int? libraryId, BookVisibility? visibility = null, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var tag = await db.Tags.Where(t => t.Id == tagId)
            .Select(t => new { t.Id, t.Name, t.SortName, t.IsCategory, t.FieldKey, Field = t.Field == null ? null : new { t.Field.FieldKey, t.Field.Name } })
            .FirstOrDefaultAsync(cancellationToken);
        // A field's own row is its list, not a tag page.
        if (tag is null || tag.FieldKey is not null)
            return null;
        var denied = await DeniedAsync(db, libraryId, visibility, cancellationToken);
        if (denied.Contains(tagId))
            return null;

        int? count = null;
        if (libraryId is { } library)
            count = await db.TagCounts.Where(c => c.LibraryId == library && c.TagId == tagId).Select(c => (int?)c.TotalCount).FirstOrDefaultAsync(cancellationToken) ?? 0;
        var parents = await db.TagParents.Where(p => p.ChildId == tagId)
            .Select(p => new TagRef(p.ParentId, p.Parent!.Name, p.Parent.Field == null ? null : p.Parent.Field.FieldKey))
            .ToListAsync(cancellationToken);
        // Children by the parent index, a bounded number; sorted here (the index has no sort name).
        var children = await db.TagParents.Where(p => p.ParentId == tagId)
            .Select(p => new TagRef(p.ChildId, p.Child!.Name, p.Child.Field == null ? null : p.Child.Field.FieldKey))
            .Take(MaxChildren + 1)
            .ToListAsync(cancellationToken);
        var more = children.Count > MaxChildren;
        return new TagDetail(tag.Id, tag.Name, tag.SortName, tag.Field?.FieldKey, tag.Field?.Name, tag.IsCategory, count,
            parents.Where(p => !denied.Contains(p.Id)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            children.Take(MaxChildren).Where(c => !denied.Contains(c.Id)).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            more);
    }

    private async Task<Page<FieldValue>> PageAsync(LibrarionDbContext db, IQueryable<Tag> tags, int? libraryId, string? after, string? from, int limit,
        BookVisibility? visibility, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var denied = await DeniedAsync(db, libraryId, visibility, cancellationToken);
        if (denied.Count > 0)
            tags = tags.Where(t => !denied.Contains(t.Id));

        var cursor = KeysetCursor.Decode(after);
        if (cursor is null && Start(from) is { } start)
            cursor = new KeysetCursor(start, null, 0);
        var items = new List<FieldValue>();
        var scanned = 0;
        string? next = null;
        while (true)
        {
            var query = tags;
            if (cursor is { Text: { } name })
                query = query.Where(t => string.Compare(t.SortName, name) >= 0 && (string.Compare(t.SortName, name) > 0 || t.Id > cursor.Id));
            var take = limit - items.Count + 1;
            var rows = await query.OrderBy(t => t.SortName).ThenBy(t => t.Id).Take(take)
                .Select(t => new { t.Id, t.Name, t.SortName })
                .ToListAsync(cancellationToken);
            var more = rows.Count == take;
            if (more)
                rows.RemoveAt(rows.Count - 1);
            scanned += rows.Count;

            if (libraryId is not { } library)
            {
                items.AddRange(rows.Select(r => new FieldValue(r.Id, r.Name, r.SortName, null)));
            }
            else
            {
                var ids = rows.Select(r => r.Id).ToList();
                // With what is under them: a category whose books are all in its subcategories is listed too.
                var counts = await db.TagCounts.Where(c => c.LibraryId == library && ids.Contains(c.TagId) && c.TotalCount > 0)
                    .ToDictionaryAsync(c => c.TagId, c => c.TotalCount, cancellationToken);
                items.AddRange(rows.Where(r => counts.ContainsKey(r.Id)).Select(r => new FieldValue(r.Id, r.Name, r.SortName, counts[r.Id])));
            }

            if (rows.Count > 0)
                cursor = new KeysetCursor(rows[^1].SortName, null, rows[^1].Id);
            if (!more)
                break;
            // A full page, or enough read for one request: the rest from where this stopped.
            if (items.Count >= limit || scanned >= MaxScanned)
            {
                next = cursor!.Encode();
                break;
            }
        }

        return new Page<FieldValue>(items, next);
    }

    /// <summary>
    /// The tags the caller's content filters deny in the library, with their descendants (bounded): left out of the lists
    /// and their pages. A tag is denied when every group admitting the caller there denies it.
    /// </summary>
    internal static async Task<IReadOnlyList<int>> DeniedAsync(LibrarionDbContext db, int? libraryId, BookVisibility? visibility, CancellationToken cancellationToken)
    {
        if (visibility is null)
            return [];
        var profiles = libraryId is { } library
            ? visibility.Libraries.GetValueOrDefault(library) ?? []
            : visibility.Libraries.Values.SelectMany(p => p).ToList();
        if (profiles.Count == 0 || profiles.Any(p => p.Denied.Count == 0))
            return [];
        var denied = profiles.Skip(1).Aggregate((IEnumerable<int>)profiles[0].Denied, (common, p) => common.Intersect(p.Denied)).ToList();
        if (denied.Count == 0)
            return [];
        return await db.TagClosure.Where(c => denied.Contains(c.AncestorId)).Select(c => c.DescendantId).Distinct().Take(MaxDenied).ToListAsync(cancellationToken);
    }

    /// <summary>A jump's start: trimmed, the first letter upper case (sort names start so; "kin" finds "King").</summary>
    private static string? Start(string? from)
    {
        if (string.IsNullOrWhiteSpace(from))
            return null;
        var text = from.Trim();
        if (text.Length > Tag.MaxNameLength)
            text = text[..Tag.MaxNameLength];
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
