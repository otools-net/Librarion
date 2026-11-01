using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;

namespace Librarion.Api.Endpoints;

/// <param name="TenantId">The new account's tenant (the request's own by default); ignored for a server administrator.</param>
public sealed record NewUser(
    string? UserName, string? DisplayName = null, string? Password = null, bool IsServerAdmin = false, bool Disabled = false,
    IReadOnlyList<int>? GroupIds = null, int? TenantId = null);

/// <summary>
/// Accounts. A tenant administrator (<see cref="Rights.ManageUsers"/>) sees and changes its tenant's accounts only:
/// other ids are not found. <see cref="Rights.ManageAllUsers"/> reaches every account and makes server administrators.
/// </summary>
public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder api)
    {
        var users = api.MapGroup("/users").WithTags("Accounts").RequireRight(Rights.ManageUsers, Rights.ManageAllUsers);

        users.MapGet("/", async (int? tenant, bool? admins, string? search, string? after, HttpContext context, CurrentAccess current, AccessService accounts) =>
            {
                var state = await current.GetAsync();
                if (admins == true)
                {
                    return state.Has(Rights.ManageAllUsers)
                        ? Results.Ok(await accounts.ListUsersAsync(null, search, after, cancellationToken: context.RequestAborted))
                        : Results.NotFound();
                }

                return ApiResults.TargetTenant(state, tenant, Rights.ManageUsers, Rights.ManageAllUsers) is { } tenantId
                    ? Results.Ok(await accounts.ListUsersAsync(tenantId, search, after, cancellationToken: context.RequestAborted))
                    : Results.NotFound();
            })
            .WithName("ListUsers")
            .WithSummary("Accounts of a tenant (the request's own by default), or the server administrators (`admins=true`), 100 at a time by name.")
            .WithDescription("`after` = the last name of the page before; `search` = start of the name or display name.");

        users.MapGet("/{id:int}", async (int id, HttpContext context, CurrentAccess current, AccessService accounts) =>
                await accounts.GetUserAsync(id, Scope(await current.GetAsync()), context.RequestAborted) is { } user ? Results.Ok(user) : Results.NotFound())
            .WithName("GetUser")
            .WithSummary("One account.");

        users.MapPost("/", async (NewUser input, HttpContext context, CurrentAccess current, AccessService accounts, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                int? tenantId = null;
                if (input.IsServerAdmin ? !state.Has(Rights.ManageAllUsers) : (tenantId = ApiResults.TargetTenant(state, input.TenantId, Rights.ManageUsers, Rights.ManageAllUsers)) is null)
                    return Results.NotFound();

                var result = await accounts.CreateUserAsync(
                    new UserInput(input.UserName, input.DisplayName, input.Password, input.IsServerAdmin, input.Disabled, input.GroupIds), tenantId, context.RequestAborted);
                if (result.Status == AccessStatus.Ok)
                    await log.LogAsync(context, state, SecurityEventKind.UserCreated, tenantId, Describe(result.Value!));
                return ApiResults.From(result);
            })
            .WithName("CreateUser")
            .WithSummary("Creates an account in a tenant, or a server administrator (needs `server.users`).");

        users.MapPut("/{id:int}", async (int id, UserInput input, HttpContext context, CurrentAccess current, AccessService accounts, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                var result = await accounts.UpdateUserAsync(id, input, Scope(state), context.RequestAborted);
                if (result.Status == AccessStatus.Ok)
                    await log.LogAsync(context, state, SecurityEventKind.UserChanged, result.Value!.TenantId, Describe(result.Value));
                return ApiResults.From(result);
            })
            .WithName("UpdateUser")
            .WithSummary("Changes an account: name, display name, password, disabled, groups, unlock. A new password or status ends its sessions.");

        users.MapDelete("/{id:int}", async (int id, HttpContext context, CurrentAccess current, AccessService accounts, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                var scope = Scope(state);
                var user = await accounts.GetUserAsync(id, scope, context.RequestAborted);
                if (user is null)
                    return Results.NotFound();
                var result = await accounts.DeleteUserAsync(id, scope, context.RequestAborted);
                if (result.Status == AccessStatus.Ok)
                    await log.LogAsync(context, state, SecurityEventKind.UserDeleted, user.TenantId, user.UserName);
                return ApiResults.From(result, _ => Results.NoContent());
            })
            .WithName("DeleteUser")
            .WithSummary("Deletes an account.");
    }

    /// <summary>Every account for <see cref="Rights.ManageAllUsers"/>; the request's tenant's otherwise (-1: none).</summary>
    private static int? Scope(AccessState state) => state.Has(Rights.ManageAllUsers) ? null : state.TenantId ?? -1;

    private static string Describe(UserInfo user) =>
        user.Groups.Count == 0 ? user.UserName : $"{user.UserName} ({string.Join(", ", user.Groups.Select(g => g.Group))})";
}
