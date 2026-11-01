using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Microsoft.Extensions.Options;

namespace Librarion.Api.Endpoints;

/// <param name="DisabledByServer">Matches <c>Librarion:Rights:Disabled</c>: nobody gets it, whatever the groups grant.</param>
public sealed record RightInfo(string Id, RightScope Scope, RightKind Kind, string Area, IReadOnlyList<BuiltInGroup> DefaultGroups, bool DisabledByServer);

/// <summary>
/// Groups of a tenant and the rights catalog: <see cref="Rights.ManageUsers"/> for the request's tenant,
/// <see cref="Rights.ManageTenants"/> for any.
/// </summary>
public static class GroupEndpoints
{
    public static void MapGroupEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/rights", (IOptionsMonitor<RightOptions> options) =>
                TypedResults.Ok(RightCatalog.All
                    .Select(r => new RightInfo(r.Id, r.Scope, r.Kind, r.Area, r.DefaultGroups, RightCatalog.MatchesAny(options.CurrentValue.Disabled, r.Id)))
                    .ToList()))
            .RequireRight(Rights.ManageUsers, Rights.ManageTenants)
            .WithTags("Groups")
            .WithName("ListRights")
            .WithSummary("Every right: scope, kind, area, default groups, and whether the server disables it.");

        var groups = api.MapGroup("/groups").WithTags("Groups").RequireRight(Rights.ManageUsers, Rights.ManageTenants);

        groups.MapGet("/", async (int? tenant, HttpContext context, CurrentAccess current, AccessService accounts) =>
                Target(await current.GetAsync(), tenant) is { } tenantId
                    ? Results.Ok(await accounts.ListGroupsAsync(tenantId, context.RequestAborted))
                    : Results.NotFound())
            .WithName("ListGroups")
            .WithSummary("The groups of a tenant (the request's own by default).");

        groups.MapPost("/", async (int? tenant, GroupInput input, HttpContext context, CurrentAccess current, AccessService accounts, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                if (Target(state, tenant) is not { } tenantId)
                    return Results.NotFound();
                var result = await accounts.CreateGroupAsync(tenantId, input, context.RequestAborted);
                if (result.Status == AccessStatus.Ok)
                    await log.LogAsync(context, state, SecurityEventKind.GroupCreated, tenantId, Describe(result.Value!));
                return ApiResults.From(result);
            })
            .WithName("CreateGroup")
            .WithSummary("Creates a group: its rights, libraries and members.");

        groups.MapPut("/{id:int}", async (int id, int? tenant, GroupInput input, HttpContext context, CurrentAccess current, AccessService accounts, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                if (Target(state, tenant) is not { } tenantId)
                    return Results.NotFound();
                var result = await accounts.UpdateGroupAsync(tenantId, id, input, context.RequestAborted);
                if (result.Status == AccessStatus.Ok)
                    await log.LogAsync(context, state, SecurityEventKind.GroupChanged, tenantId, Describe(result.Value!));
                return ApiResults.From(result);
            })
            .WithName("UpdateGroup")
            .WithSummary("Changes a group (built-in groups keep their names).");

        groups.MapDelete("/{id:int}", async (int id, int? tenant, HttpContext context, CurrentAccess current, AccessService accounts, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                if (Target(state, tenant) is not { } tenantId)
                    return Results.NotFound();
                var group = (await accounts.ListGroupsAsync(tenantId, context.RequestAborted)).FirstOrDefault(g => g.Id == id);
                var result = await accounts.DeleteGroupAsync(tenantId, id, context.RequestAborted);
                if (result.Status == AccessStatus.Ok)
                    await log.LogAsync(context, state, SecurityEventKind.GroupDeleted, tenantId, group?.Name);
                return ApiResults.From(result, _ => Results.NoContent());
            })
            .WithName("DeleteGroup")
            .WithSummary("Deletes a group made by hand (not a built-in one).");
    }

    private static int? Target(AccessState state, int? tenant) => ApiResults.TargetTenant(state, tenant, Rights.ManageUsers, Rights.ManageTenants);

    private static string Describe(GroupInfo group) => $"{group.Name}: {string.Join(", ", group.Rights)}";
}
