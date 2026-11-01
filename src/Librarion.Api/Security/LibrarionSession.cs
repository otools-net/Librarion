using System.Globalization;
using System.Security.Claims;
using Librarion.Core.Access;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Librarion.Api.Security;

/// <summary>
/// The sign-in cookie (as SFE's): the account's id, its security stamp and the tenant it works in. Changing the stamp
/// (new password, disabled account) ends every session at the next request. A cookie because <c>&lt;img&gt;</c> (covers)
/// and the reader's requests send cookies and nothing else; OPDS clients use Basic or keys instead (stage 7).
/// </summary>
public static class LibrarionSession
{
    /// <summary>The cookie's name; the port is added to it (<see cref="InstanceCookieManager"/>).</summary>
    public const string CookieName = "librarion.session";

    public const string StampClaim = "librarion:stamp";
    public const string TenantClaim = "librarion:tenant";

    public static int? UserId(this ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true
        && int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    public static int? TenantId(this ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(TenantClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>Writes the cookie, and makes the rest of the request see the new session.</summary>
    public static async Task SignInAsync(HttpContext context, SessionUser user, int? tenantId, bool persistent)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.UserName),
            new(StampClaim, user.SecurityStamp),
        ];
        if (tenantId is { } tenant)
            claims.Add(new Claim(TenantClaim, tenant.ToString(CultureInfo.InvariantCulture)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = persistent });
        context.User = principal;
        context.RequestServices.GetRequiredService<CurrentAccess>().Reset();
    }

    /// <summary>Removes the cookie, and makes the rest of the request see a visitor without an account.</summary>
    public static async Task SignOutAsync(HttpContext context)
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        context.RequestServices.GetRequiredService<CurrentAccess>().Reset();
    }

    /// <summary>Signs the same account in again with another tenant or stamp, keeping whether the cookie outlives the browser.</summary>
    public static async Task RenewAsync(HttpContext context, SessionUser user, int? tenantId)
    {
        var current = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await SignInAsync(context, user, tenantId, current.Properties?.IsPersistent ?? false);
    }

    /// <summary>Ends the session of an account deleted or disabled since, or whose password or status changed.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.UserId() is not { } userId)
            return;

        var accounts = context.HttpContext.RequestServices.GetRequiredService<AccessService>();
        var user = await accounts.GetSessionUserAsync(userId, context.HttpContext.RequestAborted);
        if (user is null || user.Disabled || user.SecurityStamp != context.Principal.FindFirstValue(StampClaim))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}

/// <summary>
/// Cookie names of their own for each port (as SFE): browsers keep cookies per host, not per port, so two instances on
/// <c>localhost:5090</c> and <c>localhost:5091</c> would otherwise share the session cookie, each sign-in signing the
/// other out. No port in the Host (80, 443, a proxy passing none): the plain name.
/// </summary>
public sealed class InstanceCookieManager : ICookieManager
{
    private readonly ChunkingCookieManager _inner = new();

    public static string Name(HttpRequest request, string name) => request.Host.Port is { } port ? $"{name}-{port}" : name;

    public string? GetRequestCookie(HttpContext context, string key) => _inner.GetRequestCookie(context, Name(context.Request, key));

    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options) =>
        _inner.AppendResponseCookie(context, Name(context.Request, key), value, options);

    public void DeleteCookie(HttpContext context, string key, CookieOptions options) =>
        _inner.DeleteCookie(context, Name(context.Request, key), options);
}
