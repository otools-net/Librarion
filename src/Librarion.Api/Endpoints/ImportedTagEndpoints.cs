using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Merging;
using Librarion.Core.Tagging;

namespace Librarion.Api.Endpoints;

/// <summary>
/// The Imported tags page (stage 6 chunk 9, agents/docs/features/imported-tags.md), all <c>library.importedTags</c>:
/// read per library (the right in that library); a change touches the tenant's mappings, so it needs the right in every
/// library of the tenant, and mapping (a merge) also <c>tenant.tags</c>. A change of more than
/// <see cref="TagService.InlineLinks"/> links or <see cref="TagMerger.InlineTags"/> tags answers 202 with its run.
/// </summary>
public static class ImportedTagEndpoints
{
    /// <param name="Libraries">The libraries whose page the caller may read.</param>
    /// <param name="CanChange">May keep and ignore (the right in every library of the tenant).</param>
    /// <param name="CanMap">May also map onto another tag (with <c>tenant.tags</c>).</param>
    public sealed record ImportedContext(IReadOnlyList<int> Libraries, bool CanChange, bool CanMap);

    public sealed record TagsInput(IReadOnlyList<int>? TagIds);

    public sealed record ReviewInput(IReadOnlyList<int>? TagIds, bool Reviewed = true);

    public sealed record MapInput(IReadOnlyList<int>? TagIds, int TargetId);

    public static void MapImportedTagEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/imported-tags").WithTags("Imported tags").RequireRight(Rights.ImportedTags);

        group.MapGet("/context", async (HttpContext context, CurrentAccess current, ImportedTags imported) =>
            {
                var state = await current.GetAsync();
                var (libraries, canChange) = await RightsAsync(state, imported, context.RequestAborted);
                return Results.Ok(new ImportedContext(libraries, canChange, canChange && state.Access.Has(Rights.ManageTags)));
            })
            .WithName("GetImportedTagsContext")
            .WithSummary("Which libraries' imported tags the caller may read, and whether it may change them (keep, ignore) and map them.");

        group.MapGet("/fields", async (HttpContext context, CurrentAccess current, ImportedTags imported) =>
                Results.Ok(await imported.FieldsAsync((await current.GetAsync()).TenantId!.Value, context.RequestAborted)))
            .WithName("ListImportedFields")
            .WithSummary($"Plain tags (`key` null) and the fields with imported names, each with its tags that have some (up to {TagLists.MaxCounted}). People and series are left out: their names have no mappings.");

        group.MapGet("", async (int library, string? field, string? source, string? status, string? after, int? limit, HttpContext context, CurrentAccess current,
                ImportedTags imported) =>
            {
                var state = await current.GetAsync();
                if (!state.Access.HasIn(Rights.ImportedTags, library) || !TryParse(source, out ImportTagSourceKind? kind) || !TryParse(status, out ImportedStatus? which))
                    return Results.NotFound();
                return await imported.ListAsync(state.TenantId!.Value, library, Field(field), kind, which ?? ImportedStatus.Review, after, limit ?? 50, context.RequestAborted)
                    is { } page
                    ? Results.Ok(page)
                    : Results.NotFound();
            })
            .WithName("ListImportedTags")
            .WithSummary("The tags of a field (`field` = its key; none: plain tags) that imports give (their raw names of `source`, or any), in `status` (`review`: a name not looked at yet; `reviewed`; `all`), by their books in `library` from the most, a page at a time (`after`).")
            .WithDescription($"`limit` up to {ImportedTags.MaxLimit}. A big field is walked a chunk at a time, so a page can end early with `next` set: ask for it to go on.");

        group.MapGet("/summary", async (int library, string? field, string? source, HttpContext context, CurrentAccess current, ImportedTags imported) =>
            {
                var state = await current.GetAsync();
                if (!state.Access.HasIn(Rights.ImportedTags, library) || !TryParse(source, out ImportTagSourceKind? kind))
                    return Results.NotFound();
                return await imported.SummaryAsync(state.TenantId!.Value, library, Field(field), kind, context.RequestAborted) is { } summary
                    ? Results.Ok(summary)
                    : Results.NotFound();
            })
            .WithName("GetImportedTagsSummary")
            .WithSummary($"How many tags of the field have imported names and books in `library`, their links, and how many of those the first {ImportedTags.TopCount} and the reviewed ones hold.");

        group.MapPost("/review", async (ReviewInput input, HttpContext context, CurrentAccess current, ImportedTags imported) =>
            {
                var state = await current.GetAsync();
                if (!(await RightsAsync(state, imported, context.RequestAborted)).CanChange)
                    return Results.Forbid();
                return ApiResults.From(await imported.ReviewAsync(state.TenantId!.Value, input.TagIds ?? [], input.Reviewed, context.RequestAborted),
                    changed => Results.Ok(new { changed }));
            })
            .WithName("ReviewImportedTags")
            .WithSummary($"Marks the raw names of up to {TagMerger.MaxBulk} tags reviewed (Keep: right as they are), or not (`reviewed` false).");

        group.MapPost("/map", async (MapInput input, HttpContext context, CurrentAccess current, ImportedTags imported, TagMerger merger) =>
            {
                var state = await current.GetAsync();
                if (!(await RightsAsync(state, imported, context.RequestAborted)).CanChange || !state.Access.Has(Rights.ManageTags))
                    return Results.Forbid();
                return Work(await merger.MapAsync(state.TenantId!.Value, input.TagIds ?? [], input.TargetId, state.User?.Id, context.RequestAborted));
            })
            .WithName("MapImportedTags")
            .WithSummary($"Maps up to {TagMerger.MaxBulk} tags onto `targetId`: each merged into it (its links, names and raw names follow, undone from Tags › Merges).");

        group.MapPost("/ignore", async (TagsInput input, HttpContext context, CurrentAccess current, ImportedTags imported, TagMerger merger) =>
            {
                var state = await current.GetAsync();
                if (!(await RightsAsync(state, imported, context.RequestAborted)).CanChange)
                    return Results.Forbid();
                return Work(await merger.IgnoreAsync(state.TenantId!.Value, input.TagIds ?? [], state.User?.Id, context.RequestAborted));
            })
            .WithName("IgnoreImportedTags")
            .WithSummary($"Ignores the raw names of up to {TagMerger.MaxBulk} tags: imports skip them from now on and the tags' imported links are taken off (the tags, their manual and rule links stay). Refused for a tag without imported names (`tag.notImported`).");

        group.MapGet("/ignored", async (long? before, int? limit, HttpContext context, CurrentAccess current, TagMerger merger) =>
                Results.Ok(await merger.IgnoredAsync((await current.GetAsync()).TenantId!.Value, before, limit ?? 50, context.RequestAborted)))
            .WithName("ListIgnoredTags")
            .WithSummary("The ignores that stand, newest first; `before` = the last id of the page before.");

        group.MapPost("/ignored/{id:long}/undo", async (long id, HttpContext context, CurrentAccess current, ImportedTags imported, TagMerger merger) =>
            {
                var state = await current.GetAsync();
                if (!(await RightsAsync(state, imported, context.RequestAborted)).CanChange)
                    return Results.Forbid();
                return Work(await merger.UndoAsync(state.TenantId!.Value, id, state.User?.Id, context.RequestAborted, MergeKind.Ignore));
            })
            .WithName("UndoIgnore")
            .WithSummary("Undoes an ignore: its raw names map to the tag again and its imported links come back. Refused (409) once undone or when the tag is gone.");
    }

    /// <summary>The libraries the caller may read the page of, and whether it has the right in all the tenant's.</summary>
    private static async Task<(IReadOnlyList<int> Libraries, bool CanChange)> RightsAsync(AccessState state, ImportedTags imported, CancellationToken cancellationToken)
    {
        var readable = state.Access.LibrariesWith(Rights.ImportedTags).ToHashSet();
        var all = await imported.LibrariesAsync(state.TenantId!.Value, cancellationToken);
        return (all.Where(readable.Contains).ToList(), all.Count > 0 && all.All(readable.Contains));
    }

    private static string? Field(string? key) => string.IsNullOrEmpty(key) ? null : key;

    private static bool TryParse<T>(string? text, out T? value) where T : struct, Enum
    {
        value = null;
        if (string.IsNullOrEmpty(text))
            return true;
        if (!Enum.TryParse<T>(text, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            return false;
        value = parsed;
        return true;
    }

    private static IResult Work(AccessResult<TagWork> result) =>
        ApiResults.From(result, work => work.RunId is null ? Results.Ok(work) : Results.Accepted($"/api/tasks/{work.RunId}", work));
}
