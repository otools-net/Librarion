using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Librarion.Api.Security;

/// <summary>
/// Signing in with an API key (agents/docs/features/opds-auth.md, D-7.2): <c>Authorization: Bearer &lt;key&gt;</c> or
/// <c>X-Api-Key</c> on <c>/api</c> and <c>/opds</c>, or the key as the Basic secret on <c>/opds</c>. The principal carries
/// the cookie's claims plus <see cref="KeyClaim"/>; the key's grant goes in the request's items, where
/// <see cref="CurrentAccess"/> narrows the account's rights by its scopes and libraries.
/// </summary>
public static class ApiKeySignIn
{
    public const string KeyClaim = "librarion:key";
    public const string Header = "X-Api-Key";

    /// <summary>The security log's detail on a refused key.</summary>
    public const string LogDetail = "api key";

    private const string GrantItem = "librarion.apiKeyGrant";

    /// <summary>The request names a key in a header (well formed or not): the key decides, never the cookie.</summary>
    public static bool HasKeyHeader(HttpRequest request) =>
        request.Headers.ContainsKey(Header)
        || (request.Headers.Authorization.ToString() is var authorization && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase));

    /// <summary>The key of <c>Authorization: Bearer</c> or <see cref="Header"/>.</summary>
    public static string? ReadKeyHeader(HttpRequest request)
    {
        if (request.Headers.TryGetValue(Header, out var header))
            return header.ToString().Trim();
        return AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var value)
               && string.Equals(value.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            ? value.Parameter?.Trim()
            : null;
    }

    /// <summary>The grant of the key the request signed in with; null for a password or a cookie.</summary>
    public static ApiKeyGrant? Grant(HttpContext context) => context.Items.TryGetValue(GrantItem, out var grant) ? grant as ApiKeyGrant : null;

    /// <summary>
    /// Checks <paramref name="key"/> (and, from Basic, that it is <paramref name="userName"/>'s); failures count in the
    /// sign-in throttle per address and go to the security log, never with the key.
    /// </summary>
    public static async Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string scheme, string? key, string? userName = null)
    {
        var services = context.RequestServices;
        var throttle = services.GetRequiredService<SignInThrottle>();
        var address = context.Address();
        // Per key: a stale key left in one reader app must not lock out the other keys used from the same address.
        var throttleName = userName ?? (ApiKeys.Parse(key) is var (id, _) ? $"{LogDetail} {id}" : LogDetail);
        if (throttle.LockedUntil(throttleName, address) is not null)
            return AuthenticateResult.Fail("Too many wrong keys.");

        var grant = await services.GetRequiredService<ApiKeys>().VerifyAsync(key, context.RequestAborted);
        var user = grant is null ? null : await services.GetRequiredService<AccessService>().GetSessionUserAsync(grant.UserId, context.RequestAborted);
        if (grant is null || user is null || (userName is not null && !string.Equals(userName.Trim(), user.UserName, StringComparison.OrdinalIgnoreCase)))
        {
            var until = throttle.Failed(throttleName, address);
            await services.GetRequiredService<SecurityLog>().WriteAsync(until is null ? SecurityEventKind.SignInFailed : SecurityEventKind.LockedOut,
                null, null, userName?.Trim(), address, LogDetail, context.RequestAborted);
            return AuthenticateResult.Fail("Wrong key.");
        }

        throttle.Succeeded(throttleName, address);
        context.Items[GrantItem] = grant;
        await services.GetRequiredService<ApiKeys>().UsedAsync(grant.KeyId, address, context.RequestAborted);
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.UserName),
            new(LibrarionSession.StampClaim, user.SecurityStamp),
            new(KeyClaim, grant.KeyId.ToString(CultureInfo.InvariantCulture)),
        ];
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, scheme)), scheme));
    }
}

/// <summary>Keys on <c>/api</c>: the default scheme sends requests with a key header here (<see cref="SecurityServiceCollectionExtensions"/>).</summary>
public sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        ApiKeySignIn.ReadKeyHeader(Request) is { Length: > 0 } key
            ? ApiKeySignIn.AuthenticateAsync(Context, SchemeName, key)
            : Task.FromResult(AuthenticateResult.Fail("No key."));

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"Librarion\"";
        return Task.CompletedTask;
    }
}
