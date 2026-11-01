using Librarion.Api.Security;
using Librarion.Core.Access;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Librarion.Api.Endpoints;

/// <summary>How the services' answers become HTTP answers, and which tenant a request may act on.</summary>
public static class ApiResults
{
    /// <summary>
    /// Ok → 200 with the value; Invalid → 400 with the errors by field (codes the client translates); NotFound → 404;
    /// Conflict → 409 whose <c>type</c> is the problem's code.
    /// </summary>
    public static IResult From<T>(AccessResult<T> result, Func<T, IResult>? ok = null) => result.Status switch
    {
        AccessStatus.Ok => ok?.Invoke(result.Value!) ?? TypedResults.Ok(result.Value),
        AccessStatus.Invalid => TypedResults.ValidationProblem(result.Errors ?? []),
        AccessStatus.NotFound => TypedResults.NotFound(),
        _ => TypedResults.Problem(result.Problem, statusCode: StatusCodes.Status409Conflict, type: result.Problem),
    };

    /// <summary>
    /// The tenant a request acts on: <paramref name="requested"/>, or the request's own when none is given. Its own
    /// needs <paramref name="tenantRight"/>; another tenant needs <paramref name="serverRight"/>. Null = not allowed
    /// (answered 404: a tenant administrator doesn't learn which other tenants exist).
    /// </summary>
    public static int? TargetTenant(AccessState state, int? requested, string tenantRight, string serverRight)
    {
        var tenantId = requested ?? state.TenantId;
        if (tenantId is null)
            return null;
        if (tenantId == state.TenantId && state.Has(tenantRight))
            return tenantId;
        return state.Has(serverRight) ? tenantId : null;
    }

    /// <summary>The signed-in account's name and id, and the request's address, for the security log.</summary>
    public static Task LogAsync(this SecurityLog log, HttpContext context, AccessState state, Core.Data.SecurityEventKind kind, int? tenantId, string? detail) =>
        log.WriteAsync(kind, tenantId, state.User?.Id, state.User?.UserName, context.Address(), detail, context.RequestAborted);

    public static NotFound NotFound() => TypedResults.NotFound();
}
