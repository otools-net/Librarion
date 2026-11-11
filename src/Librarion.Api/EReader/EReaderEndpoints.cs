using Librarion.Api.EReader.Pages;
using Librarion.Api.Endpoints;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Librarion.Api.EReader;

/// <summary>
/// The e-reader mode under <c>/e</c> (agents/docs/features/ereader-mode.md): a plain, grayscale face of the library for
/// the browsers of e-ink devices, rendered on the server by Razor components (D-8.1) with no script at all (its own
/// CSP, <see cref="SecurityHeaders.EReaderPolicy"/>). The app's cookie session; every route names its right; anything
/// else under <c>/e</c> is a plain 404 page (<see cref="EReaderGuard"/>), never the client's.
/// </summary>
public static partial class EReaderEndpoints
{
    public const string Prefix = "/e";

    /// <summary>The stylesheet's address under <see cref="Prefix"/> (<see cref="EReaderStylesheet"/>).</summary>
    public const string StylesheetPath = "/ereader.css";

    public static void MapEReaderEndpoints(this IEndpointRouteBuilder app)
    {
        var e = app.MapGroup(Prefix).ExcludeFromDescription();

        MapLibrary(e);
        e.MapGet(StylesheetPath, EReaderStylesheet.Serve).AllowAnonymous();
        e.MapGet("/signin", SignInPageAsync).AllowAnonymous();
        e.MapPost("/signin", SignInAsync).AllowAnonymous().RequireRateLimiting(SecurityServiceCollectionExtensions.SignInRateLimit);
        e.MapPost("/signout", SignOutAsync).AllowAnonymous();

        // Every other address, file-like ones too: not found (EReaderGuard renders the page), before the client's fallback takes it.
        e.MapFallback("{**path}", () => TypedResults.NotFound()).AllowAnonymous();
    }

    /// <summary>The sign-in page; someone already signed in goes on to where they were going.</summary>
    private static async Task<IResult> SignInPageAsync(string? @return, HttpContext context, CurrentAccess current)
    {
        var returnTo = SafeReturn(@return);
        if ((await current.GetAsync()).User is not null)
            return TypedResults.Redirect(context.Request.PathBase + returnTo);
        return SignInPage(context, returnTo, null, remember: true, error: null);
    }

    /// <summary>
    /// The sign-in form posted (its token checked by <see cref="EReaderGuard"/>): signed in as the app does
    /// (<see cref="SessionEndpoints.TrySignInAsync"/>: throttle, lockout, security log), then on to the return address;
    /// refused, the form again with why (400).
    /// </summary>
    private static async Task<IResult> SignInAsync(HttpContext context, ServerTexts texts, TimeProvider time)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var userName = form["userName"].ToString();
        var remember = form["remember"] == "on";
        var returnTo = SafeReturn(form["return"]);
        var outcome = await SessionEndpoints.TrySignInAsync(context, userName, form["password"], remember);
        if (outcome.User is not null)
            return TypedResults.Redirect(context.Request.PathBase + returnTo);

        var error = outcome.LockedUntil is { } until
            ? texts.Format("ereader.signIn.lockedOut", ("minutes", Math.Max(1, (int)Math.Ceiling((until - time.GetUtcNow()).TotalMinutes))))
            : texts["error.sign-in-failed"];
        var page = SignInPage(context, returnTo, userName, remember, error);
        page.StatusCode = StatusCodes.Status400BadRequest;
        return page;
    }

    /// <summary>
    /// Signs out (a form, never a link: GET changes nothing), then the sign-in page. The token keeps the method from
    /// binding as a RequestDelegate (HttpContext alone), which would drop its result.
    /// </summary>
    private static async Task<IResult> SignOutAsync(HttpContext context, CancellationToken _)
    {
        await SessionEndpoints.SignOutAsync(context);
        return TypedResults.Redirect(new EReaderLinks(context).SignIn());
    }

    private static RazorComponentResult<SignInPage> SignInPage(HttpContext context, string returnTo, string? userName, bool remember, string? error) =>
        new(new { Frame = EReaderFrame.Visitor(context, withToken: true), ReturnTo = returnTo, UserName = userName, Remember = remember, Error = error });

    /// <summary>
    /// The address to go back to after signing in: a path of the mode only (<c>/e</c>, <c>/e/…</c>), never another
    /// site (<c>//host</c>, a backslash, <c>https:</c>) nor the sign-in or sign-out address; else the home page.
    /// </summary>
    internal static string SafeReturn(string? value)
    {
        const string home = Prefix + "/";
        if (string.IsNullOrEmpty(value) || value.Contains('\\') || value.Contains("//", StringComparison.Ordinal) || value.Any(char.IsControl)
            || !(value == Prefix || value.StartsWith(home, StringComparison.Ordinal) || value.StartsWith(Prefix + "?", StringComparison.Ordinal)))
            return home;
        var path = value.Split('?', 2)[0].TrimEnd('/');
        return path is Prefix + "/signin" or Prefix + "/signout" ? home : value;
    }

    /// <summary>The request is for an e-reader page (a visitor without a session goes to its sign-in page, not a 401).</summary>
    public static bool IsEReader(HttpRequest request) => request.Path.StartsWithSegments(Prefix);
}
