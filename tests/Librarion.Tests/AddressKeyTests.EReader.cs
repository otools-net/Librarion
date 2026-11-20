using System.Net;
using System.Text.RegularExpressions;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Settings;
using Librarion.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>A key in the e-reader pages' address (<c>/e/k/&lt;key&gt;/…</c>, stage 8 chunk 8, D-8.7).</summary>
public sealed partial class AddressKeyTests
{
    /// <summary>
    /// A page of the mode reached with the key alone (no cookie): plain, and every link, picture and form under the key's
    /// prefix; only the app's icon (a static file, no key needed) at the root.
    /// </summary>
    private async Task<string> KeyPageAsync(HttpClient app, string address, string prefix)
    {
        using var response = await app.GetAsync(address, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, address);
        var html = await EReaderTests.PlainPageAsync(response, Cancel);
        var targets = Target().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        Assert.IsNotEmpty(targets, address);
        Assert.IsTrue(targets.All(t => t.StartsWith(prefix + "/", StringComparison.Ordinal) || t == "/favicon.svg"), address + "\n" + string.Join("\n", targets));
        return html;
    }

    /// <summary>
    /// The address alone opens the pages (no cookie, no sign-in): home, a list, a book, its cover and both downloads,
    /// every link keeping the prefix. No sign-out there; the sign-in and sign-out addresses are not found.
    /// </summary>
    [TestMethod]
    public async Task AKeyInTheEReadersAddressOpensItsPagesAndEveryLinkKeepsIt()
    {
        await using var shelf = await ShelfAsync();
        var key = (await KeyAsync(shelf, pathAllowed: true)).Key;
        using var app = Bare(shelf);
        var prefix = $"/e/k/{key}";
        var id = shelf.LibraryId;

        var home = await KeyPageAsync(app, prefix + "/", prefix);
        StringAssert.Contains(home, "<h1>Library</h1>", "the only library's page");
        Assert.IsFalse(home.Contains("signout", StringComparison.Ordinal), "nothing to sign out of");
        await KeyPageAsync(app, prefix, prefix);

        var titles = await KeyPageAsync(app, $"{prefix}/l/{id}/titles", prefix);
        var book = WebUtility.HtmlDecode(BookHref().Match(titles).Groups[1].Value);
        StringAssert.StartsWith(book, $"{prefix}/l/{id}/books/");
        var page = await KeyPageAsync(app, book, prefix);
        StringAssert.Contains(page, "The Hobbit");

        var file = WebUtility.HtmlDecode(FileHref().Match(page).Groups[1].Value);
        using (var download = await app.GetAsync(file, Cancel))
        {
            Assert.AreEqual(HttpStatusCode.OK, download.StatusCode, file);
            StringAssert.Contains(await download.Content.ReadAsStringAsync(Cancel), "The text.");
        }
        using (var zipped = await app.GetAsync(file + "/zip", Cancel))
        {
            Assert.AreEqual(HttpStatusCode.OK, zipped.StatusCode);
            Assert.AreEqual("application/zip", zipped.Content.Headers.ContentType?.MediaType);
        }
        var cover = WebUtility.HtmlDecode(CoverSrc().Match(page).Groups[1].Value);
        using (var image = await app.GetAsync(cover, Cancel))
            Assert.AreEqual(HttpStatusCode.OK, image.StatusCode, cover);
        await KeyPageAsync(app, $"{prefix}/l/{id}/search?q=hobbit", prefix);
        await KeyPageAsync(app, $"{prefix}/l/{id}/fields/author", prefix);

        foreach (var address in new[] { "/signin", "/signin?return=/e/", "/signout" })
            using (var gone = await app.GetAsync(prefix + address, Cancel))
                Assert.AreEqual(HttpStatusCode.NotFound, gone.StatusCode, address);
        using (var signOut = await app.PostAsync(prefix + "/signout", new FormUrlEncodedContent([]), Cancel))
            Assert.AreEqual(HttpStatusCode.NotFound, signOut.StatusCode);
        using (var signIn = await app.PostAsync(prefix + "/signin", new FormUrlEncodedContent([]), Cancel))
            Assert.AreEqual(HttpStatusCode.NotFound, signIn.StatusCode);
    }

    /// <summary>
    /// Refused with a plain page (401, the Bearer challenge, its links to the mode without the key): a key that doesn't allow the
    /// address, one only for the API, a wrong one, any while the tenant has switched path keys off, a revoked one; the
    /// address's key decides even in a browser signed in by cookie. With the mode switched off, not found.
    /// </summary>
    [TestMethod]
    public async Task OnlyAKeyAllowedInTheAddressOpensTheEReader()
    {
        await using var shelf = await ShelfAsync();
        var allowed = await KeyAsync(shelf, pathAllowed: true);
        var notAllowed = await KeyAsync(shelf, pathAllowed: false);
        var apiOnly = await KeyAsync(shelf, true, ApiKeyScopes.ApiRead);
        using var app = Bare(shelf);
        using var signedIn = await EReaderTests.SignedInBrowserAsync(shelf.Factory, "reader", Cancel);
        var id = shelf.LibraryId;

        async Task<HttpStatusCode> StatusAsync(string key, HttpClient? client = null)
        {
            var address = $"/e/k/{key}/l/{id}/titles";
            using var response = await (client ?? app).GetAsync(address, Cancel);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Assert.AreEqual(ApiKeySignIn.PathKeyChallenge, response.Headers.WwwAuthenticate.ToString());
                var html = await EReaderTests.PlainPageAsync(response, Cancel);
                StringAssert.Contains(html, "<h1>Key not accepted</h1>");
                Assert.IsFalse(Target().Matches(html).Any(m => m.Groups[1].Value.Contains("/k/", StringComparison.Ordinal)), "its links lead to /e without the key");
                StringAssert.Contains(html, "href=\"/e/\"");
            }
            return response.StatusCode;
        }

        Assert.AreEqual(HttpStatusCode.OK, await StatusAsync(allowed.Key));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await StatusAsync(notAllowed.Key));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await StatusAsync(apiOnly.Key));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await StatusAsync(allowed.Key[..^2] + "xx"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await StatusAsync(notAllowed.Key, signedIn), "the address's key decides, not the cookie");
        using (var cookie = await signedIn.GetAsync($"/e/l/{id}/titles", Cancel))
            Assert.AreEqual(HttpStatusCode.OK, cookie.StatusCode, "the cookie still works without a key");

        var settings = shelf.Factory.Services.GetRequiredService<SettingsService>();
        var tenant = new SettingOwner(SettingLevel.Tenant, shelf.TenantId);
        await settings.SaveAsync(tenant, shelf.TenantId, new SettingChanges(new() { [SettingCatalog.OpdsPathKeys] = Json("\"off\"") }), Cancel);
        Assert.AreEqual(HttpStatusCode.Unauthorized, await StatusAsync(allowed.Key), "path keys switched off for the tenant");
        await settings.SaveAsync(tenant, shelf.TenantId, new SettingChanges(new() { [SettingCatalog.OpdsPathKeys] = null }), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, await StatusAsync(allowed.Key), "on again");

        await settings.SaveAsync(tenant, shelf.TenantId, new SettingChanges(new() { [SettingCatalog.EReaderEnabled] = Json("\"off\"") }), Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, await StatusAsync(allowed.Key), "the e-reader switched off for the tenant");
        await settings.SaveAsync(tenant, shelf.TenantId, new SettingChanges(new() { [SettingCatalog.EReaderEnabled] = null }), Cancel);

        using (var web = await ApiClients.SignInAsync(shelf.Factory, "reader", ApiClients.Password, Cancel))
        using (var revoke = await web.DeleteAsync($"/api/account/api-keys/{allowed.Info.Id}", Cancel))
            Assert.IsTrue(revoke.IsSuccessStatusCode, $"{revoke.StatusCode} {await revoke.Content.ReadAsStringAsync(Cancel)}");
        Assert.AreEqual(HttpStatusCode.Unauthorized, await StatusAsync(allowed.Key), "revoked");
    }

    /// <summary>
    /// A browser that keeps some cookies may still choose its device under the key: the form posts under the prefix
    /// (with its token) and comes back under it.
    /// </summary>
    [TestMethod]
    public async Task TheDeviceChooserComesBackUnderTheKey()
    {
        await using var shelf = await ShelfAsync();
        var key = (await KeyAsync(shelf, pathAllowed: true)).Key;
        using var browser = EReaderTests.Browser(shelf.Factory);
        var prefix = $"/e/k/{key}";
        var back = $"/e/l/{shelf.LibraryId}/titles";

        var form = await KeyPageAsync(browser, $"{prefix}/device?return={Uri.EscapeDataString(back)}", prefix);
        StringAssert.Contains(form, $"action=\"{prefix}/device\"");
        StringAssert.Contains(form, $"value=\"{back}\"", "the return address never holds the key");
        var token = await EReaderTests.FormTokenAsync(browser, $"{prefix}/device", Cancel);
        using var chosen = await browser.PostAsync(prefix + "/device", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["device"] = "kobo", ["return"] = back, ["__RequestVerificationToken"] = token,
        }), Cancel);

        Assert.AreEqual(HttpStatusCode.Redirect, chosen.StatusCode, await chosen.Content.ReadAsStringAsync(Cancel));
        Assert.AreEqual($"{prefix}/l/{shelf.LibraryId}/titles", chosen.Headers.Location?.OriginalString);
    }

    /// <summary>Where a page leads: links, pictures, the stylesheet and forms.</summary>
    [GeneratedRegex(@"(?:href|src|action)=""([^""]*)""")]
    private static partial Regex Target();

    [GeneratedRegex(@"href=""([^""]*/l/\d+/books/\d+)""")]
    private static partial Regex BookHref();

    [GeneratedRegex(@"href=""([^""]*/files/\d+)""")]
    private static partial Regex FileHref();

    [GeneratedRegex(@"<img class=""cover-m"" src=""([^""]+)""")]
    private static partial Regex CoverSrc();
}
