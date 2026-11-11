using Librarion.Api.EReader.Pages;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Librarion.Api.EReader;

/// <summary>
/// What every <c>/e</c> address goes through, after the sign-in is read and before the routes' rights (D-8.2, D-8.5):
/// <list type="bullet">
/// <item>until the server is set up, a plain page saying so (503), as the API's <see cref="RequestGuard"/>;</item>
/// <item>with the mode switched off for the tenant (the server's value before anyone signs in), not found (404);</item>
/// <item>a request that may change something (anything but GET and HEAD) without a valid antiforgery token: refused
/// with a plain page (400, D-8.4), before any route sees it;</item>
/// <item>an answer that is an error without a body (a route's 404, the rights' 403, a 405, the rate limit's 429) becomes a plain page, never
/// the API's problem JSON or the client's page.</item>
/// </list>
/// The stylesheet is let through always: the pages above link it.
/// </summary>
public sealed class EReaderGuard(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SetupService setup, EReaderSwitch onOff, CurrentAccess current, ServerTexts texts, IAntiforgery antiforgery)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments(EReaderEndpoints.Prefix, out var rest) || rest == EReaderEndpoints.StylesheetPath)
        {
            await next(context);
            return;
        }

        if (await setup.IsRequiredAsync(context.RequestAborted))
        {
            await Page(EReaderFrame.Visitor(context), texts["ereader.setup.title"], texts["ereader.setup.text"], StatusCodes.Status503ServiceUnavailable, home: false)
                .ExecuteAsync(context);
            return;
        }

        var state = await current.GetAsync();
        if (!await onOff.IsOnAsync(state.TenantId, context.RequestAborted))
        {
            await NotFound(EReaderFrame.Visitor(context), texts).ExecuteAsync(context);
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !await antiforgery.IsRequestValidAsync(context))
        {
            await Page(await EReaderFrame.ForAsync(context), texts["ereader.formExpired.title"], texts["ereader.formExpired.text"], StatusCodes.Status400BadRequest)
                .ExecuteAsync(context);
            return;
        }

        await next(context);

        if (context.Response.HasStarted || context.Response.ContentLength is > 0 || context.Response.ContentType is not null)
            return;
        var status = context.Response.StatusCode;
        if (status is not (StatusCodes.Status400BadRequest or StatusCodes.Status403Forbidden or StatusCodes.Status404NotFound
            or StatusCodes.Status405MethodNotAllowed or StatusCodes.Status429TooManyRequests))
            return;
        var frame = await EReaderFrame.ForAsync(context);
        var page = status switch
        {
            StatusCodes.Status403Forbidden => Page(frame, texts["ereader.forbidden.title"], texts["error.forbidden"], status),
            StatusCodes.Status400BadRequest => Page(frame, texts["ereader.badRequest.title"], texts["ereader.badRequest.text"], status),
            StatusCodes.Status429TooManyRequests => Page(frame, texts["ereader.tooMany.title"], texts["ereader.tooMany.text"], status),
            _ => NotFound(frame, texts),
        };
        await page.ExecuteAsync(context);
    }

    public static RazorComponentResult<MessagePage> NotFound(EReaderFrame frame, ServerTexts texts) =>
        Page(frame, texts["ereader.notFound.title"], texts["ereader.notFound.text"], StatusCodes.Status404NotFound);

    private static RazorComponentResult<MessagePage> Page(EReaderFrame frame, string title, string text, int status, bool home = true) =>
        new(new { Frame = frame, Title = title, Text = text, Home = home }) { StatusCode = status };
}
