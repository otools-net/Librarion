using Librarion.Core.Access;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Librarion.Api.Security;

/// <summary>
/// Checks made before the routes' own rights (as SFE):
/// <list type="bullet">
/// <item>a request changing something must carry <see cref="Header"/>: a page of another site cannot add it without the
/// browser asking this server first (CORS), which never agrees, so it can't act with a visitor's cookie;</item>
/// <item>until the first server administrator exists, only the setup and the session answer (503
/// <see cref="SetupRequiredType"/>); the reader apps' feeds (<c>/opds</c>) too.</item>
/// </list>
/// </summary>
public sealed class RequestGuard(RequestDelegate next)
{
    public const string Header = "X-Librarion-Request";
    public const string SetupRequiredType = "setup-required";

    public async Task InvokeAsync(HttpContext context, SetupService setup)
    {
        var path = context.Request.Path;
        var api = path.StartsWithSegments("/api");
        if (!api && !path.StartsWithSegments("/openapi") && !path.StartsWithSegments(Opds.OpdsEndpoints.Prefix))
        {
            await next(context);
            return;
        }

        var method = context.Request.Method;
        // A request with an API key carries no cookie's authority (the key decides: ApiKeySignIn), so no CSRF to guard.
        if (api && !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method)
            && !context.Request.Headers.ContainsKey(Header) && !ApiKeySignIn.HasKeyHeader(context.Request))
        {
            await TypedResults.Problem($"Requests changing something need the {Header} header.", statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context);
            return;
        }

        if (!path.StartsWithSegments("/api/setup") && !path.StartsWithSegments("/api/session")
            && await setup.IsRequiredAsync(context.RequestAborted))
        {
            await TypedResults.Problem(
                    "The server has no administrator yet: open the app to set it up.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    type: SetupRequiredType)
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
