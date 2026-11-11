using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Librarion.Api.Opds;

/// <summary>
/// How reader apps sign in on <c>/opds</c> (agents/docs/features/opds-auth.md). The feeds' routes name this scheme
/// only, so the request's user is what it finds: the web app's cookie is never accepted there (no CSRF surface). Without
/// credentials the answer is 401 with a Basic challenge, which makes reader apps ask for a name and password.
/// Basic carries the account's password or an API key; <c>Bearer</c> and <c>X-Api-Key</c> carry a key (<see cref="ApiKeySignIn"/>).
/// </summary>
public sealed class OpdsAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SignInService signIn,
    AccessService accounts,
    SignInThrottle throttle,
    BasicVerificationCache cache,
    SecurityLog log)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Opds";

    /// <summary>The challenge every 401 of the feeds carries.</summary>
    public const string Challenge = "Basic realm=\"Librarion\", charset=\"UTF-8\"";

    /// <summary>The challenge of a 401 to a key in the address (not Basic: the app would ask for a password in vain).</summary>
    public const string PathKeyChallenge = "Bearer realm=\"Librarion\", error=\"invalid_token\"";

    /// <summary>The security log's detail on a failed sign-in by a reader app.</summary>
    public const string LogDetail = "opds";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // A key in the address decides alone (what else the app sends is not looked at): it must allow that use.
        if (OpdsPathKey.Of(Context) is { } pathKey)
            return await ApiKeySignIn.AuthenticateAsync(Context, SchemeName, pathKey, inPath: true);
        if (ApiKeySignIn.HasKeyHeader(Request))
            return await ApiKeySignIn.AuthenticateAsync(Context, SchemeName, ApiKeySignIn.ReadKeyHeader(Request));
        if (ReadBasic(Request) is not var (userName, password))
            return AuthenticateResult.NoResult();
        // An API key as the password (what most reader apps can send): the name must be its owner's.
        if (ApiKeys.LooksLikeKey(password))
            return await ApiKeySignIn.AuthenticateAsync(Context, SchemeName, password, userName);

        var address = Context.Address();
        if (throttle.LockedUntil(userName, address) is not null)
            return AuthenticateResult.Fail("Too many wrong passwords.");

        if (cache.Find(userName, password) is var (cachedId, stamp))
        {
            // Still the same account in the same state: no new password, not disabled, not signed out everywhere.
            if (await accounts.GetSessionUserAsync(cachedId, Context.RequestAborted) is { Disabled: false } known && known.SecurityStamp == stamp)
                return Success(known);
            cache.Forget(userName, password);
        }

        var result = await signIn.SignInAsync(userName, password, Context.RequestAborted);
        if (result is not { Status: SignInStatus.Ok, User: { } user })
        {
            var until = result.Status == SignInStatus.LockedOut ? result.LockedUntil : throttle.Failed(userName, address)?.UtcDateTime;
            await log.WriteAsync(until is null ? SecurityEventKind.SignInFailed : SecurityEventKind.LockedOut, null, null, userName.Trim(), address,
                result.Status == SignInStatus.Disabled ? LogDetail + ", disabled" : LogDetail, Context.RequestAborted);
            return AuthenticateResult.Fail("Wrong name or password.");
        }

        throttle.Succeeded(userName, address);
        cache.Remember(userName, password, user.Id, user.SecurityStamp);
        return Success(user);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        // A refused key in the address: asking for a name and password would not help (the address's key still decides).
        Response.Headers.WWWAuthenticate = OpdsPathKey.Of(Context) is null ? Challenge : PathKeyChallenge;
        return Task.CompletedTask;
    }

    /// <summary>The same claims as the session cookie's, so <see cref="CurrentAccess"/> works the account out as for the app.</summary>
    private static AuthenticateResult Success(SessionUser user)
    {
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.UserName),
            new(LibrarionSession.StampClaim, user.SecurityStamp),
        ];
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }

    /// <summary>The name and secret of <c>Authorization: Basic</c> (UTF-8, as the challenge asks); null when absent or malformed.</summary>
    public static (string UserName, string Secret)? ReadBasic(HttpRequest request)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header)
            || !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(header.Parameter))
            return null;

        string decoded;
        try
        {
            decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(header.Parameter));
        }
        catch (Exception e) when (e is FormatException or DecoderFallbackException)
        {
            return null;
        }

        // User names have no colon (accounts.md): the first one ends the name, the secret may hold more.
        var colon = decoded.IndexOf(':', StringComparison.Ordinal);
        return colon <= 0 || colon == decoded.Length - 1 ? null : (decoded[..colon], decoded[(colon + 1)..]);
    }
}
