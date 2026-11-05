using System.Text.RegularExpressions;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Tagging;

/// <summary>A tag as its editor shows it. <paramref name="Books"/>: its books in every library (from the counts), what a delete or merge changes.</summary>
public sealed record TagDefinition(
    int Id, string Name, string SortName, string? Shorthand, string? Color, bool IsCategory, bool IsHidden, int? DisambiguationParentId,
    int? FieldId, string? FieldKey, bool BuiltIn, IReadOnlyList<string> Aliases, IReadOnlyList<TagParentRef> Parents, long Books);

public sealed record TagParentRef(int Id, string Name);

/// <summary>
/// What a tag is to be. <paramref name="FieldId"/>: a new value of that field (create only); <paramref name="FieldKey"/>:
/// a new field (create only). <paramref name="SortName"/> empty = the name (a person's "Last, First" for people's fields).
/// </summary>
public sealed record TagInput(
    string? Name, string? SortName = null, string? Shorthand = null, string? Color = null, bool IsCategory = false, bool IsHidden = false,
    int? DisambiguationParentId = null, IReadOnlyList<string>? Aliases = null, IReadOnlyList<int>? ParentIds = null, int? FieldId = null, string? FieldKey = null);

/// <summary>
/// A change touching books' links: done (<paramref name="RunId"/> null) or running in the background
/// (<paramref name="RunId"/>); <paramref name="LogId"/> the merge's log; <paramref name="Links"/> the links it moved or
/// removed (expected, while it runs).
/// </summary>
public sealed record TagWork(long? RunId, long? LogId, long Links);

/// <summary>
/// The tenant's tag and field definitions (agents/docs/features/tags.md, stage 6): create, edit (names, aliases,
/// parents with the closure kept and cycles refused), delete with the links removed in batches and the counts moved.
/// Merges are <see cref="Merging.TagMerger"/>'s.
/// </summary>
public sealed partial class TagService(TenantDb tenantDb, BackgroundTasks tasks, AccessChanges changes)
{
    /// <summary>Changes touching more links than this run in the background (D-6.4, the user's choice 2026-11-05).</summary>
    public const int InlineLinks = 10_000;

    /// <summary>Links changed per transaction, keyed on <see cref="Book.Id"/>.</summary>
    public const int BatchSize = 1000;

    public const int MaxAliases = 50;
    public const int MaxParents = 20;
    private const int MaxShorthandLength = 50;
    private static readonly HashSet<string> PersonFields = ["author", "translator", "editor", "illustrator"];

    /// <summary><see cref="InlineLinks"/>; smaller in tests.</summary>
    internal long Threshold { get; set; } = InlineLinks;

    /// <summary>Whether a change of <paramref name="links"/> links runs in the background: past the threshold, or behind a run already going in the tenant.</summary>
    public bool InBackground(int tenantId, long links) => links > Threshold || tasks.AnyRunningIn(tenantId);

    public async Task<TagDefinition?> GetAsync(int tenantId, int tagId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        return await DefinitionAsync(db, tagId, cancellationToken);
    }

    public async Task<AccessResult<TagDefinition>> CreateAsync(int tenantId, TagInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var tag = new Tag { TenantId = tenantId };
        if (input.FieldKey is { } key)
        {
            key = key.Trim();
            if (!FieldKeyPattern().IsMatch(key))
                return AccessResult<TagDefinition>.Invalid("fieldKey", "fieldKey.invalid");
            if (await db.Tags.AnyAsync(t => t.FieldKey == key, cancellationToken) || await db.FieldKeyAliases.AnyAsync(a => a.Key == key, cancellationToken))
                return AccessResult<TagDefinition>.Invalid("fieldKey", "fieldKey.taken");
            tag.FieldKey = key;
        }
        else if (input.FieldId is { } fieldId)
        {
            if (!await db.Tags.AnyAsync(t => t.Id == fieldId && t.FieldKey != null, cancellationToken))
                return AccessResult<TagDefinition>.Invalid("fieldId", "field.unknown");
            tag.FieldId = fieldId;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await ApplyAsync(db, tag, input, cancellationToken) is { } problem)
            return problem;
        await db.SaveChangesAsync(cancellationToken);
        await TagClosures.AddAsync(db, tenantId, tag.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        changes.Changed();
        return AccessResult<TagDefinition>.Ok((await DefinitionAsync(db, tag.Id, cancellationToken))!);
    }

    public async Task<AccessResult<TagDefinition>> UpdateAsync(int tenantId, int tagId, TagInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var tag = await db.Tags.Include(t => t.Aliases).FirstOrDefaultAsync(t => t.Id == tagId, cancellationToken);
        if (tag is null)
            return AccessResult<TagDefinition>.Missing();
        var oldSort = tag.SortName;
        var oldParents = await db.TagParents.Where(p => p.ChildId == tagId).Select(p => p.ParentId).OrderBy(p => p).ToListAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await ApplyAsync(db, tag, input, cancellationToken) is { } problem)
            return problem;
        await db.SaveChangesAsync(cancellationToken);
        var newParents = await db.TagParents.Where(p => p.ChildId == tagId).Select(p => p.ParentId).OrderBy(p => p).ToListAsync(cancellationToken);
        ClosureChange? recount = null;
        if (!oldParents.SequenceEqual(newParents))
        {
            // The totals move for every book under it: past the threshold, in the background (the closure itself now).
            var later = InBackground(tenantId, await BooksUnderAsync(db, [tagId], cancellationToken));
            var change = await TagClosures.RebuildAsync(db, tenantId, [tagId], cancellationToken, countNow: !later);
            if (later && !change.IsEmpty)
                recount = change;
        }
        // The books that copy its sort name (it is their first author): one statement, whatever their number.
        if (tag.SortName != oldSort)
        {
            var sort = tag.SortName.Length > Book.MaxSortLength ? tag.SortName[..Book.MaxSortLength] : tag.SortName;
            var linked = db.BookTags.Where(l => l.TagId == tagId).Select(l => l.BookId);
            await db.Books.Where(b => b.SortAuthor == oldSort && linked.Contains(b.Id)).ExecuteUpdateAsync(s => s.SetProperty(b => b.SortAuthor, sort), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        changes.Changed();
        if (recount is not null)
            await tasks.StartTenantAsync(tenantId, BackgroundTaskKind.TagCounts, null, async (progress, cancel) =>
            {
                progress.Report(new ScanProgress(0, 0, 0, 0, 0, tag.Name));
                await using var counting = await tenantDb.ForTenantAsync(tenantId, cancel);
                await TagClosures.CountAsync(counting, tenantId, recount, cancel);
                return new ScanResult(0, 0, 0, 0, 0, 0, 0, [], []);
            }, cancellationToken);
        return AccessResult<TagDefinition>.Ok((await DefinitionAsync(db, tag.Id, cancellationToken))!);
    }

    /// <summary>
    /// Deletes a tag with its links (a field only once it has no values: merge it instead). Refused for built-in tags
    /// and tags a group's content filter names (deleting would change who sees what).
    /// </summary>
    public async Task<AccessResult<TagWork>> DeleteAsync(int tenantId, int tagId, int? userId, CancellationToken cancellationToken = default)
    {
        long links;
        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            var tag = await db.Tags.Where(t => t.Id == tagId).Select(t => new { t.BuiltIn, t.FieldKey }).FirstOrDefaultAsync(cancellationToken);
            if (tag is null)
                return AccessResult<TagWork>.Missing();
            if (tag.BuiltIn is not null)
                return AccessResult<TagWork>.Conflict("tag.builtIn");
            if (tag.FieldKey is not null && await db.Tags.AnyAsync(t => t.FieldId == tagId, cancellationToken))
                return AccessResult<TagWork>.Conflict("field.notEmpty");
            if (await db.GroupTagFilters.AnyAsync(f => f.TagId == tagId, cancellationToken))
                return AccessResult<TagWork>.Conflict("tag.inFilter");
            // Its children leave it: the totals of what it is under move for every book under them too.
            links = Math.Max(await LinksAsync(db, [tagId], cancellationToken), await BooksUnderAsync(db, [tagId], cancellationToken));
        }

        if (InBackground(tenantId, links))
        {
            var run = await tasks.StartTenantAsync(tenantId, BackgroundTaskKind.TagDelete, userId,
                async (progress, cancel) => new ScanResult(0, 0, await RemoveAsync(tenantId, tagId, progress, cancel), 0, 0, 0, 0, [], []), cancellationToken);
            return AccessResult<TagWork>.Ok(new TagWork(run, null, links));
        }

        return AccessResult<TagWork>.Ok(new TagWork(null, null, await RemoveAsync(tenantId, tagId, null, cancellationToken)));
    }

    /// <summary>The links of <paramref name="tagIds"/> in every library: what deciding between inline and a run looks at (the counts, never a count of links).</summary>
    public static async Task<long> LinksAsync(LibrarionDbContext db, IReadOnlyCollection<int> tagIds, CancellationToken cancellationToken) =>
        await db.TagCounts.Where(c => tagIds.Contains(c.TagId)).SumAsync(c => (long)c.BookCount, cancellationToken);

    /// <summary>The books with <paramref name="tagIds"/> or a tag under them in every library: what moving them costs the totals.</summary>
    public static async Task<long> BooksUnderAsync(LibrarionDbContext db, IReadOnlyCollection<int> tagIds, CancellationToken cancellationToken) =>
        await db.TagCounts.Where(c => tagIds.Contains(c.TagId)).SumAsync(c => (long)c.TotalCount, cancellationToken);

    /// <summary>The links removed a batch at a time, then the tag (its children keep their other parents, the closure rebuilt under them).</summary>
    private async Task<int> RemoveAsync(int tenantId, int tagId, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var removed = 0;
        var name = "";
        var after = 0;
        while (true)
        {
            await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
            if (name.Length == 0)
                name = await db.Tags.Where(t => t.Id == tagId).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken) ?? "";
            var last = after;
            var books = await db.BookTags.Where(l => l.TagId == tagId && l.BookId > last).Select(l => l.BookId).Distinct().OrderBy(b => b).Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (books.Count == 0)
                break;
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var before = await TagLinks.SnapshotAsync(db, books, cancellationToken);
            removed += await db.BookTags.Where(l => l.TagId == tagId && books.Contains(l.BookId)).ExecuteDeleteAsync(cancellationToken);
            await TagLinks.CountAsync(db, tenantId, before, await TagLinks.SnapshotAsync(db, books, cancellationToken), cancellationToken);
            await TagLinks.SortColumnsAsync(db, books, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            after = books[^1];
            progress?.Report(new ScanProgress(0, 0, 0, 0, removed, name));
        }

        await using (var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var children = await db.TagParents.Where(p => p.ParentId == tagId).Select(p => p.ChildId).ToListAsync(cancellationToken);
            await db.TagParents.Where(p => p.ParentId == tagId || p.ChildId == tagId).ExecuteDeleteAsync(cancellationToken);
            await db.Tags.Where(t => t.DisambiguationParentId == tagId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DisambiguationParentId, (int?)null), cancellationToken);
            await TagClosures.RebuildAsync(db, tenantId, children, cancellationToken);
            // Its closure rows, counts, aliases and import mappings go with it (cascades).
            await db.Tags.Where(t => t.Id == tagId).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        changes.Changed();
        return removed;
    }

    /// <summary>Checks <paramref name="input"/> and applies it to <paramref name="tag"/> (its aliases and parent rows too); the problem when it is refused.</summary>
    private static async Task<AccessResult<TagDefinition>?> ApplyAsync(LibrarionDbContext db, Tag tag, TagInput input, CancellationToken cancellationToken)
    {
        var isField = tag.FieldKey is not null;
        // Built-in fields keep their names: the client names them in its language.
        var keepsName = isField && tag.BuiltIn is not null;
        var name = keepsName ? tag.Name : input.Name?.Trim() ?? "";
        if (name.Length == 0)
            return AccessResult<TagDefinition>.Invalid("name", "name.required");
        if (name.Length > Tag.MaxNameLength)
            return AccessResult<TagDefinition>.Invalid("name", "name.tooLong");
        var normalized = TagNames.Normalize(name);
        var shorthand = input.Shorthand?.Trim() is { Length: > 0 } s ? s : null;
        if (shorthand is { Length: > MaxShorthandLength })
            return AccessResult<TagDefinition>.Invalid("shorthand", "shorthand.tooLong");
        var color = input.Color?.Trim() is { Length: > 0 } c ? c.ToLowerInvariant() : null;
        if (color is not null && !ColorPattern().IsMatch(color))
            return AccessResult<TagDefinition>.Invalid("color", "color.invalid");
        var sortName = input.SortName?.Trim() is { Length: > 0 } sort ? sort : null;
        if (sortName is { Length: > Tag.MaxNameLength })
            return AccessResult<TagDefinition>.Invalid("sortName", "name.tooLong");

        var parentIds = isField ? [] : (input.ParentIds ?? []).Distinct().ToList();
        if (parentIds.Count > MaxParents)
            return AccessResult<TagDefinition>.Invalid("parentIds", "parent.tooMany");
        if (parentIds.Count > 0)
        {
            var parents = await db.Tags.Where(t => parentIds.Contains(t.Id)).Select(t => new { t.Id, t.FieldKey, t.FieldId }).ToListAsync(cancellationToken);
            if (parents.Count != parentIds.Count)
                return AccessResult<TagDefinition>.Invalid("parentIds", "parent.unknown");
            // A plain tag under plain tags, a field's value under values of its field: the field is the value's own parent.
            if (parents.Any(p => p.FieldKey is not null || p.FieldId != tag.FieldId))
                return AccessResult<TagDefinition>.Invalid("parentIds", "parent.otherField");
            if (tag.Id != 0 && (parentIds.Contains(tag.Id) || await db.TagClosure.AnyAsync(r => r.AncestorId == tag.Id && parentIds.Contains(r.DescendantId), cancellationToken)))
                return AccessResult<TagDefinition>.Invalid("parentIds", "parent.cycle");
        }

        if (input.DisambiguationParentId is { } disambiguation && !parentIds.Contains(disambiguation))
            return AccessResult<TagDefinition>.Invalid("disambiguationParentId", "disambiguation.notParent");

        // A field's values are told apart by name, unless a disambiguation parent says which is which.
        if (tag.FieldId is { } field && input.DisambiguationParentId is null && normalized != tag.NormalizedName
            && await db.Tags.AnyAsync(t => t.FieldId == field && t.NormalizedName == normalized && t.Id != tag.Id && t.DisambiguationParentId == null, cancellationToken))
            return AccessResult<TagDefinition>.Invalid("name", "name.taken");

        var aliases = new List<(string Name, string Normalized)>();
        foreach (var alias in input.Aliases ?? [])
        {
            var trimmed = alias.Trim();
            if (trimmed.Length == 0)
                continue;
            if (trimmed.Length > Tag.MaxNameLength)
                return AccessResult<TagDefinition>.Invalid("aliases", "alias.tooLong");
            var folded = TagNames.Normalize(trimmed);
            if (folded != normalized && aliases.All(a => a.Normalized != folded))
                aliases.Add((trimmed, folded));
        }

        if (aliases.Count > MaxAliases)
            return AccessResult<TagDefinition>.Invalid("aliases", "alias.tooMany");

        tag.Name = name;
        tag.NormalizedName = normalized;
        tag.SortName = sortName ?? (tag.FieldId is not null && await IsPersonFieldAsync(db, tag.FieldId.Value, cancellationToken) ? TagNames.PersonSortName(name) : name);
        tag.Shorthand = shorthand;
        tag.Color = color;
        tag.IsCategory = isField || input.IsCategory;
        tag.IsHidden = input.IsHidden;
        tag.DisambiguationParentId = input.DisambiguationParentId;

        tag.Aliases.RemoveAll(a => aliases.All(n => n.Name != a.Name));
        foreach (var (aliasName, folded) in aliases.Where(n => tag.Aliases.All(a => a.Name != n.Name)))
            tag.Aliases.Add(new TagAlias { TenantId = tag.TenantId, Name = aliasName, NormalizedName = folded });

        if (tag.Id == 0)
        {
            db.Tags.Add(tag);
            await db.SaveChangesAsync(cancellationToken);
        }

        var current = await db.TagParents.Where(p => p.ChildId == tag.Id).ToListAsync(cancellationToken);
        db.TagParents.RemoveRange(current.Where(p => !parentIds.Contains(p.ParentId)));
        db.TagParents.AddRange(parentIds.Where(id => current.All(p => p.ParentId != id)).Select(id => new TagParent { TenantId = tag.TenantId, ChildId = tag.Id, ParentId = id }));
        return null;
    }

    private static Task<bool> IsPersonFieldAsync(LibrarionDbContext db, int fieldId, CancellationToken cancellationToken) =>
        db.Tags.AnyAsync(t => t.Id == fieldId && t.FieldKey != null && PersonFields.Contains(t.FieldKey), cancellationToken);

    private static async Task<TagDefinition?> DefinitionAsync(LibrarionDbContext db, int tagId, CancellationToken cancellationToken)
    {
        var tag = await db.Tags.AsNoTracking().Include(t => t.Aliases).FirstOrDefaultAsync(t => t.Id == tagId, cancellationToken);
        if (tag is null)
            return null;
        var parents = await db.TagParents.Where(p => p.ChildId == tagId).Select(p => new TagParentRef(p.ParentId, p.Parent!.Name)).ToListAsync(cancellationToken);
        return new TagDefinition(tag.Id, tag.Name, tag.SortName, tag.Shorthand, tag.Color, tag.IsCategory, tag.IsHidden, tag.DisambiguationParentId,
            tag.FieldId, tag.FieldKey, tag.BuiltIn is not null, tag.Aliases.Select(a => a.Name).Order(StringComparer.CurrentCultureIgnoreCase).ToList(),
            parents.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), await LinksAsync(db, [tagId], cancellationToken));
    }

    [GeneratedRegex("^[a-z0-9-]{1,50}$")]
    private static partial Regex FieldKeyPattern();

    [GeneratedRegex("^#([0-9a-f]{3}|[0-9a-f]{6})$")]
    private static partial Regex ColorPattern();
}
