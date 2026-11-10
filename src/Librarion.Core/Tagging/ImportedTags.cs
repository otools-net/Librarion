using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Tagging;

/// <summary>A field of the Imported tags page (<paramref name="Key"/> null: plain tags) with its tags that have imported names, up to <see cref="TagLists.MaxCounted"/>.</summary>
public sealed record ImportedField(int? Id, string? Key, string Name, bool BuiltIn, int Tags, bool Capped);

/// <summary>A raw name as an import gave it, from which source, and whether a curator looked at it.</summary>
public sealed record ImportedName(string Name, ImportTagSourceKind Source, bool Reviewed);

/// <summary>A tag imports give, its books in the library (direct links), its raw names; <paramref name="Reviewed"/> once each of them is.</summary>
public sealed record ImportedTag(int Id, string Name, string? Color, long Books, IReadOnlyList<ImportedName> Names, bool Reviewed);

/// <summary>
/// How much of a field's imported links its first <see cref="ImportedTags.TopCount"/> tags and its reviewed ones hold
/// (the page's coverage line: "the first 500 cover 93 %").
/// </summary>
public sealed record ImportedSummary(int Tags, long Links, int TopCount, long Top, long Reviewed);

public enum ImportedStatus
{
    /// <summary>A raw name of it nobody looked at yet.</summary>
    Review,
    Reviewed,
    All,
}

/// <summary>
/// The Imported tags page (Administration › Catalog › Imported tags, stage 6 chunk 9,
/// agents/docs/features/imported-tags.md): the tags of a field that imports give (their mappings), by their books in a
/// library from the most, a page at a time. A field with few such tags (<see cref="SmallField"/>) is read whole and
/// sorted; a big one walks <c>TagCounts (LibraryId, BookCount, TagId)</c> by key, a chunk of candidates at a time and at
/// most <see cref="MaxChunks"/> chunks a request (the page then ends early with a cursor), never the field whole.
/// </summary>
public sealed class ImportedTags(TenantDb tenantDb, AccessChanges changes)
{
    public const int SmallField = 1000;
    public const int Chunk = 2000;
    public const int MaxChunks = 4;
    public const int TopCount = 500;
    public const int MaxLimit = 100;

    /// <summary>How long a coverage line is kept while no tag changes (imports move the counts meanwhile: it is a guide).</summary>
    public static readonly TimeSpan SummaryLife = TimeSpan.FromMinutes(2);
    private const int MaxSummaries = 1000;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, string?, ImportTagSourceKind?), (long Version, long At, ImportedSummary Summary)> summaries = new();

    /// <summary>Tests walk tiny fields as big ones (<see cref="SmallField"/>, <see cref="Chunk"/>).</summary>
    internal int Small { get; set; } = SmallField;
    internal int ChunkSize { get; set; } = Chunk;

    /// <summary>The tenant's libraries: a change of the page (the tenant's mappings) needs the right in each of them.</summary>
    public async Task<IReadOnlyList<int>> LibrariesAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        return await db.Libraries.Select(l => l.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ImportedField>> FieldsAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var used = await db.ImportTagMappings.Where(m => !m.Ignored).Select(m => m.FieldId).Distinct().ToListAsync(cancellationToken);
        var rows = await db.Tags.Where(t => t.FieldKey != null && used.Contains(t.Id)).Select(t => new { t.Id, t.FieldKey, t.Name, t.BuiltIn }).ToListAsync(cancellationToken);
        var order = BuiltInTags.Fields.Keys.ToList();
        var fields = new List<ImportedField> { await CountedAsync(db, null, null, "", false, cancellationToken) };
        foreach (var row in rows.Where(r => !ImportTagResolver.UnrecordedFields.Contains(r.FieldKey!))
                     .OrderBy(f => f.BuiltIn is { } builtIn ? order.IndexOf(builtIn) : order.Count).ThenBy(f => f.FieldKey, StringComparer.Ordinal))
            fields.Add(await CountedAsync(db, row.Id, row.FieldKey, row.Name, row.BuiltIn is not null, cancellationToken));
        return fields;
    }

    private static async Task<ImportedField> CountedAsync(LibrarionDbContext db, int? fieldId, string? key, string name, bool builtIn, CancellationToken cancellationToken)
    {
        var tags = await Names(db, fieldId, null).Select(m => m.TagId).Distinct().Take(TagLists.MaxCounted + 1).CountAsync(cancellationToken);
        return new ImportedField(fieldId, key, name, builtIn, Math.Min(tags, TagLists.MaxCounted), tags > TagLists.MaxCounted);
    }

    /// <summary>
    /// The tags of field <paramref name="fieldKey"/> (null: plain tags) with imported names (of <paramref name="source"/>
    /// only, when given) in <paramref name="status"/>, that have books in library <paramref name="libraryId"/>, by those
    /// books from the most. Null for an unknown field.
    /// </summary>
    public async Task<Page<ImportedTag>?> ListAsync(int tenantId, int libraryId, string? fieldKey, ImportTagSourceKind? source, ImportedStatus status, string? after,
        int limit, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (await FieldAsync(db, fieldKey, cancellationToken) is not (true, var fieldId))
            return null;
        var cursor = KeysetCursor.Decode(after);
        var names = Names(db, fieldId, source);
        var rows = new List<(int Id, long Books)>();
        string? next = null;

        var few = await names.Select(m => m.TagId).Distinct().Take(Small + 1).CountAsync(cancellationToken) <= Small;
        if (few)
        {
            var ids = await Matching(names, status).ToListAsync(cancellationToken);
            var counts = await db.TagCounts.Where(c => c.LibraryId == libraryId && c.BookCount > 0 && ids.Contains(c.TagId))
                .Select(c => new { c.TagId, c.BookCount }).ToListAsync(cancellationToken);
            var ordered = counts.OrderByDescending(c => c.BookCount).ThenByDescending(c => c.TagId)
                .Where(c => cursor is not { Number: { } books } || c.BookCount < books || c.BookCount == books && c.TagId < cursor.Id)
                .Select(c => (c.TagId, (long)c.BookCount)).Take(limit + 1).ToList();
            if (ordered.Count > limit)
            {
                ordered.RemoveAt(limit);
                next = new KeysetCursor(null, ordered[^1].Item2, ordered[^1].TagId).Encode();
            }

            rows = ordered;
        }
        else
        {
            // An int like the column: a long made SQLite compare a cast, off the index (each chunk read from the top).
            var (books, id) = cursor is { Number: { } n } ? ((int)Math.Clamp(n, 0, int.MaxValue), cursor.Id) : (int.MaxValue, int.MaxValue);
            for (var chunk = 0; chunk < MaxChunks && rows.Count <= limit; chunk++)
            {
                var (lastBooks, lastId) = (books, id);
                var chunkRows = db.TagCounts
                    .Where(c => c.LibraryId == libraryId && c.BookCount > 0 && (c.BookCount < lastBooks || c.BookCount == lastBooks && c.TagId < lastId))
                    .OrderByDescending(c => c.BookCount).ThenByDescending(c => c.TagId).Take(ChunkSize);
                var candidates = await chunkRows.Select(c => new { c.TagId, c.BookCount }).ToListAsync(cancellationToken);
                if (candidates.Count == 0)
                {
                    books = -1;
                    break;
                }

                // SQLite: the chunk again as a subquery (a list of 2,000 ids as parameters took most of the time there).
                var ids = candidates.Select(c => c.TagId).ToList();
                var inChunk = db.Database.IsSqlite()
                    ? names.Where(m => chunkRows.Any(c => c.TagId == m.TagId))
                    : names.Where(m => ids.Contains(m.TagId!.Value));
                var matched = (await Matching(inChunk, status).ToListAsync(cancellationToken)).ToHashSet();
                foreach (var candidate in candidates)
                {
                    (books, id) = (candidate.BookCount, candidate.TagId);
                    if (!matched.Contains(candidate.TagId))
                        continue;
                    rows.Add((candidate.TagId, candidate.BookCount));
                    if (rows.Count > limit)
                        break;
                }

                if (candidates.Count < ChunkSize && rows.Count <= limit)
                {
                    books = -1;
                    break;
                }
            }

            if (rows.Count > limit)
            {
                rows.RemoveAt(limit);
                next = new KeysetCursor(null, rows[^1].Books, rows[^1].Id).Encode();
            }
            // The walk stopped before filling the page: the next one starts where it stopped.
            else if (books >= 0)
                next = new KeysetCursor(null, books, id).Encode();
        }

        return new Page<ImportedTag>(await DescribeAsync(db, rows, cancellationToken), next);
    }

    /// <summary>
    /// The coverage line of field <paramref name="fieldKey"/> in library <paramref name="libraryId"/>; null for an unknown
    /// field. It reads every tag of the field with imported names (50k plain tags at 2M books: a few hundred ms), so it is
    /// kept until a tag change (<see cref="AccessChanges"/>) or for <see cref="SummaryLife"/>; <paramref name="fresh"/>
    /// computes it anyway (the benchmark).
    /// </summary>
    public async Task<ImportedSummary?> SummaryAsync(int tenantId, int libraryId, string? fieldKey, ImportTagSourceKind? source, CancellationToken cancellationToken = default,
        bool fresh = false)
    {
        var key = (tenantId, libraryId, fieldKey, source);
        var version = changes.Version;
        var now = Environment.TickCount64;
        if (!fresh && summaries.TryGetValue(key, out var kept) && kept.Version == version && now - kept.At < SummaryLife.TotalMilliseconds)
            return kept.Summary;
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (await FieldAsync(db, fieldKey, cancellationToken) is not (true, var fieldId))
            return null;
        // The field's tags with their counts, summed here. PostgreSQL: the tags through the mappings' index, then their
        // counts by key a chunk at a time (as one join it read TagCounts whole: 480 ms at 2M books). SQLite: one join
        // (the chunks' long parameter lists took 2 s); agents/docs/features/imported-tags.md#scale.
        var names = Names(db, fieldId, source);
        var counts = new List<(long Books, bool Reviewed)>();
        if (db.Database.IsSqlite())
        {
            var rows = await db.TagCounts.Where(c => c.LibraryId == libraryId && c.BookCount > 0)
                .Join(names, c => c.TagId, m => m.TagId!.Value, (c, m) => new { c.TagId, c.BookCount, m.Reviewed }).ToListAsync(cancellationToken);
            counts.AddRange(rows.GroupBy(r => r.TagId).Select(g => ((long)g.First().BookCount, g.All(r => r.Reviewed))));
        }
        else
        {
            var tags = (await names.Select(m => new { TagId = m.TagId!.Value, m.Reviewed }).ToListAsync(cancellationToken))
                .GroupBy(m => m.TagId).ToDictionary(g => g.Key, g => g.All(m => m.Reviewed));
            foreach (var chunk in tags.Keys.Chunk(5000))
            {
                var ids = chunk.ToList();
                foreach (var row in await db.TagCounts.Where(c => c.LibraryId == libraryId && ids.Contains(c.TagId) && c.BookCount > 0)
                             .Select(c => new { c.TagId, c.BookCount }).ToListAsync(cancellationToken))
                    counts.Add((row.BookCount, tags[row.TagId]));
            }
        }

        var top = counts.Select(c => c.Books).OrderDescending().Take(TopCount).Sum();
        var summary = new ImportedSummary(counts.Count, counts.Sum(c => c.Books), TopCount, top, counts.Where(c => c.Reviewed).Sum(c => c.Books));
        if (summaries.Count > MaxSummaries)
            summaries.Clear();
        summaries[key] = (version, now, summary);
        return summary;
    }

    /// <summary>Marks the imported names of <paramref name="tagIds"/> reviewed (Keep: the tag is right as it is) or not; how many changed.</summary>
    public async Task<AccessResult<int>> ReviewAsync(int tenantId, IReadOnlyCollection<int> tagIds, bool reviewed, CancellationToken cancellationToken = default)
    {
        var ids = tagIds.Distinct().ToList();
        if (ids.Count is 0 or > Merging.TagMerger.MaxBulk)
            return AccessResult<int>.Invalid("tagIds", "tags.count");
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var changed = await db.ImportTagMappings.Where(m => m.TagId != null && ids.Contains(m.TagId.Value) && !m.Ignored && m.Reviewed != reviewed)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Reviewed, reviewed), cancellationToken);
        changes.Changed();
        return AccessResult<int>.Ok(changed);
    }

    /// <summary>The field of a key (null: plain tags); not found for an unknown or unrecorded one.</summary>
    private static async Task<(bool Found, int? Id)> FieldAsync(LibrarionDbContext db, string? fieldKey, CancellationToken cancellationToken)
    {
        if (fieldKey is null)
            return (true, null);
        if (ImportTagResolver.UnrecordedFields.Contains(fieldKey))
            return (false, null);
        var id = await db.Tags.Where(t => t.FieldKey == fieldKey).Select(t => (int?)t.Id).FirstOrDefaultAsync(cancellationToken);
        return (id is not null, id);
    }

    /// <summary>The mappings that stand (not ignored) of a field, of one source or all.</summary>
    private static IQueryable<ImportTagMapping> Names(LibrarionDbContext db, int? fieldId, ImportTagSourceKind? source)
    {
        var names = fieldId is { } id
            ? db.ImportTagMappings.Where(m => m.FieldId == id && !m.Ignored && m.TagId != null)
            : db.ImportTagMappings.Where(m => m.FieldId == null && !m.Ignored && m.TagId != null);
        return source is { } kind ? names.Where(m => m.Source == kind) : names;
    }

    /// <summary>The tags of <paramref name="names"/> in <paramref name="status"/>: to review while any of their names isn't reviewed.</summary>
    private static IQueryable<int> Matching(IQueryable<ImportTagMapping> names, ImportedStatus status) => status switch
    {
        ImportedStatus.Review => names.Where(m => !m.Reviewed).Select(m => m.TagId!.Value).Distinct(),
        ImportedStatus.Reviewed => names.Select(m => m.TagId!.Value).Distinct().Where(t => !names.Any(m => m.TagId == t && !m.Reviewed)),
        _ => names.Select(m => m.TagId!.Value).Distinct(),
    };

    private static async Task<IReadOnlyList<ImportedTag>> DescribeAsync(LibrarionDbContext db, List<(int Id, long Books)> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
            return [];
        var ids = rows.Select(r => r.Id).ToList();
        var tags = (await db.Tags.Where(t => ids.Contains(t.Id)).Select(t => new { t.Id, t.Name, t.Color }).ToListAsync(cancellationToken)).ToDictionary(t => t.Id);
        var names = (await db.ImportTagMappings.Where(m => m.TagId != null && ids.Contains(m.TagId.Value) && !m.Ignored)
                .Select(m => new { TagId = m.TagId!.Value, m.Name, m.NormalizedName, m.Source, m.Reviewed }).ToListAsync(cancellationToken))
            .ToLookup(m => m.TagId);
        return rows.Where(r => tags.ContainsKey(r.Id)).Select(r =>
        {
            var tag = tags[r.Id];
            var own = names[r.Id].OrderBy(m => m.Source).ThenBy(m => m.NormalizedName, StringComparer.Ordinal)
                .Select(m => new ImportedName(m.Name.Length > 0 ? m.Name : m.NormalizedName, m.Source, m.Reviewed)).ToList();
            return new ImportedTag(r.Id, tag.Name, tag.Color, r.Books, own, own.All(n => n.Reviewed));
        }).ToList();
    }
}
