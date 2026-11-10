using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Merging;
using Librarion.Core.Tasks;

namespace Librarion.Api.Endpoints;

/// <summary>
/// Possible duplicate tags (stage 6 chunk 14, agents/docs/features/merging.md#possible-duplicate-tags-built-stage-6-chunk-14),
/// all <c>tenant.tags</c>: a field's groups by books, a tag's look-alikes, Not the same, and the groups built whole
/// again as a tenant run. Merging goes through the tag merge (<c>POST api/tags/{id}/merge</c>).
/// </summary>
public static class TagDuplicateEndpoints
{
    public sealed record NotSameInput(IReadOnlyList<int> TagIds);

    public static void MapTagDuplicateEndpoints(this IEndpointRouteBuilder api)
    {
        var duplicates = api.MapGroup("/tag-duplicates").WithTags("Tags").RequireRight(Rights.ManageTags);

        duplicates.MapGet("", async (string? field, string? after, int? limit, HttpContext context, CurrentAccess current, TagDuplicateQueries queries) =>
            {
                var tenantId = (await current.GetAsync()).TenantId!.Value;
                return await queries.FieldAsync(tenantId, field, context.RequestAborted) is { } id
                    ? Results.Ok(await queries.ListAsync(tenantId, id, after, limit ?? 20, context.RequestAborted))
                    : Results.NotFound();
            })
            .WithName("ListTagDuplicates")
            .WithSummary("Possible duplicate tags of a field (`field` = its key; none: plain tags): groups of tags whose names have the same words, sound alike, or are a name and its initials, the most books first; `next` is the `after` of the following page.")
            .WithDescription($"A request reads at most {TagDuplicateQueries.MaxChunks * TagDuplicateQueries.ChunkGroups} groups: a short page with a `next` means more to read. `built` is false until the groups were built.");

        duplicates.MapPost("/build", async (HttpContext context, CurrentAccess current, BackgroundTasks tasks, TagDuplicateIndex index) =>
            {
                var state = await current.GetAsync();
                var tenantId = state.TenantId!.Value;
                var run = await tasks.StartTenantAsync(tenantId, BackgroundTaskKind.TagDuplicateBuild, state.User!.Id, async (progress, cancel) =>
                    new ScanResult(0, await index.BuildAsync(tenantId, progress, cancel), 0, 0, 0, 0, 0, [], []), context.RequestAborted);
                return Results.Accepted(null, new DuplicateEndpoints.BuildStarted(run));
            })
            .WithName("BuildTagDuplicates")
            .WithSummary("Starts a background run finding the tenant's possible duplicate tags again: 202 with the run's id; its `changed` counts the groups.");

        duplicates.MapPost("/not", async (NotSameInput input, HttpContext context, CurrentAccess current, TagDuplicateQueries queries) =>
            {
                var ids = input.TagIds.Distinct().ToList();
                if (ids.Count is < 2 or > TagDuplicateIndex.MaxGroup)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["tagIds"] = ["tagDuplicates.tags"] });
                var tenantId = (await current.GetAsync()).TenantId!.Value;
                if (await queries.CountAsync(tenantId, ids, context.RequestAborted) != ids.Count)
                    return Results.NotFound();
                await queries.MarkAsync(tenantId, ids, context.RequestAborted);
                return Results.NoContent();
            })
            .WithName("MarkTagsNotSame")
            .WithSummary("Says the tags are not the same (every pair among them): never suggested together again.");

        api.MapGet("/tags/{id:int}/look-alikes", async (int id, HttpContext context, CurrentAccess current, TagDuplicateQueries queries) =>
                Results.Ok(await queries.OfTagAsync((await current.GetAsync()).TenantId!.Value, id, context.RequestAborted)))
            .WithTags("Tags")
            .RequireRight(Rights.ManageTags)
            .WithName("ListTagLookAlikes")
            .WithSummary("The tags that may be the same as this one (from the stored groups), each with why: the same words, sounds alike, or initials.");
    }
}
