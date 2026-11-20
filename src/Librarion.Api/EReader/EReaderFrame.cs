using Librarion.Api.Security;
using Microsoft.AspNetCore.Antiforgery;

namespace Librarion.Api.EReader;

/// <summary>
/// What every e-reader page's frame needs (<c>EPage</c>): its links, the signed-in account's name (null for a visitor),
/// and the form token its forms carry (D-8.4: plain forms can't send <c>X-Librarion-Request</c>, so every <c>/e</c>
/// POST carries ASP.NET Core's antiforgery token instead, checked by <see cref="EReaderGuard"/>), and the device whose
/// formats come first (<see cref="EReaderDevices"/>; null: no order).
/// </summary>
public sealed record EReaderFrame(EReaderLinks Links, string? UserName, AntiforgeryTokenSet? Token, EReaderDevice? Device = null)
{
    /// <summary>The frame of this request: with the account and a form token when someone is signed in (the sign-out form).</summary>
    public static async Task<EReaderFrame> ForAsync(HttpContext context)
    {
        var state = await context.RequestServices.GetRequiredService<CurrentAccess>().GetAsync();
        var links = new EReaderLinks(context);
        if (state.User is not { } user)
            return new EReaderFrame(links, null, null, EReaderDevices.Of(context.Request));
        return new EReaderFrame(links, string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName, Tokens(context),
            EReaderDevices.Of(context.Request));
    }

    /// <summary>A frame without an account (the sign-in page, a server not set up), with a form token when it has a form.</summary>
    public static EReaderFrame Visitor(HttpContext context, bool withToken = false) =>
        new(new EReaderLinks(context), null, withToken ? Tokens(context) : null);

    private static AntiforgeryTokenSet Tokens(HttpContext context) =>
        context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
}
