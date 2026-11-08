using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Merging;

/// <param name="Scheme">The identifier's scheme, for an <see cref="DuplicateReason.Identifier"/> group.</param>
/// <param name="Books">The first <see cref="DuplicateQueries.ShownBooks"/> of its books, as list items.</param>
/// <param name="BookIds">All its books the caller sees (compared, merged or set apart together).</param>
public sealed record DuplicateGroupItem(long Id, DuplicateReason Level, string? Scheme, IReadOnlyList<BookListItem> Books, IReadOnlyList<int> BookIds);

/// <param name="Built">The tenant's groups were built (else the end of the next scan or import, or Rebuild, makes them).</param>
public sealed record DuplicatePage(IReadOnlyList<DuplicateGroupItem> Items, string? Next, bool Built);

/// <summary>
/// The Duplicates page and Not a duplicate (stage 6 chunk 12, agents/docs/features/merging.md#the-duplicates-page):
/// stored groups of one library, the biggest first, a page at a time by key on <c>DuplicateGroups (TenantId, BookCount,
/// Id)</c>; a group shows when at least two of its books are the caller's to see in that library. The walk
/// reads at most <see cref="MaxChunks"/> × <see cref="ChunkGroups"/> groups a request and hands back where it stopped.
/// Only the first <see cref="ShownBooks"/> books of a group are read as list items: the biggest groups come first, and
/// a page of them whole was a thousand books' authors and formats (132 ms at 2M on PostgreSQL, 1 s on SQLite).
/// </summary>
public sealed class DuplicateQueries(TenantDb tenantDb, TimeProvider time)
{
    public const int ChunkGroups = 50;
    public const int MaxChunks = 20;
    public const int ShownBooks = 6;

    public async Task<DuplicatePage> ListAsync(int tenantId, BookVisibility visibility, int libraryId, DuplicateReason? level, string? after, int limit,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var built = await DuplicateIndex.BuiltAsync(db, cancellationToken);
        // The cursor: the last group read, by its size (Id) and its id (Number).
        var cursor = KeysetCursor.Decode(after) is { Number: { } groupId } c ? (Count: c.Id, Id: groupId) : ((int Count, long Id)?)null;
        var visible = await BookQueries.VisibleAsync(db, visibility, libraryId, cancellationToken);
        var found = new List<(long Id, DuplicateReason Level, string Key, List<int> Books)>();
        var ended = false;
        for (var chunk = 0; chunk < MaxChunks && found.Count < limit; chunk++)
        {
            // Noise has no members (BookCount 0): a range on the index, where NOT Noise made SQLite sort every group.
            var query = db.DuplicateGroups.Where(g => g.BookCount >= 2);
            if (level is { } only)
                query = query.Where(g => g.Level == only);
            if (cursor is { } last)
                query = query.Where(g => g.BookCount <= last.Count && (g.BookCount < last.Count || g.Id < last.Id));
            var groups = await query.OrderByDescending(g => g.BookCount).ThenByDescending(g => g.Id).Take(ChunkGroups)
                .Select(g => new { g.Id, g.Level, g.Key, g.BookCount }).ToListAsync(cancellationToken);
            if (groups.Count == 0)
            {
                ended = true;
                break;
            }

            var ids = groups.Select(g => g.Id).ToList();
            var members = await db.DuplicateMembers.Where(m => ids.Contains(m.GroupId)).Select(m => new { m.GroupId, m.BookId }).ToListAsync(cancellationToken);
            var books = members.Select(m => m.BookId).Distinct().ToList();
            var seen = new HashSet<int>();
            foreach (var part in books.Chunk(1000))
                seen.UnionWith(await visible.Where(b => part.Contains(b.Id)).Select(b => b.Id).ToListAsync(cancellationToken));
            var byGroup = members.ToLookup(m => m.GroupId, m => m.BookId);
            foreach (var group in groups)
            {
                cursor = (group.BookCount, group.Id);
                var shown = byGroup[group.Id].Where(seen.Contains).ToList();
                if (shown.Count >= 2)
                    found.Add((group.Id, group.Level, group.Key, shown));
                if (found.Count == limit)
                    break;
            }

            if (groups.Count < ChunkGroups && found.Count < limit)
                ended = true;
            if (ended)
                break;
        }

        foreach (var group in found)
            group.Books.Sort();
        var items = (await BookQueries.ItemsOfAsync(db, found.SelectMany(f => f.Books.Take(ShownBooks)).Distinct().ToList(), cancellationToken)).ToDictionary(i => i.Id);
        var page = found.Select(f => new DuplicateGroupItem(f.Id, f.Level, f.Level == DuplicateReason.Identifier ? f.Key[..f.Key.IndexOf(':')] : null,
            f.Books.Take(ShownBooks).Where(items.ContainsKey).Select(id => items[id]).ToList(), f.Books)).ToList();
        var next = ended || cursor is null ? null : new KeysetCursor(null, cursor.Value.Id, cursor.Value.Count).Encode();
        return new DuplicatePage(page, next, built);
    }

    /// <summary>Whether the caller may see every one of the books (libraries and content filters).</summary>
    public async Task<bool> AllVisibleAsync(int tenantId, BookVisibility visibility, IReadOnlyCollection<int> bookIds, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var visible = await BookQueries.VisibleAsync(db, visibility, null, cancellationToken);
        var ids = bookIds.Distinct().ToList();
        return await visible.CountAsync(b => ids.Contains(b.Id), cancellationToken) == ids.Count;
    }

    /// <summary>
    /// The books said not to be duplicates of each other (every pair among them marked), their groups computed again.
    /// The caller checked they exist and may be changed.
    /// </summary>
    public async Task MarkAsync(int tenantId, IReadOnlyCollection<int> bookIds, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var ids = bookIds.Distinct().Order().ToList();
        var pairs = ids.SelectMany((a, i) => ids.Skip(i + 1).Select(b => (a, b))).ToList();
        var marked = (await db.NotDuplicates.Where(n => ids.Contains(n.BookId) && ids.Contains(n.OtherId)).Select(n => new { n.BookId, n.OtherId }).ToListAsync(cancellationToken))
            .Select(n => (n.BookId, n.OtherId)).ToHashSet();
        var now = time.GetUtcNow().UtcDateTime;
        db.NotDuplicates.AddRange(pairs.Where(p => !marked.Contains(p)).Select(p => new NotDuplicate { TenantId = tenantId, BookId = p.a, OtherId = p.b, Created = now }));
        await db.SaveChangesAsync(cancellationToken);
        if (await DuplicateIndex.BuiltAsync(db, cancellationToken))
            await DuplicateIndex.RefreshAsync(db, ids, cancellationToken);
    }
}
