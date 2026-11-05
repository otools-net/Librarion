using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Tagging;

/// <summary>
/// The tags each tag counts toward in <see cref="TagCount.TotalCount"/>: itself and its ancestors (a field over its
/// values), read from <see cref="TagClosure"/> for the tags a batch names and kept between batches (most books of an
/// import share their authors, genres and fields), cleared past a bound. One per import or tag change: the closure it
/// read must not change while it is used (tag runs and scans never overlap, agents/docs/features/tasks.md#runs).
/// </summary>
public sealed class TagAncestors
{
    private const int MaxCached = 200_000;
    private readonly Dictionary<int, int[]> cache = [];
    private long version = TagClosures.Version;

    public async Task LoadAsync(LibrarionDbContext db, IEnumerable<int> tags, CancellationToken cancellationToken)
    {
        // Parents changed since these were read: read again.
        if (version != TagClosures.Version)
        {
            cache.Clear();
            version = TagClosures.Version;
        }

        var missing = tags.Where(t => !cache.ContainsKey(t)).Distinct().ToList();
        if (missing.Count == 0)
            return;
        if (cache.Count + missing.Count > MaxCached)
            cache.Clear();
        foreach (var chunk in missing.Chunk(1000))
        {
            var rows = await db.TagClosure.Where(c => chunk.Contains(c.DescendantId)).Select(c => new { c.DescendantId, c.AncestorId }).ToListAsync(cancellationToken);
            var byTag = rows.ToLookup(r => r.DescendantId, r => r.AncestorId);
            foreach (var tag in chunk)
                cache[tag] = byTag[tag].Append(tag).Distinct().ToArray();
        }
    }

    /// <summary>The tag and its ancestors; the tag alone when it wasn't loaded.</summary>
    public IReadOnlyList<int> Of(int tag) => cache.TryGetValue(tag, out var ancestors) ? ancestors : [tag];
}

/// <summary>
/// Books' tags before and after a change (a book added, re-read, gone missing, come back, removed; links moved by a
/// merge or a delete), per library, written as count deltas: <see cref="TagCount.BookCount"/> by the tags themselves,
/// <see cref="TagCount.TotalCount"/> by the tags they are under, each book once per tag (a book under two children of
/// "Fiction" moves Fiction by one). Never a count over the catalog: the work is proportional to the books changed.
/// </summary>
public sealed class TagCountChanges
{
    private readonly List<(int Library, int[] Before, int[] After)> changes = [];

    public bool IsEmpty => changes.Count == 0;

    /// <summary>A book of <paramref name="library"/> had the tags <paramref name="before"/> and has <paramref name="after"/> (empty: not counted, as a missing book).</summary>
    public void Add(int library, IEnumerable<int> before, IEnumerable<int> after)
    {
        var was = before.Distinct().ToArray();
        var now = after.Distinct().ToArray();
        if (was.Length > 0 || now.Length > 0)
            changes.Add((library, was, now));
    }

    /// <summary>The deltas of what was added, written (one statement per library and thousand tags), and the rows left at zero removed.</summary>
    public async Task WriteAsync(LibrarionDbContext db, int tenantId, TagAncestors ancestors, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
            return;
        await ancestors.LoadAsync(db, changes.SelectMany(c => c.Before.Concat(c.After)), cancellationToken);
        var deltas = new Dictionary<(int Library, int Tag), (int Direct, int Total)>();
        void Move(int library, int tag, int direct, int total)
        {
            var (d, t) = deltas.GetValueOrDefault((library, tag));
            deltas[(library, tag)] = (d + direct, t + total);
        }

        var under = new HashSet<int>();
        var was = new HashSet<int>();
        foreach (var (library, before, after) in changes)
        {
            foreach (var tag in after.Except(before))
                Move(library, tag, +1, 0);
            foreach (var tag in before.Except(after))
                Move(library, tag, -1, 0);
            was.Clear();
            under.Clear();
            foreach (var tag in before)
                was.UnionWith(ancestors.Of(tag));
            foreach (var tag in after)
                under.UnionWith(ancestors.Of(tag));
            foreach (var tag in under)
                if (!was.Contains(tag))
                    Move(library, tag, 0, +1);
            foreach (var tag in was)
                if (!under.Contains(tag))
                    Move(library, tag, 0, -1);
        }

        changes.Clear();
        await WriteAsync(db, tenantId, deltas, cancellationToken);
    }

    /// <summary>Deltas by library and tag (zeros skipped), written; the rows left at zero in both counts removed.</summary>
    internal static async Task WriteAsync(LibrarionDbContext db, int tenantId, Dictionary<(int Library, int Tag), (int Direct, int Total)> deltas, CancellationToken cancellationToken)
    {
        foreach (var library in deltas.Where(d => d.Value != (0, 0)).GroupBy(d => d.Key.Library))
        {
            foreach (var chunk in library.Chunk(1000))
            {
                // One upsert per chunk (a row per tag through EF was most of a big import's time: thousands of authors per
                // batch), then the rows left at zero among these tags removed.
                await BulkRows.AddCountsAsync(db, tenantId, library.Key, chunk.Select(d => new BulkRows.CountDelta(d.Key.Tag, d.Value.Direct, d.Value.Total)).ToList(),
                    cancellationToken);
                var ids = chunk.Select(d => d.Key.Tag).ToList();
                var id = library.Key;
                await db.TagCounts.Where(c => c.LibraryId == id && ids.Contains(c.TagId) && c.BookCount <= 0 && c.TotalCount <= 0).ExecuteDeleteAsync(cancellationToken);
            }
        }
    }
}
