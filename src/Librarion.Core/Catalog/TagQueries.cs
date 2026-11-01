using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Catalog;

/// <param name="BookCount">Books of the library with it or a descendant, from <see cref="TagCount"/>; null when no library was named.</param>
public sealed record FieldValue(int Id, string Name, string SortName, int? BookCount);

/// <summary>
/// The values of a field (authors A–Z, series, categories…) by sort name, keyset on the index (TenantId, FieldId,
/// SortName, Id); counts read from <see cref="TagCount"/>, never counted (agents/docs/features/metadata-tags.md).
/// </summary>
public sealed class TagQueries(TenantDb tenantDb)
{
    public const int MaxLimit = 200;

    /// <returns>Null when the tenant has no field with this key.</returns>
    public async Task<Page<FieldValue>?> ValuesAsync(int tenantId, string fieldKey, int? libraryId, string? after, int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var fieldId = await db.Tags.Where(t => t.FieldKey == fieldKey).Select(t => (int?)t.Id).FirstOrDefaultAsync(cancellationToken);
        if (fieldId is null)
            return null;

        limit = Math.Clamp(limit, 1, MaxLimit);
        var query = db.Tags.Where(t => t.FieldId == fieldId);
        if (KeysetCursor.Decode(after) is { Text: { } name } cursor)
            query = query.Where(t => string.Compare(t.SortName, name) >= 0 && (string.Compare(t.SortName, name) > 0 || t.Id > cursor.Id));
        var rows = await query.OrderBy(t => t.SortName).ThenBy(t => t.Id).Take(limit + 1)
            .Select(t => new { t.Id, t.Name, t.SortName })
            .ToListAsync(cancellationToken);
        var more = rows.Count > limit;
        if (more)
            rows.RemoveAt(limit);

        Dictionary<int, int> counts = [];
        if (libraryId is { } library)
        {
            var ids = rows.Select(r => r.Id).ToList();
            counts = await db.TagCounts.Where(c => c.LibraryId == library && ids.Contains(c.TagId)).ToDictionaryAsync(c => c.TagId, c => c.BookCount, cancellationToken);
        }

        var items = rows.Select(r => new FieldValue(r.Id, r.Name, r.SortName, libraryId is null ? null : counts.GetValueOrDefault(r.Id))).ToList();
        return new Page<FieldValue>(items, more ? new KeysetCursor(rows[^1].SortName, null, rows[^1].Id).Encode() : null);
    }
}
