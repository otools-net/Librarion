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
public sealed class TagQueries(TenantDb tenantDb, TimeProvider time)
{
    public const int MaxLimit = 200;

    /// <summary>Rows read at most for one page when values without books in the library are skipped.</summary>
    public const int MaxScanned = 2000;

    public const int MaxChildren = 50;

    /// <summary>Merges followed from an old id at most (a merge into a tag merged later, and so on).</summary>
    public const int MaxMoves = 20;

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

    /// <summary>
    /// The tenant's fields that hold at least one value (a reader app's catalog leaves out the empty ones): one seek per
    /// field on the index (TenantId, FieldId, SortName), never a count. Ordered on purpose: a bare <c>EXISTS</c> was
    /// answered on PostgreSQL by a scan of <c>Tags</c> from its start, past 500k authors before the first series (2M
    /// benchmark, 2026-11-11).
    /// </summary>
    public async Task<IReadOnlyList<FieldInfo>> FieldsWithValuesAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        var fields = await FieldsAsync(tenantId, cancellationToken);
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var kept = new List<FieldInfo>();
        foreach (var field in fields)
        {
            if (await db.Tags.Where(t => t.FieldId == field.Id).OrderBy(t => t.SortName).Select(t => (int?)t.Id).FirstOrDefaultAsync(cancellationToken) is not null)
                kept.Add(field);
        }
        return kept;
    }

    /// <summary>
    /// The letters a field's first-letter page offers, in this order: digits, Latin, Greek, Cyrillic. Names starting
    /// otherwise (CJK, an accented capital where the database sorts by code point) are reached from the start of the list.
    /// </summary>
    public static readonly IReadOnlyList<string> Initials =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩАБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЭЮЯ".Select(c => c.ToString()).ToList();

    /// <summary>How long a field's letters are kept: they change only when the first value of a new letter comes in.</summary>
    public static readonly TimeSpan InitialsLifetime = TimeSpan.FromMinutes(10);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Tenant, string Field), (DateTimeOffset Expires, IReadOnlyList<string> Letters)> _initials = new();

    /// <summary>
    /// The letters of <see cref="Initials"/> some value of a field (or plain tag, <paramref name="fieldKey"/> null) starts
    /// with: for each, one seek on the index (TenantId, FieldId, SortName) for the first value at or after it, kept when
    /// it starts with that letter; every letter in one query (a <c>VALUES</c> list, a correlated <c>LIMIT 1</c> each:
    /// ~90 round trips were 186 ms at 2M on PostgreSQL, one is under 1 ms). Plain tags are probed among all rows
    /// without a field (<c>FieldKey IS NULL</c> turned PostgreSQL to another index and a sort of 50k rows per letter,
    /// 1.7 s): a field's own row found first is stepped over. Right in any collation (PostgreSQL's libc one orders punctuation and U+FFFF unlike
    /// SQLite's code points: a skip scan by "the next code point" stopped after Z there). Values of the whole tenant, so
    /// a letter may hold none of a library's books. Kept <see cref="InitialsLifetime"/> per tenant and field;
    /// <paramref name="fresh"/> probes again (the benchmark's uncached case).
    /// </summary>
    public async Task<IReadOnlyList<string>> InitialsAsync(int tenantId, string? fieldKey, CancellationToken cancellationToken = default, bool fresh = false)
    {
        var cacheKey = (tenantId, fieldKey ?? "");
        if (!fresh && _initials.TryGetValue(cacheKey, out var cached) && cached.Expires > time.GetUtcNow())
            return cached.Letters;

        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        int? fieldId = null;
        if (fieldKey is not null)
        {
            fieldId = await db.Tags.Where(t => t.FieldKey == fieldKey).Select(t => (int?)t.Id).FirstOrDefaultAsync(cancellationToken);
            if (fieldId is null)
                return [];
        }

        // The fields' own rows have no field either: never a plain tag's letter.
        // In key order, as FieldsAsync: with "IS NOT NULL" alone SQLite scans every tag.
        var fieldRows = fieldId is null
            ? (await db.Tags.Where(t => t.FieldKey != null).OrderBy(t => t.FieldKey).Select(t => t.SortName).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal)
            : [];
        var firsts = await FirstAtOrAfterAsync(db, tenantId, fieldId, Initials, cancellationToken);
        var letters = new List<string>();
        for (var i = 0; i < Initials.Count; i++)
        {
            var first = firsts[i];
            // A field's row in the way: the plain tags after it (the fields are a handful, so is this).
            while (first is not null && fieldRows.Contains(first))
                first = (await FirstAtOrAfterAsync(db, tenantId, null, [first], cancellationToken, after: true))[0];
            if (first is { Length: > 0 } && string.Equals(first[..1], Initials[i], StringComparison.OrdinalIgnoreCase))
                letters.Add(Initials[i]);
        }

        _initials[cacheKey] = (time.GetUtcNow() + InitialsLifetime, letters);
        return letters;
    }

    private sealed record FirstName(string Probe, string? First);

    /// <summary>
    /// For each of <paramref name="probes"/>, the first sort name at or after it (or after it, <paramref name="after"/>)
    /// among the field's values (<paramref name="fieldId"/> null: every tag without a field), in one query. Raw SQL that
    /// reads the same on both providers (a <c>VALUES</c> list's columns are <c>column1</c> on both); the tenant named
    /// in it, since the query filter doesn't reach raw SQL. Ordered by <c>FieldId</c> first: PostgreSQL takes
    /// <c>FieldId IS NULL</c> as no fixed value for the index's order and sorted otherwise.
    /// </summary>
    private static async Task<IReadOnlyList<string?>> FirstAtOrAfterAsync(LibrarionDbContext db, int tenantId, int? fieldId, IReadOnlyList<string> probes,
        CancellationToken cancellationToken, bool after = false)
    {
        var arguments = new List<object> { tenantId };
        if (fieldId is { } id)
            arguments.Add(id);
        var field = fieldId is null ? "t.\"FieldId\" IS NULL" : "t.\"FieldId\" = {1}";
        var values = string.Join(", ", probes.Select((probe, i) => $"({{{arguments.Count + i}}})"));
        arguments.AddRange(probes);
        var sql = $$"""
            SELECT l.column1 AS "Probe", (SELECT t."SortName" FROM "Tags" AS t
                WHERE t."TenantId" = {0} AND {{field}} AND t."SortName" {{(after ? ">" : ">=")}} l.column1
                ORDER BY t."FieldId", t."SortName" LIMIT 1) AS "First"
            FROM (VALUES {{values}}) AS l
            """;
        var rows = await db.Database.SqlQueryRaw<FirstName>(sql, arguments.ToArray()).ToListAsync(cancellationToken);
        var found = rows.ToDictionary(r => r.Probe, r => r.First, StringComparer.Ordinal);
        return probes.Select(p => found.GetValueOrDefault(p)).ToList();
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

    /// <summary>
    /// Where a merged tag went (an old address of it, a bookmark): the merge logs that hold, followed through later merges
    /// up to <see cref="MaxMoves"/>; null when the id was never merged, or its tag is gone or denied.
    /// </summary>
    public async Task<TagRef?> MovedAsync(int tenantId, int tagId, int? libraryId, BookVisibility? visibility = null, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var id = tagId;
        for (var moves = 0; moves < MaxMoves; moves++)
        {
            var from = id;
            var target = await db.MergeLogs.Where(l => l.SourceId == from && l.Kind != MergeKind.Book && l.Completed && l.Undone == null).OrderByDescending(l => l.Id)
                .Select(l => (int?)l.TargetId).FirstOrDefaultAsync(cancellationToken);
            if (target is null)
                return null;
            id = target.Value;
            var tag = await db.Tags.Where(t => t.Id == id && t.FieldKey == null)
                .Select(t => new TagRef(t.Id, t.Name, t.Field == null ? null : t.Field.FieldKey)).FirstOrDefaultAsync(cancellationToken);
            if (tag is not null)
                return (await DeniedAsync(db, libraryId, visibility, cancellationToken)).Contains(tag.Id) ? null : tag;
        }

        return null;
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
            // FieldId first: the same order (every query here fixes it), and PostgreSQL walks the index for plain tags
            // too (FieldId IS NULL, else it sorts them: 70 ms at 2M).
            var rows = await query.OrderBy(t => t.FieldId).ThenBy(t => t.SortName).ThenBy(t => t.Id).Take(take)
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
