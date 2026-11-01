using Microsoft.AspNetCore.Http.HttpResults;

namespace Librarion.Api.Security;

/// <summary>
/// Checks made before the routes' own rights. A request changing something must carry <see cref="Header"/>: a page of
/// another site cannot add it without the browser asking this server first (CORS), which never agrees, so it can't act
/// with a visitor's cookie (as SFE). The setup and update gates join here in stage 1.
/// </summary>
public sealed class RequestGuard(RequestDelegate next)
{
    public const string Header = "X-Librarion-Request";

    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        if (context.Request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method)
            && !context.Request.Headers.ContainsKey(Header))
        {
            await TypedResults.Problem($"Requests changing something need the {Header} header.", statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
