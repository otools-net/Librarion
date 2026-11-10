using System.Text.Json;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Tagging;
using Librarion.Core.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Merging;

/// <summary>A merge as the Tags section lists it.</summary>
public sealed record MergeLogInfo(long Id, MergeKind Kind, DateTime Created, int SourceId, string SourceName, int TargetId, string TargetName, long Links,
    bool Completed, DateTime? Undone);

/// <summary>
/// Tag and field merges with undo (agents/docs/features/merging.md#duplicate-tags, D-6.4, D-6.5): the source's links move
/// to the target a batch of books at a time (counts moved by delta, the books' sort columns kept), its names become
/// aliases of the target, its import mappings, content filters and children follow, and the source goes. Everything that
/// moved is kept in a <see cref="MergeLog"/>, so an undo puts it back while no later merge of the target stands in the
/// way. Past <see cref="TagService.InlineLinks"/> links it runs in the background.
/// </summary>
public sealed partial class TagMerger(TenantDb tenantDb, BackgroundTasks tasks, TagService service, AccessChanges changes, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What a merged tag was and what moved with it, enough to make it again.</summary>
    internal sealed record TagData(
        string Name, string NormalizedName, string SortName, string? Shorthand, string? Color, bool IsCategory, bool IsHidden, int? DisambiguationParentId,
        int? FieldId, string? FieldKey, List<int> Parents, List<int> MovedAliases, int? AddedAlias, List<int> MovedChildren, List<int> DroppedChildren,
        List<int> Mappings, List<int> MovedFilters, List<FilterData> DroppedFilters, List<int> Disambiguated, List<int> KeyAliases,
        List<int>? MovedValues = null, int? KeyAlias = null, List<int>? FieldMappings = null, List<int>? DefaultTags = null, List<int>? Unreviewed = null);

    internal sealed record FilterData(int GroupId, bool Deny);

    public async Task<IReadOnlyList<MergeLogInfo>> ListAsync(int tenantId, long? before, int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var query = db.MergeLogs.Where(l => l.ParentId == null && l.Kind != MergeKind.Book);
        if (before is { } id)
            query = query.Where(l => l.Id < id);
        return await query.OrderByDescending(l => l.Id).Take(Math.Clamp(limit, 1, 100))
            .Select(l => new MergeLogInfo(l.Id, l.Kind, l.Created, l.SourceId, l.SourceName, l.TargetId, l.TargetName, l.Links, l.Completed, l.Undone))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Merges tag <paramref name="sourceId"/> into <paramref name="targetId"/>: plain tags or fields' values, the two
    /// fields may differ (<c>genre:Horror</c> into <c>category:Horror</c>). Refused for a field's own row (merge the
    /// fields), a built-in source, and a target under the source (its children would hold it twice).
    /// </summary>
    public async Task<AccessResult<TagWork>> MergeAsync(int tenantId, int sourceId, int targetId, int? userId, CancellationToken cancellationToken = default)
    {
        long links;
        MergeLog log;
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            var (problem, sourceName, targetName) = await CheckAsync(db, sourceId, targetId, cancellationToken);
            if (problem is not null)
                return problem;
            // Its links move, and its children with every book under them (the totals above them).
            links = Math.Max(await TagService.LinksAsync(db, [sourceId], cancellationToken), await TagService.BooksUnderAsync(db, [sourceId], cancellationToken));
            log = await NewLogAsync(db, tenantId, MergeKind.Tag, null, userId, sourceId, sourceName, targetId, targetName, cancellationToken);
        }

        if (service.InBackground(tenantId, links))
        {
            var run = await tasks.StartTenantAsync(tenantId, BackgroundTaskKind.TagMerge, userId,
                async (progress, cancel) => Result(await MergeTagAsync(tenantId, log.Id, sourceId, targetId, progress, cancel)), cancellationToken);
            return AccessResult<TagWork>.Ok(new TagWork(run, log.Id, links));
        }

        var moved = await MergeTagAsync(tenantId, log.Id, sourceId, targetId, null, cancellationToken);
        changes.Changed();
        return AccessResult<TagWork>.Ok(new TagWork(null, log.Id, moved));
    }

    /// <summary>Why tag <paramref name="sourceId"/> can't merge into <paramref name="targetId"/> (null when it can), and their names.</summary>
    private static async Task<(AccessResult<TagWork>? Problem, string Source, string Target)> CheckAsync(LibrarionDbContext db, int sourceId, int targetId,
        CancellationToken cancellationToken)
    {
        var tags = await db.Tags.Where(t => t.Id == sourceId || t.Id == targetId).Select(t => new { t.Id, t.Name, t.FieldKey, t.BuiltIn }).ToListAsync(cancellationToken);
        var source = tags.FirstOrDefault(t => t.Id == sourceId);
        var target = tags.FirstOrDefault(t => t.Id == targetId);
        if (source is null)
            return (AccessResult<TagWork>.Missing(), "", "");
        if (target is null || sourceId == targetId)
            return (AccessResult<TagWork>.Invalid("targetId", "target.unknown"), "", "");
        if (source.FieldKey is not null || target.FieldKey is not null)
            return (AccessResult<TagWork>.Invalid("targetId", "target.field"), "", "");
        if (source.BuiltIn is not null)
            return (AccessResult<TagWork>.Conflict("tag.builtIn"), "", "");
        if (await TagClosures.IsUnderAsync(db, targetId, sourceId, cancellationToken))
            return (AccessResult<TagWork>.Invalid("targetId", "target.under"), "", "");
        return (null, source.Name, target.Name);
    }

    /// <summary>
    /// Merges field <paramref name="sourceKey"/> into <paramref name="targetKey"/>: each value merged into the target's
    /// value of the same name, the others moved under the target field; the source's key stays as an alias of the
    /// target's (<c>genre:</c> keeps working in searches and import mappings). A built-in field is never the source.
    /// </summary>
    public async Task<AccessResult<TagWork>> MergeFieldAsync(int tenantId, string sourceKey, string targetKey, int? userId, CancellationToken cancellationToken = default)
    {
        long links;
        MergeLog log;
        int sourceId, targetId;
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            var fields = await db.Tags.Where(t => t.FieldKey == sourceKey || t.FieldKey == targetKey).Select(t => new { t.Id, t.Name, t.FieldKey, t.BuiltIn })
                .ToListAsync(cancellationToken);
            var source = fields.FirstOrDefault(f => f.FieldKey == sourceKey);
            var target = fields.FirstOrDefault(f => f.FieldKey == targetKey);
            if (source is null)
                return AccessResult<TagWork>.Missing();
            if (target is null || source.Id == target.Id)
                return AccessResult<TagWork>.Invalid("target", "target.unknown");
            if (source.BuiltIn is not null)
                return AccessResult<TagWork>.Conflict("tag.builtIn");
            if (await db.GroupTagFilters.AnyAsync(f => f.TagId == source.Id, cancellationToken))
                return AccessResult<TagWork>.Conflict("tag.inFilter");
            (sourceId, targetId) = (source.Id, target.Id);
            var values = db.Tags.Where(t => t.FieldId == source.Id).Select(t => t.Id);
            links = await db.TagCounts.Where(c => values.Contains(c.TagId)).SumAsync(c => (long)c.BookCount, cancellationToken);
            log = await NewLogAsync(db, tenantId, MergeKind.Field, null, userId, sourceId, sourceKey, targetId, targetKey, cancellationToken);
        }

        if (service.InBackground(tenantId, links))
        {
            var run = await tasks.StartTenantAsync(tenantId, BackgroundTaskKind.FieldMerge, userId,
                async (progress, cancel) => Result(await MergeFieldValuesAsync(tenantId, log.Id, sourceId, targetId, userId, progress, cancel)), cancellationToken);
            return AccessResult<TagWork>.Ok(new TagWork(run, log.Id, links));
        }

        var moved = await MergeFieldValuesAsync(tenantId, log.Id, sourceId, targetId, userId, null, cancellationToken);
        changes.Changed();
        return AccessResult<TagWork>.Ok(new TagWork(null, log.Id, moved));
    }

    /// <summary>
    /// Undoes a merge: the source made again (its id when free), its links, names, mappings, filters and children back.
    /// Refused once undone, when the target is gone, or while a later merge into or of the target stands (undo that one first).
    /// </summary>
    /// <param name="kind">Only a log of this kind (another is not found).</param>
    public async Task<AccessResult<TagWork>> UndoAsync(int tenantId, long logId, int? userId, CancellationToken cancellationToken = default, MergeKind? kind = null)
    {
        long links;
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            var log = await db.MergeLogs.FirstOrDefaultAsync(l => l.Id == logId && l.ParentId == null && l.Kind != MergeKind.Book, cancellationToken);
            if (log is null || kind is not null && log.Kind != kind)
                return AccessResult<TagWork>.Missing();
            if (await BlockedAsync(db, log, cancellationToken) is { } problem)
                return AccessResult<TagWork>.Conflict(problem);
            links = log.Links;
        }

        if (service.InBackground(tenantId, links))
        {
            var run = await tasks.StartTenantAsync(tenantId, BackgroundTaskKind.MergeUndo, userId,
                async (progress, cancel) => Result(await UndoLogAsync(tenantId, logId, null, progress, cancel)), cancellationToken);
            return AccessResult<TagWork>.Ok(new TagWork(run, logId, links));
        }

        var moved = await UndoLogAsync(tenantId, logId, null, null, cancellationToken);
        changes.Changed();
        return AccessResult<TagWork>.Ok(new TagWork(null, logId, moved));
    }

    private static ScanResult Result(long links) => new(0, (int)Math.Min(links, int.MaxValue), 0, 0, 0, 0, 0, [], []);

    private async Task<MergeLog> NewLogAsync(LibrarionDbContext db, int tenantId, MergeKind kind, long? parentId, int? userId, int sourceId, string sourceName, int targetId,
        string targetName, CancellationToken cancellationToken)
    {
        var log = new MergeLog
        {
            TenantId = tenantId, Kind = kind, ParentId = parentId, UserId = userId, Created = time.GetUtcNow().UtcDateTime,
            SourceId = sourceId, SourceName = Cut(sourceName), TargetId = targetId, TargetName = Cut(targetName),
        };
        db.MergeLogs.Add(log);
        await db.SaveChangesAsync(cancellationToken);
        return log;
    }

    private static async Task<string?> BlockedAsync(LibrarionDbContext db, MergeLog log, CancellationToken cancellationToken)
    {
        if (log.Undone is not null)
            return "merge.undone";
        if (!await db.Tags.AnyAsync(t => t.Id == log.TargetId, cancellationToken))
            return "merge.targetGone";
        // An ignore only gives the tag its links back: what came to it later stays.
        if (log.Kind == MergeKind.Ignore)
            return null;
        // A later merge of the target, or into it, took links this one would move back.
        if (await db.MergeLogs.AnyAsync(l => l.Id > log.Id && l.ParentId == null && l.Kind != MergeKind.Book && l.Undone == null && (l.TargetId == log.TargetId || l.SourceId == log.TargetId), cancellationToken))
            return "merge.later";
        return null;
    }

    // ---- Tag merge ----

    private async Task<long> MergeTagAsync(int tenantId, long logId, int sourceId, int targetId, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        long moved = 0;
        var after = 0;
        string name;
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
            name = await db.Tags.Where(t => t.Id == sourceId).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken)
                   ?? throw new InvalidOperationException($"Tag {sourceId} to merge is gone");
        while (true)
        {
            await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
            var last = after;
            var books = await db.BookTags.Where(l => l.TagId == sourceId && l.BookId > last).Select(l => l.BookId).Distinct().OrderBy(b => b).Take(TagService.BatchSize)
                .ToListAsync(cancellationToken);
            if (books.Count == 0)
                break;
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            moved += await MoveLinksAsync(db, tenantId, logId, sourceId, targetId, books, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            after = books[^1];
            progress?.Report(new ScanProgress(0, 0, 0, (int)Math.Min(moved, int.MaxValue), 0, name));
        }

        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var data = await FinishTagAsync(db, tenantId, sourceId, targetId, cancellationToken);
            await db.MergeLogs.Where(l => l.Id == logId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Completed, true)
                .SetProperty(l => l.Data, JsonSerializer.Serialize(data, Json)), cancellationToken);
            // The source left its groups (its rows went with it): the target's computed again.
            await TagDuplicateIndex.RefreshAsync(db, [targetId], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        changes.Changed();
        return moved;
    }

    /// <summary>One batch: the source's links of <paramref name="books"/> to the target (dropped where it has them), logged, the counts moved.</summary>
    private static async Task<int> MoveLinksAsync(LibrarionDbContext db, int tenantId, long logId, int sourceId, int targetId, List<int> books, CancellationToken cancellationToken)
    {
        var links = await db.BookTags.Where(l => l.TagId == sourceId && books.Contains(l.BookId)).AsNoTracking().ToListAsync(cancellationToken);
        var held = (await db.BookTags.Where(l => l.TagId == targetId && books.Contains(l.BookId)).Select(l => new { l.BookId, l.Source }).ToListAsync(cancellationToken))
            .Select(l => (l.BookId, l.Source)).ToHashSet();
        var before = await TagLinks.SnapshotAsync(db, books, cancellationToken);

        await db.BookTags.Where(l => l.TagId == sourceId && books.Contains(l.BookId)).ExecuteDeleteAsync(cancellationToken);
        foreach (var link in links)
        {
            var kept = held.Contains((link.BookId, link.Source));
            db.MergeLogLinks.Add(new MergeLogLink { TenantId = tenantId, LogId = logId, BookId = link.BookId, Source = link.Source, Ordinal = link.Ordinal, Number = link.Number, Kept = kept });
            if (!kept)
                db.BookTags.Add(new BookTag { TenantId = tenantId, BookId = link.BookId, TagId = targetId, Source = link.Source, Ordinal = link.Ordinal, Number = link.Number });
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        await TagLinks.CountAsync(db, tenantId, before, await TagLinks.SnapshotAsync(db, books, cancellationToken), cancellationToken);
        await TagLinks.SortColumnsAsync(db, books, cancellationToken);
        var count = links.Count;
        await db.MergeLogs.Where(l => l.Id == logId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Links, l => l.Links + count), cancellationToken);
        return count;
    }

    /// <summary>The source's names, mappings, filters and children go to the target; the source goes, what it was kept in the log's data.</summary>
    private static async Task<TagData> FinishTagAsync(LibrarionDbContext db, int tenantId, int sourceId, int targetId, CancellationToken cancellationToken)
    {
        var source = await db.Tags.AsNoTracking().FirstAsync(t => t.Id == sourceId, cancellationToken);
        var target = await db.Tags.AsNoTracking().FirstAsync(t => t.Id == targetId, cancellationToken);
        var parents = await db.TagParents.Where(p => p.ChildId == sourceId).Select(p => p.ParentId).ToListAsync(cancellationToken);

        // Names: the source's aliases become the target's, its name one more (unless the target is called so already).
        var aliases = await db.TagAliases.Where(a => a.TagId == sourceId).Select(a => a.Id).ToListAsync(cancellationToken);
        await db.TagAliases.Where(a => a.TagId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(a => a.TagId, targetId), cancellationToken);
        int? added = null;
        if (source.NormalizedName != target.NormalizedName && !await db.TagAliases.AnyAsync(a => a.TagId == targetId && a.NormalizedName == source.NormalizedName, cancellationToken))
        {
            var alias = new TagAlias { TenantId = tenantId, TagId = targetId, Name = source.Name, NormalizedName = source.NormalizedName };
            db.TagAliases.Add(alias);
            await db.SaveChangesAsync(cancellationToken);
            added = alias.Id;
        }

        // Its raw names now go to the target: a curator's choice, no longer the importer's (the Imported tags page).
        var mappings = await db.ImportTagMappings.Where(m => m.TagId == sourceId).Select(m => m.Id).ToListAsync(cancellationToken);
        var unreviewed = await db.ImportTagMappings.Where(m => m.TagId == sourceId && !m.Reviewed).Select(m => m.Id).ToListAsync(cancellationToken);
        await db.ImportTagMappings.Where(m => m.TagId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.TagId, targetId).SetProperty(m => m.Reviewed, true), cancellationToken);

        // Content filters: the target's own one for a group wins; the source's go to the target otherwise.
        var filters = await db.GroupTagFilters.Where(f => f.TagId == sourceId).Select(f => new { f.GroupId, f.Deny }).ToListAsync(cancellationToken);
        var held = await db.GroupTagFilters.Where(f => f.TagId == targetId).Select(f => f.GroupId).ToListAsync(cancellationToken);
        var movedFilters = filters.Where(f => !held.Contains(f.GroupId)).Select(f => f.GroupId).ToList();
        var droppedFilters = filters.Where(f => held.Contains(f.GroupId)).Select(f => new FilterData(f.GroupId, f.Deny)).ToList();
        await db.GroupTagFilters.Where(f => f.TagId == sourceId && movedFilters.Contains(f.GroupId)).ExecuteUpdateAsync(s => s.SetProperty(f => f.TagId, targetId), cancellationToken);

        var disambiguated = await db.Tags.Where(t => t.DisambiguationParentId == sourceId).Select(t => t.Id).ToListAsync(cancellationToken);
        await db.Tags.Where(t => t.DisambiguationParentId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DisambiguationParentId, (int?)targetId), cancellationToken);

        // Children: under the target now (those already there keep their one edge).
        var children = await db.TagParents.Where(p => p.ParentId == sourceId).Select(p => p.ChildId).ToListAsync(cancellationToken);
        var already = await db.TagParents.Where(p => p.ParentId == targetId && children.Contains(p.ChildId)).Select(p => p.ChildId).ToListAsync(cancellationToken);
        var movedChildren = children.Except(already).ToList();
        await db.TagParents.Where(p => p.ParentId == sourceId || p.ChildId == sourceId).ExecuteDeleteAsync(cancellationToken);
        db.TagParents.AddRange(movedChildren.Select(c => new TagParent { TenantId = tenantId, ChildId = c, ParentId = targetId }));
        await db.SaveChangesAsync(cancellationToken);
        await TagClosures.RebuildAsync(db, tenantId, children, cancellationToken);

        var keyAliases = await db.FieldKeyAliases.Where(a => a.FieldId == sourceId).Select(a => a.Id).ToListAsync(cancellationToken);
        await db.FieldKeyAliases.Where(a => a.FieldId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(a => a.FieldId, targetId), cancellationToken);

        // Default tags: the source's become the target's where the target isn't one already (those go with the source).
        var defaults = await db.DefaultTags.Where(d => d.TagId == sourceId
                && !db.DefaultTags.Any(o => o.TagId == targetId && o.LibraryId == d.LibraryId && o.FolderId == d.FolderId))
            .Select(d => d.Id).ToListAsync(cancellationToken);
        await db.DefaultTags.Where(d => defaults.Contains(d.Id)).ExecuteUpdateAsync(s => s.SetProperty(d => d.TagId, targetId), cancellationToken);

        await db.Tags.Where(t => t.Id == sourceId).ExecuteDeleteAsync(cancellationToken);
        return new TagData(source.Name, source.NormalizedName, source.SortName, source.Shorthand, source.Color, source.IsCategory, source.IsHidden,
            source.DisambiguationParentId, source.FieldId, source.FieldKey, parents, aliases, added, movedChildren, already, mappings, movedFilters, droppedFilters,
            disambiguated, keyAliases, DefaultTags: defaults, Unreviewed: unreviewed);
    }

    // ---- Field merge ----

    private async Task<long> MergeFieldValuesAsync(int tenantId, long logId, int sourceId, int targetId, int? userId, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        long moved = 0;
        var movedValues = new List<int>();
        var after = 0;
        while (true)
        {
            await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
            var last = after;
            var values = await db.Tags.Where(t => t.FieldId == sourceId && t.Id > last).OrderBy(t => t.Id).Take(TagService.BatchSize)
                .Select(t => new { t.Id, t.Name, t.NormalizedName }).ToListAsync(cancellationToken);
            if (values.Count == 0)
                break;
            after = values[^1].Id;
            var names = values.Select(v => v.NormalizedName).Distinct().ToList();
            var twins = (await db.Tags.Where(t => t.FieldId == targetId && names.Contains(t.NormalizedName)).Select(t => new { t.Id, t.Name, t.NormalizedName })
                    .ToListAsync(cancellationToken))
                .GroupBy(t => t.NormalizedName).ToDictionary(g => g.Key, g => g.MinBy(t => t.Id)!);

            // The values the target field lacks move under it as they are, links and all.
            var moving = values.Where(v => !twins.ContainsKey(v.NormalizedName)).Select(v => v.Id).ToList();
            if (moving.Count > 0)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                await db.Tags.Where(t => moving.Contains(t.Id)).ExecuteUpdateAsync(s => s.SetProperty(t => t.FieldId, (int?)targetId), cancellationToken);
                await TagClosures.RebuildAsync(db, tenantId, moving, cancellationToken);
                await SortColumnsOfAsync(db, targetId, moving, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                movedValues.AddRange(moving);
            }

            // The others merge into the target's value of their name, each a merge of its own under this one.
            foreach (var value in values.Where(v => twins.ContainsKey(v.NormalizedName)))
            {
                var twin = twins[value.NormalizedName];
                if (await TagClosures.IsUnderAsync(db, twin.Id, value.Id, cancellationToken))
                    continue;
                var child = await NewLogAsync(db, tenantId, MergeKind.Tag, logId, userId, value.Id, value.Name, twin.Id, twin.Name, cancellationToken);
                moved += await MergeTagAsync(tenantId, child.Id, value.Id, twin.Id, null, cancellationToken);
                progress?.Report(new ScanProgress(0, 0, 0, (int)Math.Min(moved, int.MaxValue), 0, value.Name));
            }
        }

        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            // A value left behind (under its own twin) keeps the source field until merged by hand.
            if (await db.Tags.AnyAsync(t => t.FieldId == sourceId, cancellationToken))
                throw new InvalidOperationException($"Field {sourceId} still has values after its merge");
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var key = await db.Tags.Where(t => t.Id == sourceId).Select(t => t.FieldKey!).FirstAsync(cancellationToken);
            var fieldMappings = await db.ImportTagMappings.Where(m => m.FieldId == sourceId).Select(m => m.Id).ToListAsync(cancellationToken);
            await db.ImportTagMappings.Where(m => m.FieldId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(m => m.FieldId, (int?)targetId), cancellationToken);
            var data = await FinishTagAsync(db, tenantId, sourceId, targetId, cancellationToken);
            var alias = new FieldKeyAlias { TenantId = tenantId, Key = key, FieldId = targetId };
            db.FieldKeyAliases.Add(alias);
            await db.SaveChangesAsync(cancellationToken);
            data = data with { MovedValues = movedValues, KeyAlias = alias.Id, FieldMappings = fieldMappings };
            await db.MergeLogs.Where(l => l.Id == logId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Completed, true)
                .SetProperty(l => l.Links, moved).SetProperty(l => l.Data, JsonSerializer.Serialize(data, Json)), cancellationToken);
            // The values moved under the target field leave the source field's groups and join its.
            await TagDuplicateIndex.RefreshAsync(db, movedValues, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        changes.Changed();
        return moved;
    }

    // ---- Undo ----

    /// <summary>
    /// Undoes <paramref name="logId"/>; <paramref name="fieldId"/> puts the source under that field (a field merge's
    /// value made again under its field, made again with a new id).
    /// </summary>
    private async Task<long> UndoLogAsync(int tenantId, long logId, int? fieldId, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        MergeLog log;
        TagData? data;
        int sourceId;
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            log = await db.MergeLogs.AsNoTracking().FirstAsync(l => l.Id == logId, cancellationToken);
            data = log.Data is null || log.Kind == MergeKind.Ignore ? null : JsonSerializer.Deserialize<TagData>(log.Data, Json);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // A merge stopped half-way left its source: only its links come back (an ignore never took its tag away).
            sourceId = log.Completed && data is not null ? await RemakeAsync(db, tenantId, log, data, fieldId, cancellationToken) : log.SourceId;
            await transaction.CommitAsync(cancellationToken);
        }

        long moved = 0;
        if (log.Kind == MergeKind.Ignore)
        {
            moved += await UndoIgnoreAsync(tenantId, log, progress, cancellationToken);
        }
        else if (log.Kind == MergeKind.Field && data is not null)
        {
            moved += await UndoFieldAsync(tenantId, log, data, sourceId, progress, cancellationToken);
        }
        else
        {
            var after = 0;
            while (true)
            {
                await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
                var last = after;
                // A batch of whole books: every row of each.
                var books = await db.MergeLogLinks.Where(l => l.LogId == logId && l.BookId > last).Select(l => l.BookId).Distinct().OrderBy(b => b)
                    .Take(TagService.BatchSize).ToListAsync(cancellationToken);
                if (books.Count == 0)
                    break;
                after = books[^1];
                var rows = await db.MergeLogLinks.Where(l => l.LogId == logId && books.Contains(l.BookId)).AsNoTracking().ToListAsync(cancellationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                moved += await MoveBackAsync(db, tenantId, rows, sourceId, log.TargetId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                progress?.Report(new ScanProgress(0, 0, 0, (int)Math.Min(moved, int.MaxValue), 0, log.SourceName));
            }
        }

        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            var now = time.GetUtcNow().UtcDateTime;
            await db.MergeLogLinks.Where(l => l.LogId == logId).ExecuteDeleteAsync(cancellationToken);
            await db.MergeLogs.Where(l => l.Id == logId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Undone, now), cancellationToken);
            if (log.Kind == MergeKind.Tag)
                await TagDuplicateIndex.RefreshAsync(db, [sourceId, log.TargetId], cancellationToken);
        }

        changes.Changed();
        return moved;
    }

    private static async Task<int> MoveBackAsync(LibrarionDbContext db, int tenantId, List<MergeLogLink> rows, int sourceId, int targetId, CancellationToken cancellationToken)
    {
        var books = rows.Select(r => r.BookId).Distinct().ToList();
        var present = (await db.Books.Where(b => books.Contains(b.Id)).Select(b => b.Id).ToListAsync(cancellationToken)).ToHashSet();
        var before = await TagLinks.SnapshotAsync(db, books, cancellationToken);
        var held = (await db.BookTags.Where(l => l.TagId == sourceId && books.Contains(l.BookId)).Select(l => new { l.BookId, l.Source }).ToListAsync(cancellationToken))
            .Select(l => (l.BookId, l.Source)).ToHashSet();
        var back = rows.Where(r => present.Contains(r.BookId)).ToList();
        foreach (var moved in back.Where(r => !r.Kept))
        {
            var book = moved.BookId;
            var source = moved.Source;
            await db.BookTags.Where(l => l.TagId == targetId && l.BookId == book && l.Source == source).ExecuteDeleteAsync(cancellationToken);
        }

        db.BookTags.AddRange(back.Where(r => !held.Contains((r.BookId, r.Source)))
            .Select(r => new BookTag { TenantId = tenantId, BookId = r.BookId, TagId = sourceId, Source = r.Source, Ordinal = r.Ordinal, Number = r.Number }));
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        await TagLinks.CountAsync(db, tenantId, before, await TagLinks.SnapshotAsync(db, books, cancellationToken), cancellationToken);
        await TagLinks.SortColumnsAsync(db, books, cancellationToken);
        return back.Count;
    }

    /// <summary>The source tag made again as it was (its id when free) with what the merge took from it; its id.</summary>
    private static async Task<int> RemakeAsync(LibrarionDbContext db, int tenantId, MergeLog log, TagData data, int? fieldId, CancellationToken cancellationToken)
    {
        var free = !await db.Tags.AnyAsync(t => t.Id == log.SourceId, cancellationToken);
        var tag = new Tag
        {
            TenantId = tenantId, Name = data.Name, NormalizedName = data.NormalizedName, SortName = data.SortName, Shorthand = data.Shorthand, Color = data.Color,
            IsCategory = data.IsCategory, IsHidden = data.IsHidden, FieldId = fieldId ?? data.FieldId, FieldKey = data.FieldKey,
        };
        if (free)
            tag.Id = log.SourceId;
        if (tag.FieldId is { } field && !await db.Tags.AnyAsync(t => t.Id == field, cancellationToken))
            tag.FieldId = null;
        NameKeys.Apply(tag);
        db.Tags.Add(tag);
        await db.SaveChangesAsync(cancellationToken);
        var id = tag.Id;
        var target = log.TargetId;

        var parents = await db.Tags.Where(t => data.Parents.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken);
        db.TagParents.AddRange(parents.Select(p => new TagParent { TenantId = tenantId, ChildId = id, ParentId = p }));
        if (data.DisambiguationParentId is { } disambiguation && parents.Contains(disambiguation))
            tag.DisambiguationParentId = disambiguation;
        await db.SaveChangesAsync(cancellationToken);
        await TagClosures.AddAsync(db, tenantId, id, cancellationToken);

        await db.TagAliases.Where(a => a.TagId == target && data.MovedAliases.Contains(a.Id)).ExecuteUpdateAsync(s => s.SetProperty(a => a.TagId, id), cancellationToken);
        if (data.AddedAlias is { } added)
            await db.TagAliases.Where(a => a.Id == added).ExecuteDeleteAsync(cancellationToken);
        await db.ImportTagMappings.Where(m => m.TagId == target && data.Mappings.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.TagId, id), cancellationToken);
        var unreviewed = data.Unreviewed ?? [];
        await db.ImportTagMappings.Where(m => m.TagId == id && unreviewed.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.Reviewed, false), cancellationToken);
        await db.GroupTagFilters.Where(f => f.TagId == target && data.MovedFilters.Contains(f.GroupId)).ExecuteUpdateAsync(s => s.SetProperty(f => f.TagId, id), cancellationToken);
        var groups = await db.Groups.Where(g => data.DroppedFilters.Select(f => f.GroupId).Contains(g.Id)).Select(g => g.Id).ToListAsync(cancellationToken);
        db.GroupTagFilters.AddRange(data.DroppedFilters.Where(f => groups.Contains(f.GroupId)).Select(f => new GroupTagFilter { GroupId = f.GroupId, TagId = id, Deny = f.Deny }));
        await db.Tags.Where(t => t.DisambiguationParentId == target && data.Disambiguated.Contains(t.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DisambiguationParentId, (int?)id), cancellationToken);
        await db.FieldKeyAliases.Where(a => a.FieldId == target && data.KeyAliases.Contains(a.Id)).ExecuteUpdateAsync(s => s.SetProperty(a => a.FieldId, id), cancellationToken);
        var defaults = data.DefaultTags ?? [];
        await db.DefaultTags.Where(d => d.TagId == target && defaults.Contains(d.Id)).ExecuteUpdateAsync(s => s.SetProperty(d => d.TagId, id), cancellationToken);

        // Children back under it: the moved edges leave the target, the dropped ones (already under the target) get theirs back.
        await db.TagParents.Where(p => p.ParentId == target && data.MovedChildren.Contains(p.ChildId)).ExecuteDeleteAsync(cancellationToken);
        var children = await db.Tags.Where(t => data.MovedChildren.Contains(t.Id) || data.DroppedChildren.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken);
        db.TagParents.AddRange(children.Select(c => new TagParent { TenantId = tenantId, ChildId = c, ParentId = id }));
        await db.SaveChangesAsync(cancellationToken);
        await TagClosures.RebuildAsync(db, tenantId, children, cancellationToken);
        return id;
    }

    /// <summary>A field merge undone: its moved values back under the remade field, then each value merge undone, newest first.</summary>
    private async Task<long> UndoFieldAsync(int tenantId, MergeLog log, TagData data, int fieldId, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        long moved = 0;
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (data.KeyAlias is { } alias)
                await db.FieldKeyAliases.Where(a => a.Id == alias).ExecuteDeleteAsync(cancellationToken);
            foreach (var chunk in (data.MovedValues ?? []).Chunk(TagService.BatchSize))
            {
                var ids = chunk.ToList();
                await db.Tags.Where(t => ids.Contains(t.Id) && t.FieldId == log.TargetId).ExecuteUpdateAsync(s => s.SetProperty(t => t.FieldId, (int?)fieldId), cancellationToken);
                await TagClosures.RebuildAsync(db, tenantId, ids, cancellationToken);
                await SortColumnsOfAsync(db, log.TargetId, ids, cancellationToken);
            }

            var mappings = data.FieldMappings ?? [];
            await db.ImportTagMappings.Where(m => m.FieldId == log.TargetId && mappings.Contains(m.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.FieldId, (int?)fieldId), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        List<long> children;
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
            children = await db.MergeLogs.Where(l => l.ParentId == log.Id && l.Undone == null).OrderByDescending(l => l.Id).Select(l => l.Id).ToListAsync(cancellationToken);
        foreach (var child in children)
        {
            moved += await UndoLogAsync(tenantId, child, fieldId, null, cancellationToken);
            progress?.Report(new ScanProgress(0, 0, 0, (int)Math.Min(moved, int.MaxValue), 0, log.SourceName));
        }

        return moved;
    }

    /// <summary>
    /// Values just moved into or out of the <c>author</c> or <c>series</c> field: their books' sort columns follow
    /// (a book's first author may be one of them now).
    /// </summary>
    private static async Task SortColumnsOfAsync(LibrarionDbContext db, int fieldId, List<int> values, CancellationToken cancellationToken)
    {
        var fields = await db.Tags.Where(t => t.FieldKey == "author" || t.FieldKey == "series").Select(t => t.Id).ToListAsync(cancellationToken);
        var moved = await db.Tags.Where(t => values.Contains(t.Id)).Select(t => t.FieldId).Distinct().ToListAsync(cancellationToken);
        if (!fields.Contains(fieldId) && !moved.Any(f => f is { } id && fields.Contains(id)))
            return;
        var books = await db.BookTags.Where(l => values.Contains(l.TagId)).Select(l => l.BookId).Distinct().ToListAsync(cancellationToken);
        await TagLinks.SortColumnsAsync(db, books, cancellationToken);
    }

    private static string Cut(string text) => text.Length > Tag.MaxNameLength ? text[..Tag.MaxNameLength] : text;
}
