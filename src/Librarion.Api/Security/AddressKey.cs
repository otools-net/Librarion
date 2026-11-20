using Librarion.Api.EReader;
using Librarion.Api.Opds;
using Microsoft.AspNetCore.Http.Features;

namespace Librarion.Api.Security;

/// <summary>
/// An API key in the address (agents/docs/features/opds-auth.md): <c>/opds/k/&lt;key&gt;/…</c> for reader apps that
/// can't sign in (D-7.5), <c>/e/k/&lt;key&gt;/…</c> for e-ink browsers that lose cookies (D-8.7). Taken out of the path
/// before routing (the request goes on as <c>/opds/…</c> or <c>/e/…</c>, its raw target too, so nothing after this sees
/// the key), kept in the request's items for the sign-in (<see cref="OpdsAuthenticationHandler"/>,
/// <see cref="ApiKeyAuthenticationHandler"/>) and the links (<see cref="OpdsLinks"/>, <see cref="EReaderLinks"/>: every
/// link keeps the prefix). The hosting layer's own log of the address, written before any middleware, is redacted by
/// <see cref="LogRedaction"/>.
/// </summary>
public sealed class AddressKey(RequestDelegate next)
{
    /// <summary>The segment after a prefix that a key follows.</summary>
    public const string Segment = "/k";

    /// <summary>Where a key may come in the address.</summary>
    public static readonly IReadOnlyList<string> Prefixes = [OpdsEndpoints.Prefix, EReaderEndpoints.Prefix];

    private const string Item = "librarion.addressKey";

    /// <summary>The key the request's address carried; null for any other request.</summary>
    public static string? Of(HttpContext context) => context.Items.TryGetValue(Item, out var key) ? key as string : null;

    /// <summary><paramref name="prefix"/> as this request reached it: with the key when its address carried one.</summary>
    public static string Root(HttpContext context, string prefix) =>
        Of(context) is { } key ? prefix + Segment + "/" + Uri.EscapeDataString(key) : prefix;

    public Task InvokeAsync(HttpContext context)
    {
        foreach (var prefix in Prefixes)
        {
            if (!context.Request.Path.StartsWithSegments(prefix + Segment, out var rest) || rest.Value is not { Length: > 1 } value)
                continue;
            var end = value.IndexOf('/', 1);
            context.Items[Item] = end < 0 ? value[1..] : value[1..end];
            context.Request.Path = new PathString(prefix + (end < 0 ? "" : value[end..]));
            if (context.Features.Get<IHttpRequestFeature>() is { } request)
                request.RawTarget = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            break;
        }
        return next(context);
    }
}
