using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Merging;

/// <param name="Books">Its books, direct links in every library.</param>
public sealed record TagDuplicateTag(int Id, string Name, int Books);

/// <param name="Books">Its tags' books when the group was last written (what the list ranks by).</param>
public sealed record TagDuplicateItem(long Id, TagDuplicateLevel Level, int Books, IReadOnlyList<TagDuplicateTag> Tags);

/// <param name="Built">The tenant's groups were built (else the end of the next scan or import, or Find again, makes them).</param>
public sealed record TagDuplicatePage(IReadOnlyList<TagDuplicateItem> Items, string? Next, bool Built);

/// <summary>A tag that may be the same as another one, and why (the tag page's panel).</summary>
public sealed record TagLookAlike(TagDuplicateLevel Level, TagDuplicateTag Tag);

/// <summary>
/// Tags › Possible duplicates and the tag page's look-alikes (stage 6 chunk 14,
/// agents/docs/features/merging.md#possible-duplicate-tags-built-stage-6-chunk-14): a field's stored groups (or the plain
/// tags'), the most books first, a page at a time by key on <c>TagDuplicateGroups (TenantId, Field, BookCount, Id)</c>;
/// a group shows while two of its tags are still there (a merged or deleted tag leaves its rows behind until the group
/// is written again). At most <see cref="MaxChunks"/> × <see cref="ChunkGroups"/> groups read a request.
/// </summary>
public sealed class TagDuplicateQueries(TenantDb tenantDb, TimeProvider time)
{
    public const int ChunkGroups = 50;
    public const int MaxChunks = 20;

    /// <summary>A field's groups (<paramref name="field"/> 0: plain tags).</summary>
    public async Task<TagDuplicatePage> ListAsync(int tenantId, int field, string? after, int limit, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var built = await TagDuplicateIndex.BuiltAsync(db, cancellationToken);
        var cursor = KeysetCursor.Decode(after) is { Number: { } groupId } c ? (Count: c.Id, Id: groupId) : ((int Count, long Id)?)null;
        var found = new List<(long Id, TagDuplicateLevel Level, int Books, List<int> Tags)>();
        var ended = false;
        for (var chunk = 0; chunk < MaxChunks && found.Count < limit; chunk++)
        {
            // Noise has no members: TagCount 0, left out as a range.
            var query = db.TagDuplicateGroups.Where(g => g.Field == field && g.TagCount >= 2);
            if (cursor is { } last)
                query = query.Where(g => g.BookCount <= last.Count && (g.BookCount < last.Count || g.Id < last.Id));
            var groups = await query.OrderByDescending(g => g.BookCount).ThenByDescending(g => g.Id).Take(ChunkGroups)
                .Select(g => new { g.Id, g.Level, g.BookCount }).ToListAsync(cancellationToken);
            if (groups.Count == 0)
            {
                ended = true;
                break;
            }

            var ids = groups.Select(g => g.Id).ToList();
            var members = (await db.TagDuplicateMembers.Where(m => ids.Contains(m.GroupId)).Select(m => new { m.GroupId, m.TagId }).ToListAsync(cancellationToken))
                .ToLookup(m => m.GroupId, m => m.TagId);
            foreach (var group in groups)
            {
                cursor = (group.BookCount, group.Id);
                var tags = members[group.Id].Order().ToList();
                if (tags.Count >= 2)
                    found.Add((group.Id, group.Level, group.BookCount, tags));
                if (found.Count == limit)
                    break;
            }

            if (groups.Count < ChunkGroups && found.Count < limit)
            {
                ended = true;
                break;
            }
        }

        var shown = await TagsAsync(db, found.SelectMany(f => f.Tags).Distinct().ToList(), cancellationToken);
        var page = found.Select(f => new TagDuplicateItem(f.Id, f.Level, f.Books,
                f.Tags.Where(shown.ContainsKey).Select(id => shown[id]).OrderByDescending(t => t.Books).ThenBy(t => t.Id).ToList()))
            .Where(i => i.Tags.Count >= 2).ToList();
        var next = ended || cursor is null ? null : new KeysetCursor(null, cursor.Value.Id, cursor.Value.Count).Encode();
        return new TagDuplicatePage(page, next, built);
    }

    /// <summary>The tags that may be the same as <paramref name="tagId"/>, from the groups it is in (a handful: a group holds fewer than 20).</summary>
    public async Task<IReadOnlyList<TagLookAlike>> OfTagAsync(int tenantId, int tagId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var groups = await db.TagDuplicateMembers.Where(m => m.TagId == tagId)
            .Join(db.TagDuplicateGroups, m => m.GroupId, g => g.Id, (m, g) => new { g.Id, g.Level }).ToListAsync(cancellationToken);
        var ids = groups.Select(g => g.Id).ToList();
        var levels = groups.ToDictionary(g => g.Id, g => g.Level);
        var others = (await db.TagDuplicateMembers.Where(m => ids.Contains(m.GroupId) && m.TagId != tagId).Select(m => new { m.GroupId, m.TagId })
            .ToListAsync(cancellationToken)).Select(m => new { Level = levels[m.GroupId], m.TagId }).ToList();
        var tags = await TagsAsync(db, others.Select(o => o.TagId).Distinct().ToList(), cancellationToken);
        // A group's level is the whole group's: in a group that sounds alike, a name of the very same words says so.
        var words = NameKeys.WordsText(await db.Tags.Where(t => t.Id == tagId).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken) ?? "");
        TagDuplicateLevel Level(TagDuplicateLevel level, int other) =>
            level == TagDuplicateLevel.Sound && NameKeys.WordsText(tags[other].Name) == words ? TagDuplicateLevel.Words : level;
        // The strongest reason of each: the same words, then sounds alike, then initials.
        return others.Where(o => tags.ContainsKey(o.TagId)).GroupBy(o => o.TagId).Select(g => new TagLookAlike(g.Min(o => Level(o.Level, g.Key)), tags[g.Key]))
            .OrderBy(l => l.Level).ThenByDescending(l => l.Tag.Books).ThenBy(l => l.Tag.Id).ToList();
    }

    /// <summary>The tags said not the same as each other (every pair), their groups written again. The caller checked they exist.</summary>
    public async Task MarkAsync(int tenantId, IReadOnlyCollection<int> tagIds, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var ids = tagIds.Distinct().Order().ToList();
        var marked = (await db.NotSameTags.Where(n => ids.Contains(n.TagId) && ids.Contains(n.OtherId)).Select(n => new { n.TagId, n.OtherId }).ToListAsync(cancellationToken))
            .Select(n => (n.TagId, n.OtherId)).ToHashSet();
        var now = time.GetUtcNow().UtcDateTime;
        db.NotSameTags.AddRange(ids.SelectMany((a, i) => ids.Skip(i + 1).Select(b => (a, b))).Where(p => !marked.Contains(p))
            .Select(p => new NotSameTag { TenantId = tenantId, TagId = p.a, OtherId = p.b, Created = now }));
        await db.SaveChangesAsync(cancellationToken);
        await TagDuplicateIndex.RefreshAsync(db, ids, cancellationToken);
    }

    /// <summary>A field's tag id by its key (an alias key leads to its field); 0 for none (plain tags); null when there is no such field.</summary>
    public async Task<int?> FieldAsync(int tenantId, string? key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            return 0;
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        return await db.Tags.Where(t => t.FieldKey == key).Select(t => (int?)t.Id).FirstOrDefaultAsync(cancellationToken)
               ?? await db.FieldKeyAliases.Where(a => a.Key == key).Select(a => (int?)a.FieldId).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>How many of <paramref name="tagIds"/> exist.</summary>
    public async Task<int> CountAsync(int tenantId, IReadOnlyCollection<int> tagIds, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var ids = tagIds.Distinct().ToList();
        return await db.Tags.CountAsync(t => ids.Contains(t.Id), cancellationToken);
    }

    /// <summary>Tags by id with their books (direct links, every library).</summary>
    private static async Task<Dictionary<int, TagDuplicateTag>> TagsAsync(LibrarionDbContext db, List<int> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, TagDuplicateTag>();
        foreach (var part in ids.Chunk(1000))
        {
            var books = (await db.TagCounts.Where(c => part.Contains(c.TagId)).GroupBy(c => c.TagId).Select(g => new { g.Key, Books = g.Sum(c => c.BookCount) })
                .ToListAsync(cancellationToken)).ToDictionary(b => b.Key, b => b.Books);
            foreach (var tag in await db.Tags.Where(t => part.Contains(t.Id)).Select(t => new { t.Id, t.Name }).ToListAsync(cancellationToken))
                result[tag.Id] = new TagDuplicateTag(tag.Id, tag.Name, books.GetValueOrDefault(tag.Id));
        }

        return result;
    }
}
