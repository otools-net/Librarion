using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Merging;
using Librarion.Core.Tagging;

namespace Librarion.Api.Endpoints;

/// <summary>
/// The tenant's tag vocabulary (stage 6, agents/docs/features/tags.md): definitions, delete, merges and their undo, all
/// <c>tenant.tags</c>. A change touching more than <see cref="TagService.InlineLinks"/> links answers 202 with the run
/// that does it (the progress panel follows it); a smaller one is done when it answers.
/// </summary>
public static class TagEndpoints
{
    public sealed record MergeInput(int TargetId);

    public sealed record FieldMergeInput(string? Target);

    public static void MapTagEndpoints(this IEndpointRouteBuilder api)
    {
        var tags = api.MapGroup("").WithTags("Tags").RequireRight(Rights.ManageTags);

        tags.MapGet("/tags/manage/fields", async (HttpContext context, CurrentAccess current, TagLists lists) =>
                await TenantOf(current) is { } tenantId ? Results.Ok(await lists.FieldsAsync(tenantId, context.RequestAborted)) : Results.NotFound())
            .WithName("ListManagedFields")
            .WithSummary($"The tag manager's fields with how many values each has, and the plain tags counted the same way (up to {TagLists.MaxCounted}).");

        tags.MapGet("/tags/manage", async (string? field, string? q, string? after, int? limit, HttpContext context, CurrentAccess current, TagLists lists) =>
                await TenantOf(current) is { } tenantId && await lists.ListAsync(tenantId, string.IsNullOrEmpty(field) ? null : field, q, after, limit ?? 50, context.RequestAborted) is { } page
                    ? Results.Ok(page)
                    : Results.NotFound())
            .WithName("ListManagedTags")
            .WithSummary("Every tag of a field (`field` = its key; none: plain tags), books or not, by sort name a page at a time (`after`); with `q`, those whose name starts so or has its words, one page (`capped` when there were more).")
            .WithDescription($"Each with its books in every library and how many tags are directly under it. `limit` up to {TagLists.MaxLimit}.");

        tags.MapGet("/tags/{id:int}/definition", async (int id, HttpContext context, CurrentAccess current, TagService service) =>
                await TenantOf(current) is { } tenantId && await service.GetAsync(tenantId, id, context.RequestAborted) is { } tag
                    ? Results.Ok(tag)
                    : Results.NotFound())
            .WithName("GetTagDefinition")
            .WithSummary("A tag as its editor shows it: names, aliases, parents, flags, and its books in every library (what a delete or merge changes).");

        tags.MapPost("/tags", async (TagInput input, HttpContext context, CurrentAccess current, TagService service) =>
                await TenantOf(current) is { } tenantId
                    ? ApiResults.From(await service.CreateAsync(tenantId, input, context.RequestAborted), tag => Results.Created($"/api/tags/{tag.Id}", tag))
                    : Results.NotFound())
            .WithName("CreateTag")
            .WithSummary("Creates a plain tag, a value of a field (`fieldId`) or a field (`fieldKey`: `[a-z0-9-]`, unique).")
            .WithDescription("Parents must be tags of the same kind (plain tags, or values of the same field); a cycle is refused; `disambiguationParentId` must be one of the parents; `color` is `#rgb` or `#rrggbb`.");

        tags.MapPut("/tags/{id:int}", async (int id, TagInput input, HttpContext context, CurrentAccess current, TagService service) =>
                await TenantOf(current) is { } tenantId ? ApiResults.From(await service.UpdateAsync(tenantId, id, input, context.RequestAborted)) : Results.NotFound())
            .WithName("UpdateTag")
            .WithSummary("Changes a tag: names, aliases, parents (the closure kept), flags. A built-in field keeps its name.");

        tags.MapDelete("/tags/{id:int}", async (int id, HttpContext context, CurrentAccess current, TagService service) =>
                await current.GetAsync() is { TenantId: { } tenantId } state
                    ? Work(await service.DeleteAsync(tenantId, id, state.User?.Id, context.RequestAborted))
                    : Results.NotFound())
            .WithName("DeleteTag")
            .WithSummary("Deletes a tag and its links. Refused (409) for built-in tags (`tag.builtIn`), a field with values (`field.notEmpty`) and a tag a content filter names (`tag.inFilter`).");

        tags.MapPost("/tags/{id:int}/merge", async (int id, MergeInput input, HttpContext context, CurrentAccess current, TagMerger merger) =>
                await current.GetAsync() is { TenantId: { } tenantId } state
                    ? Work(await merger.MergeAsync(tenantId, id, input.TargetId, state.User?.Id, context.RequestAborted))
                    : Results.NotFound())
            .WithName("MergeTag")
            .WithSummary("Merges the tag into `targetId`: its links, names (as aliases), import mappings, filters and children go to the target; undone by `api/merges/{id}/undo`.");

        tags.MapPost("/fields/{key}/merge", async (string key, FieldMergeInput input, HttpContext context, CurrentAccess current, TagMerger merger) =>
                await current.GetAsync() is { TenantId: { } tenantId } state
                    ? Work(await merger.MergeFieldAsync(tenantId, key, input.Target ?? "", state.User?.Id, context.RequestAborted))
                    : Results.NotFound())
            .WithName("MergeField")
            .WithSummary("Merges the field into the field `target`: values of the same name merged, the others moved; the key stays an alias (`genre:` keeps working).");

        tags.MapGet("/merges", async (long? before, int? limit, HttpContext context, CurrentAccess current, TagMerger merger) =>
                await TenantOf(current) is { } tenantId ? Results.Ok(await merger.ListAsync(tenantId, before, limit ?? 50, context.RequestAborted)) : Results.NotFound())
            .WithName("ListMerges")
            .WithSummary("The tenant's tag and field merges, newest first; `before` = the last id of the page before.");

        tags.MapPost("/merges/{id:long}/undo", async (long id, HttpContext context, CurrentAccess current, TagMerger merger) =>
                await current.GetAsync() is { TenantId: { } tenantId } state
                    ? Work(await merger.UndoAsync(tenantId, id, state.User?.Id, context.RequestAborted))
                    : Results.NotFound())
            .WithName("UndoMerge")
            .WithSummary("Undoes a merge. Refused (409) once undone (`merge.undone`), when the target is gone (`merge.targetGone`) or while a later merge of the target stands (`merge.later`).");
    }

    private static async Task<int?> TenantOf(CurrentAccess current) => (await current.GetAsync()).TenantId;

    /// <summary>Done: 200 with what it did; running: 202 with the run.</summary>
    private static IResult Work(AccessResult<TagWork> result) =>
        ApiResults.From(result, work => work.RunId is null ? Results.Ok(work) : Results.Accepted($"/api/tasks/{work.RunId}", work));
}
