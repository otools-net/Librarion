using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;

namespace Librarion.Api.Endpoints;

public sealed record LibraryInput(string? Name);

/// <summary>
/// Libraries of a tenant: listed for whoever may browse one (only those it reaches) or manage them; created, renamed
/// and deleted with <see cref="Rights.ManageLibraries"/> (another tenant's: <see cref="Rights.ManageTenants"/>).
/// </summary>
public static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this IEndpointRouteBuilder api)
    {
        var libraries = api.MapGroup("/libraries").WithTags("Libraries");

        libraries.MapGet("/", async (int? tenant, HttpContext context, CurrentAccess current, AccessService accounts) =>
            {
                var state = await current.GetAsync();
                if (ApiResults.TargetTenant(state, tenant, Rights.ManageLibraries, Rights.ManageTenants) is { } managed)
                    return Results.Ok(await accounts.ListLibrariesAsync(managed, context.RequestAborted));
                if (tenant is not null && tenant != state.TenantId || state.TenantId is not { } own)
                    return Results.NotFound();
                var reachable = state.Access.LibrariesWith(Rights.Browse).ToHashSet();
                return Results.Ok((await accounts.ListLibrariesAsync(own, context.RequestAborted)).Where(l => reachable.Contains(l.Id)).ToList());
            })
            .RequireRight(Rights.Browse, Rights.ManageLibraries, Rights.ManageTenants)
            .WithName("ListLibraries")
            .WithSummary("The libraries of a tenant (the request's own by default) the caller reaches or manages.");

        libraries.MapPost("/", async (int? tenant, LibraryInput input, HttpContext context, CurrentAccess current, AccessService accounts, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                if (ApiResults.TargetTenant(state, tenant, Rights.ManageLibraries, Rights.ManageTenants) is not { } tenantId)
                    return Results.NotFound();
                var result = await accounts.CreateLibraryAsync(tenantId, input.Name, context.RequestAborted);
                if (result.Status == AccessStatus.Ok)
                    await log.LogAsync(context, state, SecurityEventKind.LibraryCreated, tenantId, result.Value!.Name);
                return ApiResults.From(result);
            })
            .RequireRight(Rights.ManageLibraries, Rights.ManageTenants)
            .WithName("CreateLibrary")
            .WithSummary("Creates a library in a tenant (the request's own by default).");

        libraries.MapPut("/{id:int}", async (int id, int? tenant, LibraryInput input, HttpContext context, CurrentAccess current, AccessService accounts, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                if (ApiResults.TargetTenant(state, tenant, Rights.ManageLibraries, Rights.ManageTenants) is not { } tenantId)
                    return Results.NotFound();
                var result = await accounts.RenameLibraryAsync(tenantId, id, input.Name, context.RequestAborted);
                if (result.Status == AccessStatus.Ok)
                    await log.LogAsync(context, state, SecurityEventKind.LibraryChanged, tenantId, result.Value!.Name);
                return ApiResults.From(result);
            })
            .RequireRight(Rights.ManageLibraries, Rights.ManageTenants)
            .WithName("RenameLibrary")
            .WithSummary("Renames a library.");

        libraries.MapDelete("/{id:int}", async (int id, int? tenant, HttpContext context, CurrentAccess current, AccessService accounts, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                if (ApiResults.TargetTenant(state, tenant, Rights.ManageLibraries, Rights.ManageTenants) is not { } tenantId)
                    return Results.NotFound();
                var library = (await accounts.ListLibrariesAsync(tenantId, context.RequestAborted)).FirstOrDefault(l => l.Id == id);
                if (library is null || !await accounts.DeleteLibraryAsync(tenantId, id, context.RequestAborted))
                    return Results.NotFound();
                await log.LogAsync(context, state, SecurityEventKind.LibraryDeleted, tenantId, library.Name);
                return Results.NoContent();
            })
            .RequireRight(Rights.ManageLibraries, Rights.ManageTenants)
            .WithName("DeleteLibrary")
            .WithSummary("Deletes a library (its books, from stage 3 on, go with it).");
    }
}
