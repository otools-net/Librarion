using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;

namespace Librarion.Api.Endpoints;

/// <summary>
/// The security log: a tenant administrator reads its tenant's events (<see cref="Rights.TenantSecurityLog"/>), a
/// server administrator any tenant's, the server's own, or all (<see cref="Rights.ServerSecurityLog"/>).
/// </summary>
public static class SecurityEventEndpoints
{
    public static void MapSecurityEventEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/security-events", async (int? tenant, bool? all, long? before, string? kind, string? user,
                HttpContext context, CurrentAccess current, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                SecurityEventKind? kindFilter = Enum.TryParse<SecurityEventKind>(kind, ignoreCase: true, out var parsed) ? parsed : null;
                SecurityEventQuery query;
                if (state.Has(Rights.ServerSecurityLog))
                    query = new SecurityEventQuery(tenant, all == true && tenant is null, before, kindFilter, user);
                else if (state.TenantId is { } own && (tenant is null || tenant == own) && state.Has(Rights.TenantSecurityLog))
                    query = new SecurityEventQuery(own, false, before, kindFilter, user);
                else
                    return Results.NotFound();
                return Results.Ok(await log.ListAsync(query, context.RequestAborted));
            })
            .RequireRight(Rights.TenantSecurityLog, Rights.ServerSecurityLog)
            .WithTags("Security log")
            .WithName("ListSecurityEvents")
            .WithSummary("Security events, newest first, 100 at a time.")
            .WithDescription("`before` = the last id of the page before. A tenant administrator gets its tenant's; a server administrator a tenant's (`tenant`), every event (`all=true`), or the server's own (neither).");
    }
}
