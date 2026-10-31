using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;

namespace Librarion.Api.Endpoints;

/// <summary>Tenants: server administrators only (<see cref="Rights.ManageTenants"/>).</summary>
public static class TenantEndpoints
{
    public static void MapTenantEndpoints(this IEndpointRouteBuilder api)
    {
        var tenants = api.MapGroup("/tenants").WithTags("Tenants").RequireRight(Rights.ManageTenants);

        tenants.MapGet("/", async (AccessService accounts, CancellationToken cancellationToken) => TypedResults.Ok(await accounts.ListTenantsAsync(cancellationToken)))
            .WithName("ListTenants")
            .WithSummary("Every tenant.");

        tenants.MapPost("/", async (TenantInput input, HttpContext context, AccessService accounts, CurrentAccess current, SecurityLog log) =>
            {
                var result = await accounts.CreateTenantAsync(input, context.RequestAborted);
                if (result.Status == AccessStatus.Ok)
                    await log.LogAsync(context, await current.GetAsync(), SecurityEventKind.TenantCreated, null, result.Value!.Name);
                return ApiResults.From(result);
            })
            .WithName("CreateTenant")
            .WithSummary("Creates a tenant with its built-in groups (Administrators, Users) and its first library.");

        tenants.MapPut("/{id:int}", async (int id, TenantInput input, HttpContext context, AccessService accounts, CurrentAccess current, SecurityLog log) =>
            {
                var result = await accounts.UpdateTenantAsync(id, input, context.RequestAborted);
                if (result.Status == AccessStatus.Ok)
                    await log.LogAsync(context, await current.GetAsync(), SecurityEventKind.TenantChanged, null, result.Value!.Name);
                return ApiResults.From(result);
            })
            .WithName("UpdateTenant")
            .WithSummary("Renames, disables or enables a tenant, and sets the rights nobody gets in it.");

        tenants.MapDelete("/{id:int}", async (int id, HttpContext context, AccessService accounts, CurrentAccess current, SecurityLog log) =>
            {
                var tenant = await accounts.GetTenantAsync(id, context.RequestAborted);
                if (tenant is null || !await accounts.DeleteTenantAsync(id, context.RequestAborted))
                    return Results.NotFound();
                await log.LogAsync(context, await current.GetAsync(), SecurityEventKind.TenantDeleted, null, tenant.Name);
                return Results.NoContent();
            })
            .WithName("DeleteTenant")
            .WithSummary("Deletes a tenant with its libraries, accounts, groups and settings.");
    }
}
