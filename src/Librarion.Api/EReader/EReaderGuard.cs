using Librarion.Api.EReader.Pages;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Librarion.Api.EReader;

/// <summary>
/// What every <c>/e</c> address goes through, after the sign-in is read and before the routes' rights (D-8.2, D-8.5):
/// <list type="bullet">
/// <item>until the server is set up, a plain page saying so (503), as the API's <see cref="RequestGuard"/>;</item>
/// <item>with the mode switched off for the tenant (the server's value before anyone signs in), not found (404);</item>
/// <item>an answer that is an error without a body (a route's 404, the rights' 403, a 405) becomes a plain page, never
/// the API's problem JSON or the client's page.</item>
/// </list>
/// The stylesheet is let through always: the pages above link it.
/// </summary>
public sealed class EReaderGuard(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SetupService setup, EReaderSwitch onOff, CurrentAccess current, ServerTexts texts)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments(EReaderEndpoints.Prefix, out var rest) || rest == EReaderEndpoints.StylesheetPath)
        {
            await next(context);
            return;
        }

        var links = new EReaderLinks(context);
        if (await setup.IsRequiredAsync(context.RequestAborted))
        {
            await Page(links, texts["ereader.setup.title"], texts["ereader.setup.text"], StatusCodes.Status503ServiceUnavailable, home: false).ExecuteAsync(context);
            return;
        }

        var state = await current.GetAsync();
        if (!await onOff.IsOnAsync(state.TenantId, context.RequestAborted))
        {
            await NotFound(links, texts).ExecuteAsync(context);
            return;
        }

        await next(context);

        if (context.Response.HasStarted || context.Response.ContentLength is > 0 || context.Response.ContentType is not null)
            return;
        var page = context.Response.StatusCode switch
        {
            StatusCodes.Status404NotFound or StatusCodes.Status405MethodNotAllowed => NotFound(links, texts),
            StatusCodes.Status403Forbidden => Page(links, texts["ereader.forbidden.title"], texts["error.forbidden"], StatusCodes.Status403Forbidden),
            StatusCodes.Status400BadRequest => Page(links, texts["ereader.badRequest.title"], texts["ereader.badRequest.text"], StatusCodes.Status400BadRequest),
            _ => null,
        };
        if (page is not null)
            await page.ExecuteAsync(context);
    }

    public static RazorComponentResult<MessagePage> NotFound(EReaderLinks links, ServerTexts texts) =>
        Page(links, texts["ereader.notFound.title"], texts["ereader.notFound.text"], StatusCodes.Status404NotFound);

    private static RazorComponentResult<MessagePage> Page(EReaderLinks links, string title, string text, int status, bool home = true) =>
        new(new { Links = links, Title = title, Text = text, Home = home }) { StatusCode = status };
}
