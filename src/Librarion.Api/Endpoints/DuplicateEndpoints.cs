using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Merging;
using Librarion.Core.Tasks;

namespace Librarion.Api.Endpoints;

/// <summary>
/// The stored duplicate groups (stage 6 chunk 11, agents/docs/features/merging.md#stored-duplicate-groups): built whole
/// again on request, as a tenant run, by those who may merge books in some library. The end of each scan or import
/// keeps them, and builds them when the tenant has none.
/// </summary>
public static class DuplicateEndpoints
{
    public sealed record BuildStarted(long RunId);

    public static void MapDuplicateEndpoints(this IEndpointRouteBuilder api)
    {
        var duplicates = api.MapGroup("/duplicates").WithTags("Duplicates").RequireRight(Rights.MergeBooks);

        duplicates.MapPost("/build", async (HttpContext context, CurrentAccess current, BackgroundTasks tasks, DuplicateIndex index) =>
            {
                var state = await current.GetAsync();
                var tenantId = state.TenantId!.Value;
                var run = await tasks.StartTenantAsync(tenantId, BackgroundTaskKind.DuplicateBuild, state.User!.Id, async (progress, cancel) =>
                    new ScanResult(0, await index.BuildAsync(tenantId, progress, cancel), 0, 0, 0, 0, 0, [], []), context.RequestAborted);
                return Results.Accepted(null, new BuildStarted(run));
            })
            .WithName("BuildDuplicates")
            .WithSummary("Starts a background run building the tenant's duplicate groups whole again (same file, identifier, work): 202 with the run's id; its `changed` counts the groups.");
    }
}
