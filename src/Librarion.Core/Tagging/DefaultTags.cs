using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Tagging;

/// <param name="FolderId">Where it is set: a folder, or null for the library.</param>
/// <param name="From">That folder's name (null for the library).</param>
public sealed record DefaultTagItem(int TagId, string Name, string? Color, int? FolderId, string? From);

/// <summary>A library's or a folder's default tags: its own, then those it inherits (the library's, the folders' above it).</summary>
public sealed record DefaultTagList(IReadOnlyList<DefaultTagItem> Own, IReadOnlyList<DefaultTagItem> Inherited);

/// <summary>
/// Default tags of a library and of its folders (stage 6 chunk 8c; SFE's folder default tags,
/// agents/docs/features/default-tags.md): plain tags given as <see cref="TagSource.Rule"/> links to the books that come
/// in (<see cref="DefaultTagRules"/>, at import) and, when asked, to the books already there; taking one away asks
/// whether those books keep it. The books under a folder are walked through the folder index a batch at a time; past
/// <see cref="TagService.InlineLinks"/> a background run of the library.
/// </summary>
public sealed class DefaultTags(TenantDb tenantDb, TagService tags, BookTagger tagger, BackgroundTasks tasks, TimeProvider time)
{
    /// <summary>The most default tags one library or folder has.</summary>
    public const int MaxPerPlace = 20;

    public async Task<AccessResult<DefaultTagList>> ListAsync(int tenantId, int libraryId, int? folderId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (await ChainAsync(db, libraryId, folderId, cancellationToken) is not { } chain)
            return AccessResult<DefaultTagList>.Missing();
        var places = chain.Select(c => c.Id).ToList();
        var rows = await db.DefaultTags.Where(d => d.LibraryId == libraryId && (d.FolderId == null || places.Contains(d.FolderId!.Value)))
            .Join(db.Tags, d => d.TagId, t => t.Id, (d, t) => new { d.FolderId, t.Id, t.Name, t.Color }).ToListAsync(cancellationToken);
        var names = chain.ToDictionary(c => c.Id, c => c.Name);
        DefaultTagItem Item(int? folder, int id, string name, string? color) => new(id, name, color, folder, folder is { } f ? names[f] : null);
        var own = rows.Where(r => r.FolderId == folderId).Select(r => Item(r.FolderId, r.Id, r.Name, r.Color))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        // Nearest first: the folder's parent, …, the library.
        var order = chain.Select((c, i) => (c.Id, i)).ToDictionary(c => c.Id, c => c.i);
        var inherited = rows.Where(r => r.FolderId != folderId).OrderBy(r => r.FolderId is { } f ? order[f] : int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => Item(r.FolderId, r.Id, r.Name, r.Color)).ToList();
        return AccessResult<DefaultTagList>.Ok(new DefaultTagList(own, inherited));
    }

    /// <summary>
    /// Makes <paramref name="tag"/> a default of the library or folder; with <paramref name="existing"/>, the books there
    /// (and under it) get it too: inline, or a background run past the threshold.
    /// </summary>
    public async Task<AccessResult<TagWork>> AddAsync(int tenantId, int libraryId, int? folderId, BookTagEdit tag, bool existing, int? userId,
        CancellationToken cancellationToken = default)
    {
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            if (await ChainAsync(db, libraryId, folderId, cancellationToken) is null)
                return AccessResult<TagWork>.Missing();
            if (await db.DefaultTags.CountAsync(d => d.LibraryId == libraryId && d.FolderId == folderId, cancellationToken) >= MaxPerPlace)
                return AccessResult<TagWork>.Invalid("tag", "defaultTags.tooMany");
        }

        var resolved = await tagger.PlainTagAsync(tenantId, tag, cancellationToken);
        if (resolved.Status != AccessStatus.Ok)
            return resolved.As<TagWork>();
        var tagId = resolved.Value;
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            if (!await db.DefaultTags.AnyAsync(d => d.LibraryId == libraryId && d.FolderId == folderId && d.TagId == tagId, cancellationToken))
            {
                db.DefaultTags.Add(new DefaultTag { TenantId = tenantId, LibraryId = libraryId, FolderId = folderId, TagId = tagId, Created = time.GetUtcNow().UtcDateTime });
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return existing ? await WalkOrRunAsync(tenantId, libraryId, folderId, tagId, give: true, userId, cancellationToken) : AccessResult<TagWork>.Ok(new TagWork(null, null, 0));
    }

    /// <summary>
    /// The tag is no longer a default there; with <paramref name="fromBooks"/>, the books there and under it lose its
    /// rule links (not those given by hand or by an import), unless the library or a folder above still gives it.
    /// </summary>
    public async Task<AccessResult<TagWork>> RemoveAsync(int tenantId, int libraryId, int? folderId, int tagId, bool fromBooks, int? userId,
        CancellationToken cancellationToken = default)
    {
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            if (await ChainAsync(db, libraryId, folderId, cancellationToken) is not { } chain)
                return AccessResult<TagWork>.Missing();
            if (await db.DefaultTags.Where(d => d.LibraryId == libraryId && d.FolderId == folderId && d.TagId == tagId).ExecuteDeleteAsync(cancellationToken) == 0)
                return AccessResult<TagWork>.Missing();
            var above = chain.Skip(1).Select(c => c.Id).ToList();
            var stillGiven = folderId is not null && await db.DefaultTags.AnyAsync(d => d.LibraryId == libraryId && d.TagId == tagId
                && (d.FolderId == null || above.Contains(d.FolderId!.Value)), cancellationToken);
            if (!fromBooks || stillGiven)
                return AccessResult<TagWork>.Ok(new TagWork(null, null, 0));
        }

        return await WalkOrRunAsync(tenantId, libraryId, folderId, tagId, give: false, userId, cancellationToken);
    }

    private async Task<AccessResult<TagWork>> WalkOrRunAsync(int tenantId, int libraryId, int? folderId, int tagId, bool give, int? userId,
        CancellationToken cancellationToken)
    {
        long books;
        string name;
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            books = folderId is { } folder
                ? await db.Folders.Where(f => f.Id == folder).Select(f => (long)f.TotalFileCount).FirstAsync(cancellationToken)
                : await db.Libraries.Where(l => l.Id == libraryId).Select(l => (long)l.BookCount).FirstAsync(cancellationToken);
            name = await db.Tags.Where(t => t.Id == tagId).Select(t => t.Name).FirstAsync(cancellationToken);
        }

        if (tags.InBackground(tenantId, books))
        {
            var run = await tasks.StartLibraryAsync(tenantId, libraryId, BackgroundTaskKind.ScopeTags, userId,
                async (progress, cancel) => new ScanResult(0, await WalkAsync(tenantId, libraryId, folderId, tagId, give, name, progress, cancel), 0, 0, 0, 0, 0, [], []),
                cancellationToken);
            return AccessResult<TagWork>.Ok(new TagWork(run, null, books));
        }

        return AccessResult<TagWork>.Ok(new TagWork(null, null, await WalkAsync(tenantId, libraryId, folderId, tagId, give, name, null, cancellationToken)));
    }

    /// <summary>
    /// The books of the library, or of the folder and every folder under it (a level of folders at a time, their files'
    /// books by the index <c>(FolderId, BookId)</c>), a batch at a time: the rule link given or taken off.
    /// </summary>
    private async Task<int> WalkAsync(int tenantId, int libraryId, int? folderId, int tagId, bool give, string name, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var changed = 0;
        async Task BatchAsync(List<int> books)
        {
            changed += await ApplyAsync(db, tenantId, books, tagId, give, cancellationToken);
            progress?.Report(new ScanProgress(0, 0, 0, changed, 0, name));
        }

        if (folderId is null)
        {
            for (var after = 0; ;)
            {
                var last = after;
                var books = await db.Books.Where(b => b.LibraryId == libraryId && b.Id > last).OrderBy(b => b.Id).Select(b => b.Id).Take(TagService.BatchSize)
                    .ToListAsync(cancellationToken);
                if (books.Count == 0)
                    return changed;
                await BatchAsync(books);
                after = books[^1];
            }
        }

        var level = new List<int> { folderId.Value };
        while (level.Count > 0)
        {
            var next = new List<int>();
            foreach (var folders in level.Chunk(TagService.BatchSize))
            {
                for (var after = 0; ;)
                {
                    var last = after;
                    var books = await db.BookFiles.Where(f => folders.Contains(f.FolderId) && f.BookId > last).Select(f => f.BookId).Distinct().OrderBy(id => id)
                        .Take(TagService.BatchSize).ToListAsync(cancellationToken);
                    if (books.Count == 0)
                        break;
                    await BatchAsync(books);
                    after = books[^1];
                }

                next.AddRange(await db.Folders.Where(f => f.ParentId != null && folders.Contains(f.ParentId.Value)).Select(f => f.Id).ToListAsync(cancellationToken));
            }

            level = next;
        }

        return changed;
    }

    /// <summary>
    /// One batch: the rule link added where the book has the tag from no source and didn't have it taken off, or the rule
    /// links taken off; counts moved.
    /// </summary>
    private static async Task<int> ApplyAsync(LibrarionDbContext db, int tenantId, IReadOnlyCollection<int> books, int tagId, bool give, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var before = await TagLinks.SnapshotAsync(db, books, cancellationToken);
        int changed;
        if (give)
        {
            var have = await db.BookTags.Where(l => books.Contains(l.BookId) && l.TagId == tagId).Select(l => l.BookId)
                .Union(db.BookTagSuppressions.Where(s => books.Contains(s.BookId) && s.TagId == tagId).Select(s => s.BookId)).ToListAsync(cancellationToken);
            var fresh = books.Except(have).ToList();
            db.BookTags.AddRange(fresh.Select(b => new BookTag { TenantId = tenantId, BookId = b, TagId = tagId, Source = TagSource.Rule }));
            await db.SaveChangesAsync(cancellationToken);
            changed = fresh.Count;
        }
        else
        {
            changed = await db.BookTags.Where(l => books.Contains(l.BookId) && l.TagId == tagId && l.Source == TagSource.Rule).ExecuteDeleteAsync(cancellationToken);
        }

        if (changed > 0)
        {
            await TagLinks.CountAsync(db, tenantId, before, await TagLinks.SnapshotAsync(db, books, cancellationToken), cancellationToken);
            await TagLinks.UntaggedAsync(db, books, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return changed;
    }

    /// <summary>The folder and every folder above it, nearest first, with their names (a root's: its folder on disk); empty for the library; null when the folder isn't the library's.</summary>
    private static async Task<List<(int Id, string Name)>?> ChainAsync(LibrarionDbContext db, int libraryId, int? folderId, CancellationToken cancellationToken)
    {
        if (!await db.Libraries.AnyAsync(l => l.Id == libraryId, cancellationToken))
            return null;
        var chain = new List<(int Id, string Name)>();
        for (var at = folderId; at is { } id && chain.Count < 256;)
        {
            var row = await db.Folders.Where(f => f.Id == id && f.LibraryId == libraryId).Select(f => new { f.Name, f.ParentId, RootPath = f.Root!.Path }).FirstOrDefaultAsync(cancellationToken);
            if (row is null)
                return null;
            chain.Add((id, row.ParentId is null ? Path.GetFileName(row.RootPath.TrimEnd('/', '\\')) : row.Name));
            at = row.ParentId;
        }

        return chain;
    }
}
