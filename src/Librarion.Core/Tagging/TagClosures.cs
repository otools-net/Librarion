using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Tagging;

/// <summary>
/// What a closure rebuild changed: for each tag whose ancestors outside the rebuilt subtree changed, those ancestors
/// before and after. <see cref="TagClosures.CountAsync"/> moves the totals of the ancestors that gained or lost its books.
/// </summary>
public sealed class ClosureChange
{
    internal Dictionary<int, HashSet<int>> Before { get; } = [];
    internal Dictionary<int, HashSet<int>> After { get; } = [];

    public bool IsEmpty => Before.Count == 0;
}

/// <summary>
/// Keeps <see cref="TagClosure"/> right when parents change (agents/docs/features/tags.md#scale), set-based on both
/// providers, never a walk per tag: after the parent edges of <c>roots</c> changed, the ancestors of everything under
/// them (D, the roots included) are rebuilt. Pairs inside D stay (their paths never leave D, whose own edges didn't
/// change); pairs from outside D are dropped, then made again from each edge leaving D (a parent, or a value's
/// field) times that parent's ancestors. Correct for several roots at once as long as every edge that changed points
/// out of D, which holds for re-parenting (no cycles), deleting a tag (its children are the roots) and merging (the
/// source's children, the target not under them). The totals of the ancestors gained or lost move with it
/// (<see cref="TagCount.TotalCount"/>), for the books under the tags whose ancestors changed only.
/// </summary>
public static class TagClosures
{
    private static long version;

    /// <summary>Bumped by every rebuild: a <see cref="TagAncestors"/> read before it starts over (a scan running meanwhile).</summary>
    public static long Version => Interlocked.Read(ref version);

    /// <summary>
    /// Rebuilds under <paramref name="roots"/>; the totals move now unless <paramref name="countNow"/> is false (a big
    /// subtree: the caller runs <see cref="CountAsync"/> in the background with the change answered).
    /// </summary>
    public static async Task<ClosureChange> RebuildAsync(LibrarionDbContext db, int tenantId, IReadOnlyCollection<int> roots, CancellationToken cancellationToken,
        bool countNow = true)
    {
        var change = new ClosureChange();
        foreach (var chunk in roots.Distinct().Chunk(500))
        {
            var ids = chunk.ToArray();
            var under = db.TagClosure.Where(c => ids.Contains(c.AncestorId)).Select(c => c.DescendantId);
            var outside = db.TagClosure.Where(c => under.Contains(c.DescendantId) && !under.Contains(c.AncestorId));
            var before = (await outside.Select(c => new { c.AncestorId, c.DescendantId }).ToListAsync(cancellationToken))
                .ToLookup(c => c.DescendantId, c => c.AncestorId);
            // Rows from an ancestor in D to a root (a root's own row among them) are never deleted here, so `under` reads
            // the same before and after.
            await outside.ExecuteDeleteAsync(cancellationToken);

            var parents = db.TagParents.Where(p => under.Contains(p.ChildId) && !under.Contains(p.ParentId)).Select(p => new { Child = p.ChildId, Parent = p.ParentId });
            var fields = db.Tags.Where(t => t.FieldId != null && under.Contains(t.Id)).Select(t => new { Child = t.Id, Parent = t.FieldId!.Value });
            var made = new Dictionary<(int Ancestor, int Descendant), int>();
            foreach (var edges in new[] { parents, fields })
            {
                var rows = from edge in edges
                           join below in db.TagClosure on edge.Child equals below.AncestorId
                           join above in db.TagClosure on edge.Parent equals above.DescendantId
                           select new { above.AncestorId, below.DescendantId, Depth = above.Depth + 1 + below.Depth };
                foreach (var row in await rows.ToListAsync(cancellationToken))
                    if (!made.TryGetValue((row.AncestorId, row.DescendantId), out var depth) || row.Depth < depth)
                        made[(row.AncestorId, row.DescendantId)] = row.Depth;
            }

            foreach (var batch in made.Chunk(5000))
            {
                var added = batch.Select(r => new TagClosure { TenantId = tenantId, AncestorId = r.Key.Ancestor, DescendantId = r.Key.Descendant, Depth = r.Value }).ToList();
                db.TagClosure.AddRange(added);
                await db.SaveChangesAsync(cancellationToken);
                // Not kept tracked: thousands of rows (a category's) would slow every later save of the caller.
                foreach (var row in added)
                    db.Entry(row).State = EntityState.Detached;
            }

            var after = made.Keys.ToLookup(k => k.Descendant, k => k.Ancestor);
            foreach (var tag in before.Select(g => g.Key).Union(after.Select(g => g.Key)))
            {
                var was = before[tag].ToHashSet();
                var now = after[tag].ToHashSet();
                if (was.SetEquals(now))
                    continue;
                change.Before[tag] = was;
                change.After[tag] = now;
            }
        }

        Interlocked.Increment(ref version);
        if (countNow)
            await CountAsync(db, tenantId, change, cancellationToken);
        return change;
    }

    /// <summary>
    /// The totals a <see cref="ClosureChange"/> moves: for each present book under a changed tag, the ancestors it was
    /// and is under, among those that changed (its other tags' ancestors taken into account: a book still under
    /// "Fiction" through another tag keeps counting there). A batch of 1,000 books at a time.
    /// </summary>
    public static async Task CountAsync(LibrarionDbContext db, int tenantId, ClosureChange change, CancellationToken cancellationToken)
    {
        if (change.IsEmpty)
            return;
        var changed = change.Before.Keys.ToList();
        var affected = new HashSet<int>();
        foreach (var tag in changed)
        {
            affected.UnionWith(change.Before[tag].Except(change.After[tag]));
            affected.UnionWith(change.After[tag].Except(change.Before[tag]));
        }

        var books = new HashSet<int>();
        foreach (var chunk in changed.Chunk(1000))
            books.UnionWith(await db.BookTags.Where(l => chunk.Contains(l.TagId)).Select(l => l.BookId).Distinct().ToListAsync(cancellationToken));

        var ancestors = new TagAncestors();
        foreach (var batch in books.Order().Chunk(1000))
        {
            var links = await db.BookTags.Where(l => batch.Contains(l.BookId) && l.Book!.MissingSince == null)
                .Select(l => new { l.BookId, l.Book!.LibraryId, l.TagId }).ToListAsync(cancellationToken);
            await ancestors.LoadAsync(db, links.Select(l => l.TagId).Where(t => !change.Before.ContainsKey(t)), cancellationToken);
            var deltas = new Dictionary<(int Library, int Tag), (int Direct, int Total)>();
            var was = new HashSet<int>();
            var now = new HashSet<int>();
            foreach (var book in links.GroupBy(l => (l.BookId, l.LibraryId)))
            {
                was.Clear();
                now.Clear();
                foreach (var link in book)
                {
                    if (change.Before.TryGetValue(link.TagId, out var before))
                    {
                        was.UnionWith(before);
                        now.UnionWith(change.After[link.TagId]);
                    }
                    else
                    {
                        was.UnionWith(ancestors.Of(link.TagId));
                        now.UnionWith(ancestors.Of(link.TagId));
                    }
                }

                was.IntersectWith(affected);
                now.IntersectWith(affected);
                foreach (var tag in now.Except(was))
                    deltas[(book.Key.LibraryId, tag)] = (0, deltas.GetValueOrDefault((book.Key.LibraryId, tag)).Total + 1);
                foreach (var tag in was.Except(now))
                    deltas[(book.Key.LibraryId, tag)] = (0, deltas.GetValueOrDefault((book.Key.LibraryId, tag)).Total - 1);
            }

            await TagCountChanges.WriteAsync(db, tenantId, deltas, cancellationToken);
        }
    }

    /// <summary>A tag's own rows for a tag made now: itself, its field, and its parents' ancestors.</summary>
    public static async Task AddAsync(LibrarionDbContext db, int tenantId, int tagId, CancellationToken cancellationToken)
    {
        db.TagClosure.Add(new TagClosure { TenantId = tenantId, AncestorId = tagId, DescendantId = tagId, Depth = 0 });
        await db.SaveChangesAsync(cancellationToken);
        await RebuildAsync(db, tenantId, [tagId], cancellationToken);
    }

    /// <summary>Whether <paramref name="descendant"/> is <paramref name="ancestor"/> or under it.</summary>
    public static Task<bool> IsUnderAsync(LibrarionDbContext db, int descendant, int ancestor, CancellationToken cancellationToken) =>
        db.TagClosure.AnyAsync(c => c.AncestorId == ancestor && c.DescendantId == descendant, cancellationToken);
}
