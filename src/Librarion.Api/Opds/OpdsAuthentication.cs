using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Librarion.Api.Opds;

/// <summary>
/// How reader apps sign in on <c>/opds</c> (agents/docs/features/opds-auth.md). The feeds' routes name this scheme
/// only, so the request's user is what it finds: the web app's cookie is never accepted there (no CSRF surface). Without
/// credentials the answer is 401 with a Basic challenge, which makes reader apps ask for a name and password.
/// Stage 7 chunk 1 finds nobody yet; chunk 2 checks Basic passwords, chunk 3 API keys.
/// </summary>
public sealed class OpdsAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Opds";

    /// <summary>The challenge every 401 of the feeds carries.</summary>
    public const string Challenge = "Basic realm=\"Librarion\", charset=\"UTF-8\"";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = Challenge;
        return Task.CompletedTask;
    }
}
