using System.Text.Json;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Tagging;
using Librarion.Core.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Merging;

/// <summary>An ignore as the Imported tags page lists it: the tag, its raw names ignored, the imported links taken off.</summary>
public sealed record IgnoredTagInfo(long Id, DateTime Created, int TagId, string TagName, IReadOnlyList<string> Names, long Links, bool Completed);

/// <summary>
/// The Imported tags page's changes (stage 6 chunk 9, agents/docs/features/imported-tags.md): several tags mapped onto
/// one (each a merge of its own, undone one by one) and tags' raw names ignored, both logged in <see cref="MergeLog"/>.
/// </summary>
public sealed partial class TagMerger
{
    /// <summary>At most this many tags in one change of the page.</summary>
    public const int MaxBulk = 100;

    /// <summary>More tags than this in one change run in the background whatever their links (each is a merge of its own).</summary>
    public const int InlineTags = 10;

    /// <summary>What an ignore changed: the mappings it ignored and which of them were not reviewed yet.</summary>
    internal sealed record IgnoreData(List<int> Mappings, List<int> Unreviewed);

    /// <summary>
    /// Maps tags <paramref name="sourceIds"/> onto <paramref name="targetId"/>: each merged into it (its raw names follow
    /// as reviewed mappings), refused as a whole when one can't be.
    /// </summary>
    public async Task<AccessResult<TagWork>> MapAsync(int tenantId, IReadOnlyCollection<int> sourceIds, int targetId, int? userId, CancellationToken cancellationToken = default)
    {
        var ids = sourceIds.Distinct().ToList();
        if (ids.Count is 0 or > MaxBulk)
            return AccessResult<TagWork>.Invalid("tagIds", "tags.count");
        long links;
        var logs = new List<(long Log, int Source)>();
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            var names = new List<(int Id, string Source, string Target)>();
            foreach (var id in ids)
            {
                var (problem, sourceName, targetName) = await CheckAsync(db, id, targetId, cancellationToken);
                if (problem is not null)
                    return problem;
                names.Add((id, sourceName, targetName));
            }

            links = Math.Max(await TagService.LinksAsync(db, ids, cancellationToken), await TagService.BooksUnderAsync(db, ids, cancellationToken));
            foreach (var (id, sourceName, targetName) in names)
                logs.Add(((await NewLogAsync(db, tenantId, MergeKind.Tag, null, userId, id, sourceName, targetId, targetName, cancellationToken)).Id, id));
        }

        async Task<long> All(IProgress<ScanProgress>? progress, CancellationToken cancel)
        {
            long moved = 0;
            foreach (var (log, source) in logs)
                moved += await MergeTagAsync(tenantId, log, source, targetId, progress, cancel);
            return moved;
        }

        if (ids.Count > InlineTags || service.InBackground(tenantId, links))
        {
            var run = await tasks.StartTenantAsync(tenantId, BackgroundTaskKind.TagMerge, userId,
                async (progress, cancel) => Result(await All(progress, cancel)), cancellationToken);
            return AccessResult<TagWork>.Ok(new TagWork(run, logs.Count == 1 ? logs[0].Log : null, links));
        }

        var done = await All(null, cancellationToken);
        changes.Changed();
        return AccessResult<TagWork>.Ok(new TagWork(null, logs.Count == 1 ? logs[0].Log : null, done));
    }

    /// <summary>
    /// Ignores the raw names of tags <paramref name="tagIds"/>: every mapping to each becomes ignored (imports skip the
    /// name from now on) and its imported links are taken off, logged so an undo gives them back. The tags stay, and so
    /// do their manual and rule links. Refused for a tag without an imported name.
    /// </summary>
    public async Task<AccessResult<TagWork>> IgnoreAsync(int tenantId, IReadOnlyCollection<int> tagIds, int? userId, CancellationToken cancellationToken = default)
    {
        var ids = tagIds.Distinct().ToList();
        if (ids.Count is 0 or > MaxBulk)
            return AccessResult<TagWork>.Invalid("tagIds", "tags.count");
        long links;
        var logs = new List<(long Log, int Tag)>();
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            var tags = await db.Tags.Where(t => ids.Contains(t.Id)).Select(t => new { t.Id, t.Name }).ToListAsync(cancellationToken);
            if (tags.Count < ids.Count)
                return AccessResult<TagWork>.Missing();
            var imported = await db.ImportTagMappings.Where(m => m.TagId != null && ids.Contains(m.TagId.Value) && !m.Ignored).Select(m => m.TagId!.Value).Distinct()
                .ToListAsync(cancellationToken);
            if (imported.Count < ids.Count)
                return AccessResult<TagWork>.Invalid("tagIds", "tag.notImported");
            links = await TagService.LinksAsync(db, ids, cancellationToken);
            foreach (var tag in tags)
                logs.Add(((await NewLogAsync(db, tenantId, MergeKind.Ignore, null, userId, tag.Id, tag.Name, tag.Id, tag.Name, cancellationToken)).Id, tag.Id));
        }

        async Task<long> All(IProgress<ScanProgress>? progress, CancellationToken cancel)
        {
            long removed = 0;
            foreach (var (log, tag) in logs)
                removed += await IgnoreTagAsync(tenantId, log, tag, progress, cancel);
            return removed;
        }

        if (ids.Count > InlineTags || service.InBackground(tenantId, links))
        {
            var run = await tasks.StartTenantAsync(tenantId, BackgroundTaskKind.TagIgnore, userId,
                async (progress, cancel) => Result(await All(progress, cancel)), cancellationToken);
            return AccessResult<TagWork>.Ok(new TagWork(run, logs.Count == 1 ? logs[0].Log : null, links));
        }

        var done = await All(null, cancellationToken);
        changes.Changed();
        return AccessResult<TagWork>.Ok(new TagWork(null, logs.Count == 1 ? logs[0].Log : null, done));
    }

    /// <summary>The ignores that stand, newest first; <paramref name="before"/> = the last id of the page before.</summary>
    public async Task<IReadOnlyList<IgnoredTagInfo>> IgnoredAsync(int tenantId, long? before, int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var query = db.MergeLogs.Where(l => l.Kind == MergeKind.Ignore && l.Undone == null);
        if (before is { } id)
            query = query.Where(l => l.Id < id);
        var logs = await query.OrderByDescending(l => l.Id).Take(Math.Clamp(limit, 1, 100))
            .Select(l => new { l.Id, l.Created, l.SourceId, l.SourceName, l.Links, l.Completed, l.Data }).ToListAsync(cancellationToken);
        var mappings = logs.SelectMany(l => Data(l.Data)?.Mappings ?? []).Distinct().ToList();
        var names = (await db.ImportTagMappings.Where(m => mappings.Contains(m.Id)).Select(m => new { m.Id, m.Name }).ToListAsync(cancellationToken))
            .ToDictionary(m => m.Id, m => m.Name);
        return logs.Select(l => new IgnoredTagInfo(l.Id, l.Created, l.SourceId, l.SourceName,
            (Data(l.Data)?.Mappings ?? []).Where(names.ContainsKey).Select(m => names[m]).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), l.Links, l.Completed)).ToList();
    }

    private static IgnoreData? Data(string? json) => json is null ? null : JsonSerializer.Deserialize<IgnoreData>(json, Json);

    /// <summary>One tag's names ignored (first, so an import running meanwhile stops giving it), then its imported links taken off by batches.</summary>
    private async Task<long> IgnoreTagAsync(int tenantId, long logId, int tagId, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        string name;
        bool sorted;
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            var tag = await db.Tags.Where(t => t.Id == tagId).Select(t => new { t.Name, t.Field!.FieldKey }).FirstOrDefaultAsync(cancellationToken)
                      ?? throw new InvalidOperationException($"Tag {tagId} to ignore is gone");
            name = tag.Name;
            sorted = tag.FieldKey is "author" or "series";
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var mappings = await db.ImportTagMappings.Where(m => m.TagId == tagId && !m.Ignored).Select(m => new { m.Id, m.Reviewed }).ToListAsync(cancellationToken);
            var data = new IgnoreData(mappings.Select(m => m.Id).ToList(), mappings.Where(m => !m.Reviewed).Select(m => m.Id).ToList());
            var ids = data.Mappings;
            await db.ImportTagMappings.Where(m => ids.Contains(m.Id)).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Ignored, true).SetProperty(m => m.TagId, (int?)null).SetProperty(m => m.Reviewed, true), cancellationToken);
            await db.MergeLogs.Where(l => l.Id == logId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Data, JsonSerializer.Serialize(data, Json)), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        long removed = 0;
        var after = 0;
        while (true)
        {
            await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
            var last = after;
            var links = await db.BookTags.Where(l => l.TagId == tagId && l.Source == TagSource.Imported && l.BookId > last).OrderBy(l => l.BookId)
                .Take(TagService.BatchSize).AsNoTracking().ToListAsync(cancellationToken);
            if (links.Count == 0)
                break;
            after = links[^1].BookId;
            var books = links.Select(l => l.BookId).ToList();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var before = await TagLinks.SnapshotAsync(db, books, cancellationToken);
            await db.BookTags.Where(l => l.TagId == tagId && l.Source == TagSource.Imported && books.Contains(l.BookId)).ExecuteDeleteAsync(cancellationToken);
            db.MergeLogLinks.AddRange(links.Select(l => new MergeLogLink { TenantId = tenantId, LogId = logId, BookId = l.BookId, Source = l.Source, Ordinal = l.Ordinal, Number = l.Number }));
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            await TagLinks.CountAsync(db, tenantId, before, await TagLinks.SnapshotAsync(db, books, cancellationToken), cancellationToken);
            if (sorted)
                await TagLinks.SortColumnsAsync(db, books, cancellationToken);
            var count = links.Count;
            await db.MergeLogs.Where(l => l.Id == logId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Links, l => l.Links + count), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            removed += count;
            progress?.Report(new ScanProgress(0, 0, 0, (int)Math.Min(removed, int.MaxValue), 0, name));
        }

        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
            await db.MergeLogs.Where(l => l.Id == logId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Completed, true), cancellationToken);
        changes.Changed();
        return removed;
    }

    /// <summary>An ignore undone: its mappings back to the tag (unreviewed again where they were), then its links, where the books still are.</summary>
    private async Task<long> UndoIgnoreAsync(int tenantId, MergeLog log, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var tagId = log.TargetId;
        bool sorted;
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            sorted = await db.Tags.Where(t => t.Id == tagId).Select(t => t.Field!.FieldKey).FirstOrDefaultAsync(cancellationToken) is "author" or "series";
            if (Data(log.Data) is { } data)
            {
                var ids = data.Mappings;
                var unreviewed = data.Unreviewed;
                // A name mapped again since (an import can't: it is ignored; a curator could) keeps that.
                await db.ImportTagMappings.Where(m => ids.Contains(m.Id) && m.Ignored && m.TagId == null).ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Ignored, false).SetProperty(m => m.TagId, (int?)tagId).SetProperty(m => m.Reviewed, m => !unreviewed.Contains(m.Id)), cancellationToken);
            }
        }

        long restored = 0;
        var after = 0;
        while (true)
        {
            await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
            var last = after;
            var rows = await db.MergeLogLinks.Where(l => l.LogId == log.Id && l.BookId > last).OrderBy(l => l.BookId).Take(TagService.BatchSize).AsNoTracking()
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
                break;
            after = rows[^1].BookId;
            var books = rows.Select(r => r.BookId).ToList();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var present = (await db.Books.Where(b => books.Contains(b.Id)).Select(b => b.Id).ToListAsync(cancellationToken)).ToHashSet();
            var held = (await db.BookTags.Where(l => l.TagId == tagId && l.Source == TagSource.Imported && books.Contains(l.BookId)).Select(l => l.BookId)
                .ToListAsync(cancellationToken)).ToHashSet();
            var back = rows.Where(r => present.Contains(r.BookId) && !held.Contains(r.BookId)).ToList();
            var before = await TagLinks.SnapshotAsync(db, books, cancellationToken);
            db.BookTags.AddRange(back.Select(r => new BookTag { TenantId = tenantId, BookId = r.BookId, TagId = tagId, Source = TagSource.Imported, Ordinal = r.Ordinal, Number = r.Number }));
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            await TagLinks.CountAsync(db, tenantId, before, await TagLinks.SnapshotAsync(db, books, cancellationToken), cancellationToken);
            if (sorted)
                await TagLinks.SortColumnsAsync(db, books, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            restored += back.Count;
            progress?.Report(new ScanProgress(0, 0, 0, (int)Math.Min(restored, int.MaxValue), 0, log.SourceName));
        }

        return restored;
    }
}
