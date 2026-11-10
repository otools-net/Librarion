using System.Collections.Concurrent;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Merging;

/// <summary>
/// The stored possible duplicate tags (stage 6 chunk 14, the user's answers 2026-11-09,
/// agents/docs/features/merging.md#possible-duplicate-tags-built-stage-6-chunk-14): tags of one field (or plain tags)
/// sharing a match key (<see cref="NameKeys"/>: the sound of their words, or a surname with an initial), never compared
/// pairwise. <see cref="BuildAsync"/> walks each field's keys on their indexes, a page of <see cref="WalkSize"/> tags at
/// a time; <see cref="RefreshAsync"/> recomputes the keys of given tags (their old and new ones) by keyed seeks. A key of
/// <see cref="MaxGroup"/> tags or more is noise, stored without members until a rebuild. An initials key makes a group
/// only with a name of initials and one written out; tags said not the same (<see cref="NotSameTag"/>) never make one
/// alone. Each group keeps its members' books (every library) when written: what the page ranks by.
/// </summary>
public sealed class TagDuplicateIndex(TenantDb tenantDb, TimeProvider time)
{
    /// <summary>Tags sharing a key, at most: more is noise ("A. Smith" among thousands of Smiths).</summary>
    public const int MaxGroup = 20;

    public const int WalkSize = 5000;

    private sealed record Row(long Key, int Id, string Name);

    /// <summary>A key of a field: <paramref name="Field"/> 0 for plain tags.</summary>
    private readonly record struct Unit(int Field, bool Initials, long Key);

    /// <summary>One build or end-of-run upkeep of a tenant at a time (as <see cref="DuplicateIndex"/>).</summary>
    private readonly ConcurrentDictionary<int, SemaphoreSlim> running = new();

    public static Task<bool> BuiltAsync(LibrarionDbContext db, CancellationToken cancellationToken) =>
        db.Tenants.Where(t => t.Id == db.CurrentTenantId).AnyAsync(t => t.TagDuplicatesBuilt != null, cancellationToken);

    /// <summary>
    /// The end of a scan or import: the groups built whole when the tenant has none yet or the run made many tags (only
    /// after a run that completed), else the groups of the tags it made computed again.
    /// </summary>
    public async Task AfterRunAsync(int tenantId, TouchedIds created, bool completed, CancellationToken cancellationToken)
    {
        if (!created.Any)
            return;
        var gate = running.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
            if (!await BuiltAsync(db, cancellationToken) || created.Overflowed)
            {
                if (completed)
                    await BuildLockedAsync(tenantId, null, cancellationToken);
                else
                    await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.TagDuplicatesBuilt, (DateTime?)null), cancellationToken);
                return;
            }

            await RefreshAsync(db, created.Ids, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Every group of the tenant built again; the tenant marked built at the end only. How many groups (noise left out) it made.</summary>
    public async Task<int> BuildAsync(int tenantId, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var gate = running.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await BuildLockedAsync(tenantId, progress, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> BuildLockedAsync(int tenantId, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.TagDuplicatesBuilt, (DateTime?)null), cancellationToken);
        await db.TagDuplicateGroups.ExecuteDeleteAsync(cancellationToken);
        var apart = (await db.NotSameTags.Select(n => new { n.TagId, n.OtherId }).ToListAsync(cancellationToken)).Select(n => (n.TagId, n.OtherId)).ToHashSet();
        var fields = await db.Tags.Where(t => t.FieldKey != null).Select(t => (int?)t.Id).ToListAsync(cancellationToken);
        var (groups, seen) = (0, 0);
        foreach (var field in fields.Prepend(null))
        {
            foreach (var initials in new[] { false, true })
            {
                long? last = null;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rows = await PageAsync(db, field, initials, last, cancellationToken);
                    if (rows.Count == 0)
                        break;
                    seen += rows.Count;
                    var runs = rows.GroupBy(r => r.Key).Select(g => (Key: g.Key, Rows: g.ToList())).ToList();
                    // A full page's last key may go on in the next page: read again there, unless it fills the page (noise).
                    if (rows.Count == WalkSize && runs.Count > 1)
                        runs.RemoveAt(runs.Count - 1);
                    var made = runs.Select(r => Make(tenantId, new Unit(field ?? 0, initials, r.Key), r.Rows, apart)).OfType<TagDuplicateGroup>().ToList();
                    groups += await WriteAsync(db, made, cancellationToken);
                    progress?.Report(new ScanProgress(seen, 0, groups, 0, 0, initials ? "initials" : "sound"));
                    last = runs[^1].Key;
                    if (rows.Count < WalkSize)
                        break;
                }
            }
        }

        // SQLite plans by statistics it only has once asked (as the books' groups, DuplicateIndex): without them a page's
        // member lookup walked the tenant's (TenantId, TagId) index.
        if (db.Database.IsSqlite())
            await db.Database.ExecuteSqlRawAsync("ANALYZE \"TagDuplicateGroups\"; ANALYZE \"TagDuplicateMembers\";", cancellationToken);
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.TagDuplicatesBuilt, time.GetUtcNow().UtcDateTime), cancellationToken);
        return groups;
    }

    /// <summary>
    /// The groups of <paramref name="tagIds"/> computed again: the keys they had (their groups) and have now, each read
    /// whole by its index (at most <see cref="MaxGroup"/> tags). A key known as noise stays noise until a rebuild.
    /// Nothing when the tenant isn't built yet.
    /// </summary>
    public static async Task RefreshAsync(LibrarionDbContext db, IReadOnlyCollection<int> tagIds, CancellationToken cancellationToken)
    {
        if (tagIds.Count == 0 || !await BuiltAsync(db, cancellationToken))
            return;
        foreach (var chunk in tagIds.Distinct().Chunk(1000))
        {
            var units = new HashSet<Unit>();
            foreach (var tag in await db.Tags.Where(t => chunk.Contains(t.Id)).Select(t => new { t.FieldId, t.SoundKey, t.InitialsKey }).ToListAsync(cancellationToken))
            {
                if (tag.SoundKey is { } sound)
                    units.Add(new Unit(tag.FieldId ?? 0, false, sound));
                if (tag.InitialsKey is { } initials)
                    units.Add(new Unit(tag.FieldId ?? 0, true, initials));
            }

            var old = await db.TagDuplicateMembers.Where(m => chunk.Contains(m.TagId))
                .Join(db.TagDuplicateGroups, m => m.GroupId, g => g.Id, (m, g) => new { g.Field, g.Level, g.Key }).Distinct().ToListAsync(cancellationToken);
            units.UnionWith(old.Select(g => new Unit(g.Field, g.Level == TagDuplicateLevel.Initials, g.Key)));

            var made = new List<TagDuplicateGroup>();
            foreach (var unit in units)
            {
                var stored = await db.TagDuplicateGroups.Where(g => g.Field == unit.Field && g.Key == unit.Key)
                    .Select(g => new { g.Id, g.Level, g.Noise }).ToListAsync(cancellationToken);
                var same = stored.Where(g => (g.Level == TagDuplicateLevel.Initials) == unit.Initials).ToList();
                if (same.Any(g => g.Noise))
                    continue;
                var ids = same.Select(g => g.Id).ToList();
                if (ids.Count > 0)
                    await db.TagDuplicateGroups.Where(g => ids.Contains(g.Id)).ExecuteDeleteAsync(cancellationToken);
                var rows = await ByKeyAsync(db, unit, cancellationToken);
                var members = rows.Select(r => r.Id).ToList();
                var apart = (await db.NotSameTags.Where(n => members.Contains(n.TagId) && members.Contains(n.OtherId)).Select(n => new { n.TagId, n.OtherId })
                    .ToListAsync(cancellationToken)).Select(n => (n.TagId, n.OtherId)).ToHashSet();
                if (Make(db.CurrentTenantId, unit, rows, apart) is { } group)
                    made.Add(group);
            }

            await WriteAsync(db, made, cancellationToken);
        }
    }

    /// <summary>A key's group of <paramref name="rows"/>: noise past <see cref="MaxGroup"/>; null when there is none to suggest.</summary>
    private static TagDuplicateGroup? Make(int tenantId, Unit unit, List<Row> rows, IReadOnlySet<(int, int)> apart)
    {
        if (rows.Count >= MaxGroup)
            return new TagDuplicateGroup { TenantId = tenantId, Field = unit.Field, Level = unit.Initials ? TagDuplicateLevel.Initials : TagDuplicateLevel.Sound, Key = unit.Key, Noise = true };
        if (rows.Count < 2)
            return null;
        var ids = rows.Select(r => r.Id).Order().ToList();
        if (ids.SelectMany((a, i) => ids.Skip(i + 1).Select(b => (a, b))).All(apart.Contains))
            return null;
        if (unit.Initials && !(rows.Any(r => NameKeys.IsInitials(r.Name)) && rows.Any(r => !NameKeys.IsInitials(r.Name))))
            return null;
        var level = unit.Initials ? TagDuplicateLevel.Initials
            : rows.Select(r => NameKeys.WordsText(r.Name)).Distinct(StringComparer.Ordinal).Count() == 1 ? TagDuplicateLevel.Words : TagDuplicateLevel.Sound;
        var group = new TagDuplicateGroup { TenantId = tenantId, Field = unit.Field, Level = level, Key = unit.Key, TagCount = ids.Count };
        group.Members.AddRange(ids.Select(id => new TagDuplicateMember { TenantId = tenantId, TagId = id }));
        return group;
    }

    /// <summary>The groups written, each with its members' books (direct links, every library): how many weren't noise.</summary>
    private static async Task<int> WriteAsync(LibrarionDbContext db, List<TagDuplicateGroup> groups, CancellationToken cancellationToken)
    {
        if (groups.Count == 0)
            return 0;
        var ids = groups.SelectMany(g => g.Members.Select(m => m.TagId)).Distinct().ToList();
        var books = new Dictionary<int, int>();
        foreach (var part in ids.Chunk(1000))
            foreach (var count in await db.TagCounts.Where(c => part.Contains(c.TagId)).GroupBy(c => c.TagId)
                         .Select(g => new { g.Key, Books = g.Sum(c => c.BookCount) }).ToListAsync(cancellationToken))
                books[count.Key] = count.Books;
        foreach (var group in groups)
            group.BookCount = group.Members.Sum(m => books.GetValueOrDefault(m.TagId));
        db.TagDuplicateGroups.AddRange(groups);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return groups.Count(g => !g.Noise);
    }

    /// <summary>A field's tags with a key, in key order, after <paramref name="after"/>: a page of the build's walk.</summary>
    private static Task<List<Row>> PageAsync(LibrarionDbContext db, int? field, bool initials, long? after, CancellationToken cancellationToken)
    {
        var tags = field is { } id ? db.Tags.Where(t => t.FieldId == id) : db.Tags.Where(t => t.FieldId == null);
        if (initials)
        {
            var query = tags.Where(t => t.InitialsKey != null);
            if (after is { } last)
                query = query.Where(t => t.InitialsKey > last);
            return query.OrderBy(t => t.InitialsKey).Take(WalkSize).Select(t => new Row(t.InitialsKey!.Value, t.Id, t.Name)).ToListAsync(cancellationToken);
        }
        else
        {
            var query = tags.Where(t => t.SoundKey != null);
            if (after is { } last)
                query = query.Where(t => t.SoundKey > last);
            return query.OrderBy(t => t.SoundKey).Take(WalkSize).Select(t => new Row(t.SoundKey!.Value, t.Id, t.Name)).ToListAsync(cancellationToken);
        }
    }

    /// <summary>The tags of a key (one more than a group holds, to see noise).</summary>
    private static Task<List<Row>> ByKeyAsync(LibrarionDbContext db, Unit unit, CancellationToken cancellationToken)
    {
        var tags = unit.Field != 0 ? db.Tags.Where(t => t.FieldId == unit.Field) : db.Tags.Where(t => t.FieldId == null);
        var query = unit.Initials ? tags.Where(t => t.InitialsKey == unit.Key) : tags.Where(t => t.SoundKey == unit.Key);
        return query.OrderBy(t => t.Id).Take(MaxGroup).Select(t => new Row(unit.Key, t.Id, t.Name)).ToListAsync(cancellationToken);
    }
}
