using System.Text.Json;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Merging;

/// <summary>A book merge in the Duplicates page's list: the kept book and the books merged into it (the first few names).</summary>
/// <param name="Books">How many books were merged into the kept one.</param>
public sealed record BookMergeInfo(long Id, DateTime Created, int TargetId, string TargetName, IReadOnlyList<string> SourceNames, int Books, DateTime? Undone);

/// <summary>
/// Book merges listed and undone, and groups merged in bulk (stage 6 chunk 13, the user's answers 2026-11-09,
/// agents/docs/features/merging.md#undo-bulk-merge-and-split-built-stage-6-chunk-13).
/// </summary>
public sealed partial class BookMerger
{
    /// <summary>The levels merged in bulk (the user's answer): the same file, the same identifier; title and author groups through Compare only.</summary>
    public static readonly DuplicateReason[] BulkLevels = [DuplicateReason.File, DuplicateReason.Identifier];

    public const int ListSize = 50;

    /// <summary>Groups read a chunk at a time by the bulk merge.</summary>
    private const int BulkChunk = 100;

    /// <summary>A library's book merges, the newest first (<paramref name="before"/>: the last id of the page before).</summary>
    public async Task<IReadOnlyList<BookMergeInfo>> ListAsync(int tenantId, int libraryId, long? before, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var query = db.MergeLogs.Where(l => l.Kind == MergeKind.Book && l.ParentId == null && db.Books.Any(b => b.Id == l.TargetId && b.LibraryId == libraryId));
        if (before is { } last)
            query = query.Where(l => l.Id < last);
        var logs = await query.OrderByDescending(l => l.Id).Take(ListSize)
            .Select(l => new { l.Id, l.Created, l.TargetId, l.TargetName, l.SourceName, l.Undone }).ToListAsync(cancellationToken);
        var ids = logs.Select(l => l.Id).ToList();
        var children = (await db.MergeLogs.Where(l => l.ParentId != null && ids.Contains(l.ParentId.Value)).OrderBy(l => l.Id)
            .Select(l => new { ParentId = l.ParentId!.Value, l.SourceName }).ToListAsync(cancellationToken)).ToLookup(l => l.ParentId, l => l.SourceName);
        return logs.Select(l =>
        {
            var names = children[l.Id].Prepend(l.SourceName).ToList();
            return new BookMergeInfo(l.Id, l.Created, l.TargetId, l.TargetName, names.Take(5).ToList(), names.Count, l.Undone);
        }).ToList();
    }

    /// <summary>The kept book of a merge and its library (rights are checked there); null when the log isn't a book merge or the book is gone.</summary>
    public async Task<(int BookId, int LibraryId)?> LogBookAsync(int tenantId, long logId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var book = await db.MergeLogs.Where(l => l.Id == logId && l.Kind == MergeKind.Book)
            .Join(db.Books, l => l.TargetId, b => b.Id, (l, b) => new { b.Id, b.LibraryId }).FirstOrDefaultAsync(cancellationToken);
        return book is null ? null : (book.Id, book.LibraryId);
    }

    /// <summary>
    /// A whole merge undone (a child log stands for its parent): each merged book gets back its files still on the kept
    /// book, its links, identifiers, properties and suppressions, and stops redirecting; what it gave the kept book is
    /// taken off it, and the kept book's title, description, rating and cover go back when unchanged since. A merged book
    /// none of whose files is left (removed since) stays merged. Refused once undone, and while the kept book itself
    /// was merged into another later (undo that one first). Answers the books given back.
    /// </summary>
    public async Task<AccessResult<int>> UndoAsync(int tenantId, long logId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var log = await db.MergeLogs.FirstOrDefaultAsync(l => l.Id == logId && l.Kind == MergeKind.Book, cancellationToken);
        if (log?.ParentId is { } parentId)
            log = await db.MergeLogs.FirstOrDefaultAsync(l => l.Id == parentId, cancellationToken);
        if (log is null)
            return AccessResult<int>.Missing();
        if (log.Undone is not null)
            return AccessResult<int>.Conflict("merge.undone");
        var target = await db.Books.FirstOrDefaultAsync(b => b.Id == log.TargetId, cancellationToken);
        if (target is null)
            return AccessResult<int>.Conflict("merge.targetGone");
        if (target.MergedIntoId is not null)
            return AccessResult<int>.Conflict("merge.later");

        var logs = await db.MergeLogs.Where(l => l.Id == log.Id || l.ParentId == log.Id).OrderBy(l => l.Id).ToListAsync(cancellationToken);
        var sourceIds = logs.Select(l => l.SourceId).ToList();
        var sources = await db.Books.Where(b => sourceIds.Contains(b.Id) && b.MergedIntoId == target.Id).ToDictionaryAsync(b => b.Id, cancellationToken);
        var all = sourceIds.Prepend(target.Id).ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var before = await TagLinks.SnapshotAsync(db, all, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var restored = 0;
        BookMergeData? first = null;
        foreach (var entry in logs)
        {
            var data = JsonSerializer.Deserialize<BookMergeData>(entry.Data ?? "null", Json) ?? throw new InvalidDataException($"Merge log {entry.Id} has no data.");
            first ??= data;
            if (!sources.TryGetValue(entry.SourceId, out var source))
                continue;
            var files = data.Files.ToList();
            if (await db.BookFiles.Where(f => files.Contains(f.Id) && f.BookId == target.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.BookId, source.Id), cancellationToken) == 0)
                continue;

            var moved = data.Links.Where(l => l.Moved).Select(l => l.TagId).ToList();
            await db.BookTags.Where(l => l.BookId == target.Id && moved.Contains(l.TagId)).ExecuteDeleteAsync(cancellationToken);
            var has = (await db.BookTags.Where(l => l.BookId == source.Id).Select(l => l.TagId).ToListAsync(cancellationToken)).ToHashSet();
            db.BookTags.AddRange(data.Links.Where(l => has.Add(l.TagId)).Select(l => new BookTag
            {
                TenantId = tenantId, BookId = source.Id, TagId = l.TagId, Source = l.Source, Ordinal = l.Ordinal, Number = l.Number,
            }));

            foreach (var identifier in data.Identifiers.Where(i => i.Moved))
                await db.BookIdentifiers.Where(i => i.BookId == target.Id && i.Scheme == identifier.Key && i.Value == identifier.Value).ExecuteDeleteAsync(cancellationToken);
            var held = (await db.BookIdentifiers.Where(i => i.BookId == source.Id).Select(i => new { i.Scheme, i.Value }).ToListAsync(cancellationToken))
                .Select(i => (i.Scheme, i.Value)).ToHashSet();
            db.BookIdentifiers.AddRange(data.Identifiers.Where(i => held.Add((i.Key, i.Value)))
                .Select(i => new BookIdentifier { TenantId = tenantId, BookId = source.Id, Scheme = i.Key, Value = i.Value }));

            foreach (var property in data.Properties.Where(p => p.Moved))
                await db.BookProperties.Where(p => p.BookId == target.Id && p.Name == property.Key && p.Value == property.Value).ExecuteDeleteAsync(cancellationToken);
            var named = (await db.BookProperties.Where(p => p.BookId == source.Id).Select(p => p.Name).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
            db.BookProperties.AddRange(data.Properties.Where(p => named.Add(p.Key))
                .Select(p => new BookProperty { TenantId = tenantId, BookId = source.Id, Name = p.Key, Value = p.Value }));

            var suppressed = (await db.BookTagSuppressions.Where(s => s.BookId == source.Id).Select(s => s.TagId).ToListAsync(cancellationToken)).ToHashSet();
            db.BookTagSuppressions.AddRange(data.Suppressed.Where(suppressed.Add).Select(tagId => new BookTagSuppression { TenantId = tenantId, BookId = source.Id, TagId = tagId }));

            source.MergedIntoId = null;
            source.Updated = now;
            restored++;
        }

        if (first is not null)
            Restore(target, first.Target, first.After);
        target.Updated = now;
        foreach (var entry in logs)
            entry.Undone = now;
        await db.SaveChangesAsync(cancellationToken);

        var after = await TagLinks.SnapshotAsync(db, all, cancellationToken);
        await TagLinks.CountAsync(db, tenantId, before, after, cancellationToken);
        await db.Libraries.Where(l => l.Id == target.LibraryId).ExecuteUpdateAsync(s => s.SetProperty(l => l.BookCount, l => l.BookCount + restored), cancellationToken);
        await TagLinks.SortColumnsAsync(db, all, cancellationToken);
        if (await DuplicateIndex.BuiltAsync(db, cancellationToken))
            await DuplicateIndex.RefreshAsync(db, all, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return AccessResult<int>.Ok(restored);
    }

    /// <summary>The kept book's single values as they were before the merge, each only when still what the merge made it.</summary>
    private static void Restore(Book target, TargetData was, TargetData set)
    {
        if (target.Title == set.Title && target.SortTitle == set.SortTitle)
        {
            target.Title = was.Title;
            target.SortTitle = was.SortTitle;
            target.SearchText = was.SearchText;
            Flag(target, was, BookEdits.Title);
        }

        if (target.Description == set.Description)
        {
            target.Description = was.Description;
            Flag(target, was, BookEdits.Description);
        }

        if (target.Rating == set.Rating)
        {
            target.Rating = was.Rating;
            Flag(target, was, BookEdits.Rating);
        }

        if (target.CoverSource == set.CoverSource && target.CoverFile == set.CoverFile && target.GeneratedTemplate == set.GeneratedTemplate
            && target.GeneratedVariant == set.GeneratedVariant)
        {
            // Its own files are what they were: the cover inside them is looked for again.
            target.CoverSource = was.CoverSource;
            target.CoverFile = was.CoverFile;
            target.GeneratedTemplate = was.GeneratedTemplate;
            target.GeneratedVariant = was.GeneratedVariant;
            target.CoverState = CoverState.Unknown;
            Flag(target, was, BookEdits.Cover);
        }
    }

    private static void Flag(Book target, TargetData was, BookEdits flag) => target.Edited = (target.Edited & ~flag) | (was.Edited & flag);

    /// <summary>
    /// The book a merge keeps by default (as the compare sheet): the one with most metadata (tags, identifiers, files,
    /// title, description, rating), else the oldest.
    /// </summary>
    public static async Task<int> KeptAsync(LibrarionDbContext db, IReadOnlyCollection<int> bookIds, CancellationToken cancellationToken)
    {
        var scores = await db.Books.Where(b => bookIds.Contains(b.Id))
            .Select(b => new
            {
                b.Id,
                Score = b.Tags.Count + b.Identifiers.Count + b.Files.Count + (b.Title != null ? 1 : 0) + (b.Description != null ? 1 : 0) + (b.Rating != null ? 1 : 0),
            })
            .ToListAsync(cancellationToken);
        return scores.OrderByDescending(s => s.Score).ThenBy(s => s.Id).First().Id;
    }

    /// <summary>
    /// Groups merged in bulk, each with the default choices (<see cref="KeptAsync"/>, the kept book's own values) in a
    /// transaction of its own: <paramref name="groupIds"/>, else every group of <paramref name="level"/>. Only the books of
    /// <paramref name="libraryId"/> the caller sees; a group of another level than <see cref="BulkLevels"/>, with fewer
    /// than two such books, or with two marked not duplicates is skipped. Answers the groups merged.
    /// </summary>
    public async Task<(int Merged, int Skipped)> MergeGroupsAsync(int tenantId, int? userId, BookVisibility visibility, int libraryId, IReadOnlyList<long>? groupIds,
        DuplicateReason? level, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        int merged = 0, skipped = 0;
        var chosen = groupIds?.Distinct().Order().ToList();
        long last = 0;
        while (true)
        {
            var work = new List<List<int>>();
            await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
            {
                var after = last;
                var query = db.DuplicateGroups.Where(g => g.Id > after && g.BookCount >= 2 && BulkLevels.Contains(g.Level));
                query = chosen is not null ? query.Where(g => chosen.Contains(g.Id)) : query.Where(g => g.Level == level);
                var chunk = await query.OrderBy(g => g.Id).Take(BulkChunk).Select(g => g.Id).ToListAsync(cancellationToken);
                if (chunk.Count == 0)
                    break;
                last = chunk[^1];
                var members = await db.DuplicateMembers.Where(m => chunk.Contains(m.GroupId)).Select(m => new { m.GroupId, m.BookId }).ToListAsync(cancellationToken);
                var ids = members.Select(m => m.BookId).Distinct().ToList();
                var visible = await BookQueries.VisibleAsync(db, visibility, libraryId, cancellationToken);
                var shown = new HashSet<int>();
                var apart = new HashSet<(int, int)>();
                foreach (var part in ids.Chunk(1000))
                {
                    shown.UnionWith(await visible.Where(b => part.Contains(b.Id)).Select(b => b.Id).ToListAsync(cancellationToken));
                    apart.UnionWith((await db.NotDuplicates.Where(n => part.Contains(n.BookId)).Select(n => new { n.BookId, n.OtherId }).ToListAsync(cancellationToken))
                        .Select(n => (n.BookId, n.OtherId)));
                }

                foreach (var group in members.GroupBy(m => m.GroupId).OrderBy(g => g.Key))
                {
                    var books = group.Select(m => m.BookId).Where(shown.Contains).Order().ToList();
                    if (books.Count >= 2 && !books.Any(a => apart.Contains((a, a)) || books.Any(b => a < b && apart.Contains((a, b)))))
                        work.Add(books);
                    else
                        skipped++;
                }

                skipped += chunk.Count - members.Select(m => m.GroupId).Distinct().Count();
            }

            foreach (var books in work)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A book an earlier group of this run merged (two groups can share books) is left out.
                int kept;
                List<int> current;
                await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
                {
                    current = await db.Books.Where(b => books.Contains(b.Id) && b.MergedIntoId == null && b.MissingSince == null).Select(b => b.Id).ToListAsync(cancellationToken);
                    kept = current.Count >= 2 ? await KeptAsync(db, current, cancellationToken) : 0;
                }

                if (current.Count >= 2 && (await MergeAsync(tenantId, userId, new BookMergeInput(kept, current.Where(id => id != kept).ToList()), cancellationToken)).Status == AccessStatus.Ok)
                    merged++;
                else
                    skipped++;
                progress?.Report(new ScanProgress(merged + skipped, 0, 0, merged, 0, null, Skipped: skipped));
            }
        }

        return (merged, skipped);
    }
}
