using System.Net;
using System.Text.Json;
using Librarion.Api.EReader;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Settings;
using Librarion.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>
/// The ways into the e-reader mode (stage 8 chunk 9, D-8.11): e-ink browsers sent from the app's pages to <c>/e</c> by
/// the server, a book's address to its page; the Full site cookie that stops it and the way back; never for the API,
/// the catalog, files or covers.
/// </summary>
[TestClass]
public sealed class EReaderWaysInTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    internal const string Kindle = "Mozilla/5.0 (X11; U; Linux armv7l like Android; en-us) AppleWebKit/531.2+ (KHTML, like Gecko) Version/5.0 Safari/533.2+ Kindle/3.0+";
    internal const string Kobo = "Mozilla/5.0 (Linux; U; Android 2.0; en-us;) AppleWebKit/538.1 (KHTML, like Gecko) Version/4.0 Mobile Safari/538.1 (Kobo Touch 0377/4.38.21908)";
    internal const string PocketBook = "Mozilla/5.0 (Linux; Ubuntu 14.04) AppleWebKit/537.36 Chromium/57.0.2950.0 Safari/537.36 PocketBook/740";
    internal const string Tolino = "Mozilla/5.0 (Linux; Android 4.4.2; tolino vision 4 HD Build/KOT49H) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/30.0.0.0 Safari/537.36";
    internal const string Boox = "Mozilla/5.0 (Linux; Android 11; NoteAir2P Build/RKQ1.210107.001; BOOX) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/87.0.4280.141 Safari/537.36";
    private const string Desktop = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    private const string FireTablet = "Mozilla/5.0 (Linux; Android 9; KFTRWI) AppleWebKit/537.36 (KHTML, like Gecko) Silk/118.3.1 like Chrome/118.0.5993.144 Safari/537.36 Kindle/1.0";

    /// <summary>A browser on <paramref name="userAgent"/>: keeps cookies, follows no redirect.</summary>
    private static HttpClient Device(WebApplicationFactory<Program> factory, string? userAgent)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        if (userAgent is not null)
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        return client;
    }

    private async Task<string?> RedirectOfAsync(HttpClient client, string address)
    {
        using var response = await client.GetAsync(address, Cancel);
        return response.StatusCode == HttpStatusCode.Redirect ? response.Headers.Location?.OriginalString : null;
    }

    /// <summary>Each e-ink family is sent to <c>/e/</c> from any page of the app, a book's address to its own; phones, desktops, a Fire tablet and no User-Agent are not.</summary>
    [TestMethod]
    [DataRow(Kindle)]
    [DataRow(Kobo)]
    [DataRow(PocketBook)]
    [DataRow(Tolino)]
    [DataRow(Boox)]
    public async Task EInkBrowsersAreSentToTheEReader(string userAgent)
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var _ = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        using var device = Device(factory, userAgent);

        Assert.AreEqual("/e/", await RedirectOfAsync(device, "/"));
        Assert.AreEqual("/e/", await RedirectOfAsync(device, "/login?return=%2Fsearch"));
        Assert.AreEqual("/e/", await RedirectOfAsync(device, "/search?q=dune"));
        Assert.AreEqual("/e/", await RedirectOfAsync(device, "/tag/12/Vol.%202"), "a tag's readable address");
        Assert.AreEqual("/e/books/42", await RedirectOfAsync(device, "/books/42"), "a book's address to its page");
        Assert.AreEqual("/e/", await RedirectOfAsync(device, "/books/x"));
    }

    [TestMethod]
    [DataRow(Desktop)]
    [DataRow(FireTablet)]
    [DataRow(null)]
    public async Task OtherBrowsersStayOnTheApp(string? userAgent)
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var _ = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        using var browser = Device(factory, userAgent);

        foreach (var address in new[] { "/", "/search", "/books/42" })
            Assert.IsNull(await RedirectOfAsync(browser, address), address);
    }

    /// <summary>Never the API, the catalog, the e-reader's own files, static files, covers or downloads; never a POST.</summary>
    [TestMethod]
    public async Task OnlyTheAppsPagesAreRedirected()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var _ = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        using var kindle = Device(factory, Kindle);

        foreach (var address in new[] { "/api/session", "/api/nothing", "/api/libraries/1/books/1/files/1", "/api/libraries/1/books/1/cover/s", "/opds", "/opds/l/1",
                     "/e/ereader.css", "/favicon.ico", "/main.js", "/openapi/v1.json" })
        {
            var to = await RedirectOfAsync(kindle, address);
            Assert.IsFalse(to?.StartsWith("/e", StringComparison.Ordinal) == true && !address.StartsWith("/e", StringComparison.Ordinal), $"{address} → {to}");
        }
        using var posted = await kindle.PostAsync("/books/42", new StringContent(""), Cancel);
        Assert.AreNotEqual(HttpStatusCode.Redirect, posted.StatusCode);
    }

    /// <summary>
    /// Full site (a link on every e-reader page) keeps this browser on the app for a year; the app's link to the e-reader
    /// (<c>/e/start</c>) lets it be sent there again.
    /// </summary>
    [TestMethod]
    public async Task FullSiteStopsTheRedirectAndTheWayBackRestoresIt()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var _ = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        using var kindle = Device(factory, Kindle);

        using (var signIn = await kindle.GetAsync("/e/signin", Cancel))
            StringAssert.Contains(await EReaderTests.PlainPageAsync(signIn, Cancel), "<a class=\"muted\" href=\"/e/full-site\">Full site</a>", "on every page, the sign-in page too");

        using (var full = await kindle.GetAsync("/e/full-site", Cancel))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, full.StatusCode);
            Assert.AreEqual("/", full.Headers.Location?.OriginalString);
            var cookie = full.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(EReaderRedirect.FullSiteCookie + "=", StringComparison.Ordinal));
            StringAssert.Contains(cookie, "path=/;");
            StringAssert.Contains(cookie, "httponly");
            StringAssert.Contains(cookie, "samesite=lax");
            var expires = DateTimeOffset.Parse(System.Text.RegularExpressions.Regex.Match(cookie, "expires=([^;]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsGreaterThan(DateTimeOffset.UtcNow.AddDays(360), expires, "a year");
        }
        Assert.IsNull(await RedirectOfAsync(kindle, "/"), "the full site chosen");
        Assert.IsNull(await RedirectOfAsync(kindle, "/books/42"));

        using (var back = await kindle.GetAsync("/e/start", Cancel))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, back.StatusCode);
            Assert.AreEqual("/e/", back.Headers.Location?.OriginalString);
        }
        Assert.AreEqual("/e/", await RedirectOfAsync(kindle, "/"), "sent again");
        using (var again = await kindle.GetAsync("/e/start", Cancel))
            Assert.IsFalse(again.Headers.Contains("Set-Cookie") && again.Headers.GetValues("Set-Cookie").Any(c => c.StartsWith(EReaderRedirect.FullSiteCookie, StringComparison.Ordinal)),
                "nothing to clear: no cookie written");
    }

    /// <summary>No redirect with the mode switched off (the tenant's for its accounts, the server's before signing in), nor before the setup.</summary>
    [TestMethod]
    public async Task NoRedirectWhileTheModeIsOffOrTheServerNotSetUp()
    {
        using (var fresh = new TestCatalog())
        {
            await using var waiting = ApiClients.CreateFactory(fresh);
            using var device = Device(waiting, Kindle);
            Assert.IsNull(await RedirectOfAsync(device, "/"), "the app's setup page");
        }

        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        var (admin, session) = await ApiClients.SetUpAsync(factory, Cancel);
        using var _ = admin;
        using var kindle = Device(factory, Kindle);
        var settings = factory.Services.GetRequiredService<SettingsService>();
        var off = new SettingChanges(new() { [SettingCatalog.EReaderEnabled] = JsonDocument.Parse("\"off\"").RootElement.Clone() });
        var reset = new SettingChanges(new() { [SettingCatalog.EReaderEnabled] = null });

        await settings.SaveAsync(new SettingOwner(SettingLevel.Server, 0), null, off, Cancel);
        Assert.IsNull(await RedirectOfAsync(kindle, "/"), "off for the server, before signing in");
        await settings.SaveAsync(new SettingOwner(SettingLevel.Server, 0), null, reset, Cancel);
        Assert.AreEqual("/e/", await RedirectOfAsync(kindle, "/"));

        // Signed in (on the e-reader's form), the tenant's value decides.
        using (var signedIn = await EReaderTests.SignInAsync(kindle, "admin", ApiClients.Password, Cancel))
            Assert.AreEqual(HttpStatusCode.Redirect, signedIn.StatusCode);
        await settings.SaveAsync(new SettingOwner(SettingLevel.Tenant, session.Tenant!.Id), session.Tenant.Id, off, Cancel);
        Assert.IsNull(await RedirectOfAsync(kindle, "/"), "off for the tenant");
    }

    /// <summary><c>/e/books/&lt;id&gt;</c> (where a book's app address leads): on to its library's page; a hidden or unknown book, not found.</summary>
    [TestMethod]
    public async Task ABookByItsIdGoesToItsLibrarysPage()
    {
        await using var library = await TestLibrary.CreateAsync(Cancel);
        library.Write("Dune.fb2", OpdsFeedTests.Fb2("Dune"));
        await library.ScanAsync(Cancel);
        int id;
        await using (var db = await library.Factory.Services.GetRequiredService<IDbContextFactory<LibrarionDbContext>>().CreateDbContextAsync(Cancel))
            id = await db.Books.IgnoreQueryFilters().Select(b => b.Id).SingleAsync(Cancel);
        using var visitor = Device(library.Factory, Kindle);
        using var reader = await EReaderTests.SignedInBrowserAsync(library.Factory, "admin", Cancel);

        Assert.AreEqual($"/e/books/{id}", await RedirectOfAsync(visitor, $"/books/{id}"));
        StringAssert.StartsWith(await RedirectOfAsync(visitor, $"/e/books/{id}"), "/e/signin?return=", "a visitor signs in first");
        Assert.AreEqual($"/e/l/{library.LibraryId}/books/{id}", await RedirectOfAsync(reader, $"/e/books/{id}"));
        using var unknown = await reader.GetAsync($"/e/books/{id + 1000}", Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
