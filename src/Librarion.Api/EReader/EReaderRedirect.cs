using System.Globalization;
using Librarion.Api.Security;
using Librarion.Core.Access;

namespace Librarion.Api.EReader;

/// <summary>
/// The ways into the e-reader mode (D-8.11, agents/docs/features/ereader-mode.md): an e-ink browser (by its User-Agent,
/// <see cref="EReaderDevices.Guess"/>) asking for the app's page is sent to <c>/e</c> by the server, since its script
/// can't be relied on; a book's address to its <c>/e</c> page, anything else to the home page. A browser that chose the
/// full site (<see cref="FullSiteCookie"/>, set by <c>/e/full-site</c>, cleared by <c>/e/start</c>) is never sent;
/// nor while the server waits for its setup (the app's setup page) or with the mode switched off. Only the app's page
/// (<see cref="ClientPage"/>) asks here: never <c>/api</c>, <c>/opds</c>, files, covers or downloads.
/// </summary>
public static class EReaderRedirect
{
    /// <summary>This browser wants the app, not the e-reader pages: no redirect while it is there.</summary>
    public const string FullSiteCookie = "librarion.fullsite";

    /// <summary>Where an e-ink browser asking for the app's page goes instead; null: the app's page as usual.</summary>
    public static async Task<string?> TargetAsync(HttpContext context)
    {
        var request = context.Request;
        if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            || request.Cookies.ContainsKey(FullSiteCookie)
            || EReaderDevices.Guess(request.Headers.UserAgent) is null)
            return null;
        var services = context.RequestServices;
        if (await services.GetRequiredService<SetupService>().IsRequiredAsync(context.RequestAborted))
            return null;
        var state = await services.GetRequiredService<CurrentAccess>().GetAsync();
        if (!await services.GetRequiredService<EReaderSwitch>().IsOnAsync(state.TenantId, context.RequestAborted))
            return null;
        var links = new EReaderLinks(context);
        return BookOf(request.Path) is { } book ? links.AnyBook(book) : links.Home;
    }

    /// <summary>The book a client address names (<c>/books/&lt;id&gt;</c>, the app's book page); null for any other.</summary>
    internal static int? BookOf(PathString path)
    {
        var parts = (path.Value ?? "").Trim('/').Split('/');
        return parts is ["books", var id] && int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var book) ? book : null;
    }

    /// <summary>Keeps this browser on the app (a year): <c>/e/full-site</c>, the link on every e-reader page.</summary>
    public static void ChooseFullSite(HttpContext context) =>
        context.Response.Cookies.Append(FullSiteCookie, "1", Options(context, DateTimeOffset.UtcNow.AddYears(1)));

    /// <summary>Lets e-ink browsers be sent to the e-reader pages again: <c>/e/start</c>, the app's link to them.</summary>
    public static void ChooseEReader(HttpContext context)
    {
        if (context.Request.Cookies.ContainsKey(FullSiteCookie))
            context.Response.Cookies.Delete(FullSiteCookie, Options(context, null));
    }

    /// <summary>For every address of the app (its root, <c>PathBase</c> too), HttpOnly, Lax.</summary>
    private static CookieOptions Options(HttpContext context, DateTimeOffset? expires) => new()
    {
        Path = (context.Request.PathBase.Value?.TrimEnd('/') ?? "") + "/",
        Expires = expires,
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps,
        IsEssential = true,
    };
}
