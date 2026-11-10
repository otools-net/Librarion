using System.Threading.RateLimiting;
using Librarion.Core.Access;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

namespace Librarion.Api.Security;

public static class SecurityServiceCollectionExtensions
{
    public const string SignInRateLimit = "sign-in";

    /// <summary>Sign-in cookie, rights, sign-in limits and forwarded headers (agents/docs/architecture/sessions-and-pipeline.md).</summary>
    public static IServiceCollection AddLibrarionSecurity(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        var security = configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();

        // Kept on the disk: otherwise every restart signs everyone out.
        services.AddDataProtection()
            .SetApplicationName("Librarion")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(security.KeysDirectory, contentRoot)));

        services.AddHttpContextAccessor();
        services.AddScoped<CurrentAccess>();
        services.AddSingleton<SignInThrottle>();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = LibrarionSession.CookieName;
                options.CookieManager = new InstanceCookieManager();
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                // Plain http on a home network has to work; behind https (or a trusted proxy's) the cookie is marked secure.
                options.Cookie.SecurePolicy = security.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromDays(Math.Max(1, security.SessionDays));
                options.SlidingExpiration = true;
                // An API answers with a status; the client shows its own sign-in page.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = LibrarionSession.ValidateAsync;
            })
            // Reader apps on /opds: Basic or API keys, never the cookie (Opds/OpdsAuthentication.cs).
            .AddScheme<AuthenticationSchemeOptions, Librarion.Api.Opds.OpdsAuthenticationHandler>(Librarion.Api.Opds.OpdsAuthenticationHandler.Scheme, null);

        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, RightPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, RightHandler>();

        // A reverse proxy's visitor address and scheme: this computer's proxies always, others when named.
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var proxy in security.TrustedProxies.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()))
            {
                if (System.Net.IPNetwork.TryParse(proxy, out var network))
                    options.KnownIPNetworks.Add(network);
                else if (System.Net.IPAddress.TryParse(proxy, out var address))
                    options.KnownProxies.Add(address);
                else
                    throw new InvalidOperationException($"Librarion:Security:TrustedProxies: “{proxy}” is not an address or a network (like 10.0.0.0/8).");
            }
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(SignInRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, security.SignInsPerMinute), Window = TimeSpan.FromMinutes(1) }));
        });

        return services;
    }

    /// <summary>The visitor's address, for the throttle and the security log.</summary>
    public static string Address(this HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
}
