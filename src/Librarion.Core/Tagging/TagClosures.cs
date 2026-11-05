using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Tagging;

/// <summary>
/// Keeps <see cref="TagClosure"/> right when parents change (agents/docs/features/tags.md#scale), set-based on both
/// providers, never a walk per tag: after the parent edges of <c>roots</c> changed, the ancestors of everything under
/// them (D, the roots included) are rebuilt. Pairs inside D stay (their paths never leave D, whose own edges didn't
/// change); pairs from outside D are dropped, then made again from each edge leaving D (a parent, or a value's
/// field) times that parent's ancestors. Correct for several roots at once as long as every edge that changed points
/// out of D, which holds for re-parenting (no cycles), deleting a tag (its children are the roots) and merging (the
/// source's children, the target not under them).
/// </summary>
public static class TagClosures
{
    public static async Task RebuildAsync(LibrarionDbContext db, int tenantId, IReadOnlyCollection<int> roots, CancellationToken cancellationToken)
    {
        foreach (var chunk in roots.Distinct().Chunk(500))
        {
            var ids = chunk.ToArray();
            var under = db.TagClosure.Where(c => ids.Contains(c.AncestorId)).Select(c => c.DescendantId);
            // Rows from an ancestor in D to a root (a root's own row among them) are never deleted here, so `under` reads
            // the same before and after.
            await db.TagClosure.Where(c => under.Contains(c.DescendantId) && !under.Contains(c.AncestorId)).ExecuteDeleteAsync(cancellationToken);

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
