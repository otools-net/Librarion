using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Tagging;

/// <summary>A plain tag among the selected books: <paramref name="Books"/> how many of them have it (all, some).</summary>
public sealed record SelectionTag(int Id, string Name, string? Color, int Books);

/// <summary>The selection's plain tags: <paramref name="Books"/> the books of it the caller may tag.</summary>
public sealed record SelectionTags(int Books, IReadOnlyList<SelectionTag> Tags);

/// <summary>Plain tags to give (by id, or by name: found, else made) and to take away.</summary>
public sealed record TagChange(IReadOnlyList<BookTagEdit>? Add = null, IReadOnlyList<int>? Remove = null);

/// <summary>Every book of a library's list: all of them, those with <paramref name="Tag"/> (a tag's page), those matching <paramref name="Query"/>.</summary>
public sealed record BookScope(int Library, int? Tag = null, string? Query = null, int? Folder = null);

/// <summary>
/// Plain tags given to or taken from many books (stage 6, chunk 7; SFE's selection and scope tagging,
/// agents/docs/features/tags.md): a selection (ids, at most <see cref="MaxSelection"/>) with how many of it have each
/// tag (the dialog's all / some / none), or a scope (a library's list: a search, a tag's page, the library) counted
/// first and then walked by id a batch at a time, never listed; past <see cref="TagService.InlineLinks"/> changes a
/// background run. As on a book's page: given tags are <see cref="TagSource.Manual"/> links, a tag taken off is
/// suppressed against re-imports, <see cref="TagSource.Rule"/> links stay; counts move by delta.
/// </summary>
public sealed class BookTagger(TenantDb tenantDb, TagService tags, BackgroundTasks tasks)
{
    public const int MaxSelection = 1000;
    public const int MaxChanges = 20;

    public async Task<SelectionTags> StateAsync(int tenantId, BookVisibility visibility, IReadOnlySet<int> taggable, IReadOnlyCollection<int> bookIds,
        CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var ids = await AllowedAsync(db, visibility, taggable, bookIds, cancellationToken);
        var counts = await db.BookTags.Where(l => ids.Contains(l.BookId) && l.Tag!.FieldId == null && l.Tag.FieldKey == null)
            .Select(l => new { l.BookId, l.TagId }).Distinct().GroupBy(l => l.TagId).Select(g => new { g.Key, Books = g.Count() })
            .ToListAsync(cancellationToken);
        var tagIds = counts.Select(c => c.Key).ToList();
        var rows = await db.Tags.Where(t => tagIds.Contains(t.Id)).Select(t => new { t.Id, t.Name, t.Color }).ToDictionaryAsync(t => t.Id, cancellationToken);
        return new SelectionTags(ids.Count, counts.Where(c => rows.ContainsKey(c.Key))
            .Select(c => new SelectionTag(c.Key, rows[c.Key].Name, rows[c.Key].Color, c.Books))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    /// <summary>The selected books the caller may tag get the change; Missing when none is.</summary>
    public async Task<AccessResult<TagWork>> TagSelectionAsync(int tenantId, BookVisibility visibility, IReadOnlySet<int> taggable, IReadOnlyCollection<int> bookIds,
        TagChange change, CancellationToken cancellationToken = default)
    {
        if (bookIds.Count > MaxSelection)
            return AccessResult<TagWork>.Invalid("books", "books.tooMany");
        if (await ResolveAsync(tenantId, change, cancellationToken) is not { } resolved)
            return AccessResult<TagWork>.Invalid("add", "name.required");
        if (resolved.Problem is { } problem)
            return problem;
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var ids = await AllowedAsync(db, visibility, taggable, bookIds, cancellationToken);
        if (ids.Count == 0)
            return AccessResult<TagWork>.Missing();
        var changed = 0;
        foreach (var chunk in ids.Order().Chunk(TagService.BatchSize))
            changed += await ApplyAsync(db, tenantId, chunk, resolved.Add, resolved.Remove, cancellationToken);
        return AccessResult<TagWork>.Ok(new TagWork(null, null, changed));
    }

    /// <summary>How many books the scope holds for the caller (what the dialog asks to confirm).</summary>
    public async Task<long> CountAsync(int tenantId, BookVisibility visibility, BookScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        return await (await BookQueries.ScopeAsync(db, visibility, scope.Library, scope.Tag, scope.Query, TagService.BatchSize, cancellationToken, scope.Folder))
            .LongCountAsync(cancellationToken);
    }

    /// <summary>Every book of the scope gets the change: inline, or a run of the library past the threshold (or behind another run).</summary>
    public async Task<AccessResult<TagWork>> TagScopeAsync(int tenantId, BookVisibility visibility, BookScope scope, TagChange change, int? userId,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveAsync(tenantId, change, cancellationToken) is not { } resolved)
            return AccessResult<TagWork>.Invalid("add", "name.required");
        if (resolved.Problem is { } problem)
            return problem;
        if (resolved.Add.Count + resolved.Remove.Count == 0)
            return AccessResult<TagWork>.Ok(new TagWork(null, null, 0));
        var books = await CountAsync(tenantId, visibility, scope, cancellationToken);
        var links = books * (resolved.Add.Count + resolved.Remove.Count);
        if (tags.InBackground(tenantId, links))
        {
            var run = await tasks.StartLibraryAsync(tenantId, scope.Library, BackgroundTaskKind.ScopeTags, userId,
                async (progress, cancel) => new ScanResult(0, await WalkAsync(tenantId, visibility, scope, resolved, progress, cancel), 0, 0, 0, 0, 0, [], []),
                cancellationToken);
            return AccessResult<TagWork>.Ok(new TagWork(run, null, links));
        }

        return AccessResult<TagWork>.Ok(new TagWork(null, null, await WalkAsync(tenantId, visibility, scope, resolved, null, cancellationToken)));
    }

    /// <summary>One plain tag by id, or by name (found, else made): what a default tag is given as.</summary>
    public async Task<AccessResult<int>> PlainTagAsync(int tenantId, BookTagEdit tag, CancellationToken cancellationToken = default)
    {
        if (await ResolveAsync(tenantId, new TagChange([tag]), cancellationToken) is not { } resolved)
            return AccessResult<int>.Invalid("tag", "name.required");
        if (resolved.Problem is { } problem)
            return problem.As<int>();
        return AccessResult<int>.Ok(resolved.Add.Single());
    }

    private sealed record Resolved(List<int> Add, List<int> Remove, string Name, AccessResult<TagWork>? Problem = null);

    /// <summary>The tags to give, by id or name (a missing name made as a plain tag); null when a name is empty.</summary>
    private async Task<Resolved?> ResolveAsync(int tenantId, TagChange change, CancellationToken cancellationToken)
    {
        var add = new List<int>();
        var remove = (change.Remove ?? []).Distinct().ToList();
        if ((change.Add?.Count ?? 0) + remove.Count > MaxChanges)
            return new Resolved([], [], "", AccessResult<TagWork>.Invalid("add", "tags.tooMany"));
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (await db.Tags.CountAsync(t => remove.Contains(t.Id) && t.FieldId == null && t.FieldKey == null, cancellationToken) != remove.Count)
            return new Resolved([], [], "", AccessResult<TagWork>.Invalid("remove", "tag.unknown"));
        foreach (var tag in change.Add ?? [])
        {
            if (tag.Id is { } id)
            {
                if (!await db.Tags.AnyAsync(t => t.Id == id && t.FieldId == null && t.FieldKey == null, cancellationToken))
                    return new Resolved([], [], "", AccessResult<TagWork>.Invalid("add", "tag.unknown"));
                add.Add(id);
                continue;
            }

            var name = tag.Name?.Trim() ?? "";
            if (name.Length == 0)
                return null;
            var normalized = TagNames.Normalize(name);
            var found = await db.Tags.Where(t => t.FieldId == null && t.FieldKey == null && t.NormalizedName == normalized).OrderBy(t => t.Id)
                .Select(t => (int?)t.Id).FirstOrDefaultAsync(cancellationToken);
            if (found is null)
            {
                var made = await tags.CreateAsync(tenantId, new TagInput(name), cancellationToken);
                if (made.Status != AccessStatus.Ok)
                    return new Resolved([], [], "", made.As<TagWork>());
                found = made.Value!.Id;
            }

            add.Add(found.Value);
        }

        add = add.Distinct().Where(id => !remove.Contains(id)).ToList();
        var first = add.Concat(remove).FirstOrDefault();
        var label = first == 0 ? "" : await db.Tags.Where(t => t.Id == first).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken) ?? "";
        return new Resolved(add, remove, label);
    }

    /// <summary>The books of <paramref name="bookIds"/> the caller sees and may tag (its libraries with the right).</summary>
    private static async Task<List<int>> AllowedAsync(LibrarionDbContext db, BookVisibility visibility, IReadOnlySet<int> taggable, IReadOnlyCollection<int> bookIds,
        CancellationToken cancellationToken)
    {
        var ids = bookIds.Distinct().Take(MaxSelection).ToList();
        var libraries = taggable.ToList();
        return await (await BookQueries.VisibleAsync(db, visibility, null, cancellationToken, ids.Count))
            .Where(b => ids.Contains(b.Id) && libraries.Contains(b.LibraryId)).Select(b => b.Id).ToListAsync(cancellationToken);
    }

    /// <summary>The scope by id, a batch at a time, each asked again (a change of the tag the scope is about leaves the rest in it).</summary>
    private async Task<int> WalkAsync(int tenantId, BookVisibility visibility, BookScope scope, Resolved change, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var changed = 0;
        var after = 0;
        while (true)
        {
            await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
            var last = after;
            var books = await (await BookQueries.ScopeAsync(db, visibility, scope.Library, scope.Tag, scope.Query, TagService.BatchSize, cancellationToken, scope.Folder))
                .Where(b => b.Id > last).OrderBy(b => b.Id).Select(b => b.Id).Take(TagService.BatchSize).ToListAsync(cancellationToken);
            if (books.Count == 0)
                break;
            changed += await ApplyAsync(db, tenantId, books, change.Add, change.Remove, cancellationToken);
            after = books[^1];
            progress?.Report(new ScanProgress(0, 0, 0, changed, 0, change.Name));
        }

        return changed;
    }

    /// <summary>One batch: the links added and taken off, suppressions kept, counts moved; the links changed.</summary>
    private static async Task<int> ApplyAsync(LibrarionDbContext db, int tenantId, IReadOnlyCollection<int> books, List<int> add, List<int> remove,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var before = await TagLinks.SnapshotAsync(db, books, cancellationToken);
        var touched = add.Concat(remove).ToList();
        var existing = await db.BookTags.Where(l => books.Contains(l.BookId) && touched.Contains(l.TagId)).Select(l => new { l.BookId, l.TagId, l.Source })
            .ToListAsync(cancellationToken);

        var changed = remove.Count == 0 ? 0
            : await db.BookTags.Where(l => books.Contains(l.BookId) && remove.Contains(l.TagId) && l.Source != TagSource.Rule).ExecuteDeleteAsync(cancellationToken);
        var off = existing.Where(l => remove.Contains(l.TagId) && l.Source != TagSource.Rule).Select(l => (l.BookId, l.TagId)).Distinct().ToList();
        if (off.Count > 0)
        {
            var offBooks = off.Select(o => o.BookId).Distinct().ToList();
            var known = (await db.BookTagSuppressions.Where(s => offBooks.Contains(s.BookId) && remove.Contains(s.TagId)).Select(s => new { s.BookId, s.TagId })
                .ToListAsync(cancellationToken)).Select(s => (s.BookId, s.TagId)).ToHashSet();
            db.BookTagSuppressions.AddRange(off.Where(o => !known.Contains(o)).Select(o => new BookTagSuppression { TenantId = tenantId, BookId = o.BookId, TagId = o.TagId }));
        }

        if (add.Count > 0)
        {
            var have = existing.Where(l => add.Contains(l.TagId)).Select(l => (l.BookId, l.TagId)).ToHashSet();
            var fresh = books.SelectMany(b => add.Select(t => (BookId: b, TagId: t))).Where(p => !have.Contains(p)).ToList();
            db.BookTags.AddRange(fresh.Select(p => new BookTag { TenantId = tenantId, BookId = p.BookId, TagId = p.TagId, Source = TagSource.Manual }));
            changed += fresh.Count;
            await db.BookTagSuppressions.Where(s => books.Contains(s.BookId) && add.Contains(s.TagId)).ExecuteDeleteAsync(cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await TagLinks.CountAsync(db, tenantId, before, await TagLinks.SnapshotAsync(db, books, cancellationToken), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return changed;
    }
}
