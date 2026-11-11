using Microsoft.AspNetCore.Http.Features;

namespace Librarion.Api.Opds;

/// <summary>
/// The key in the catalog's address (<c>/opds/k/&lt;key&gt;/…</c>, D-7.5, agents/docs/features/opds-auth.md) for reader
/// apps that can't sign in: taken out of the path before routing (the request goes on as <c>/opds/…</c>, its raw target
/// too, so nothing after this sees the key), kept in the request's items for <see cref="OpdsAuthenticationHandler"/>
/// and <see cref="OpdsLinks"/> (every link of the feed keeps the prefix). The hosting layer's own log of the address,
/// written before any middleware, is redacted by <see cref="Security.LogRedaction"/>.
/// </summary>
public sealed class OpdsPathKey(RequestDelegate next)
{
    public const string Prefix = OpdsEndpoints.Prefix + "/k";

    private const string Item = "librarion.opdsPathKey";

    /// <summary>The key the request's address carried; null for any other request.</summary>
    public static string? Of(HttpContext context) => context.Items.TryGetValue(Item, out var key) ? key as string : null;

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(Prefix, out var rest) && rest.Value is { Length: > 1 } value)
        {
            var end = value.IndexOf('/', 1);
            context.Items[Item] = end < 0 ? value[1..] : value[1..end];
            context.Request.Path = new PathString(OpdsEndpoints.Prefix + (end < 0 ? "" : value[end..]));
            if (context.Features.Get<IHttpRequestFeature>() is { } request)
                request.RawTarget = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        }
        return next(context);
    }
}
