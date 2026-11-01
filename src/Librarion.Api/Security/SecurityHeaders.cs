using Microsoft.Net.Http.Headers;

namespace Librarion.Api.Security;

/// <summary>
/// Headers every answer carries (as SFE): no framing by other sites, no type guessing, no addresses leaked in links,
/// and the app's pages run and load only the app's own files: <c>default-src 'self'</c> is also what makes a bundled
/// resource the only kind that works (agents/docs/development/privacy.md). A route that sets its own
/// <c>Content-Security-Policy</c> keeps it.
/// </summary>
public sealed class SecurityHeaders(RequestDelegate next)
{
    /// <summary>Angular adds component styles as &lt;style&gt; elements, hence <c>'unsafe-inline'</c> for styles only.</summary>
    public const string AppPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "media-src 'self' blob:; connect-src 'self'; font-src 'self' data:; object-src 'none'; base-uri 'self'; " +
        "form-action 'self'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers[HeaderNames.XContentTypeOptions] = "nosniff";
            headers[HeaderNames.XFrameOptions] = "DENY";
            headers["Referrer-Policy"] = "same-origin";
            if (!headers.ContainsKey(HeaderNames.ContentSecurityPolicy))
                headers[HeaderNames.ContentSecurityPolicy] = AppPolicy;
            return Task.CompletedTask;
        });
        return next(context);
    }
}
