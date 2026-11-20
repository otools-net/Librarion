using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Librarion.Api.EReader;
using Librarion.Api.Endpoints;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Settings;
using Librarion.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>The e-reader mode under <c>/e</c> (agents/docs/features/ereader-mode.md; stage 8).</summary>
[TestClass]
public sealed partial class EReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    /// <summary>A browser: keeps cookies, follows no redirect (so the test sees them), sends no API header.</summary>
    internal static HttpClient Browser(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    /// <summary>A page of the mode: HTML, its own CSP, the security headers, no script of any kind.</summary>
    internal static async Task<string> PlainPageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var address = response.RequestMessage?.RequestUri?.PathAndQuery;
        Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType, address);
        Assert.AreEqual("utf-8", response.Content.Headers.ContentType?.CharSet, address);
        Assert.AreEqual(SecurityHeaders.EReaderPolicy, string.Join(";", response.Headers.GetValues("Content-Security-Policy")), address);
        Assert.AreEqual("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single(), address);
        StringAssert.StartsWith(html.TrimStart(), "<!DOCTYPE html>", address);
        StringAssert.DoesNotMatch(html, Script(), address);
        Assert.IsFalse(html.Contains("<lib-root>", StringComparison.Ordinal), address);
        Assert.IsFalse(html.Contains("blazor", StringComparison.OrdinalIgnoreCase), address);
        return html;
    }

    // ---- The frame (chunk 1) ----

    [TestMethod]
    public async Task UntilTheSetupAPlainPageSaysSo()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var browser = Browser(factory);

        foreach (var address in new[] { "/e", "/e/", "/e/anything" })
        {
            using var response = await browser.GetAsync(address, Cancel);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, address);
            StringAssert.Contains(await PlainPageAsync(response, Cancel), "Not set up yet", address);
        }
    }

    [TestMethod]
    public async Task AVisitorIsSentToSignInAndBackAfterwards()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        (await ApiClients.SetUpAsync(factory, Cancel)).Client.Dispose();
        using var browser = Browser(factory);

        using var response = await browser.GetAsync("/e/", Cancel);

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/e/signin?return=%2Fe%2F", response.Headers.Location?.OriginalString);
    }

    /// <summary>The app's cookie opens the mode (one sign-in for both): the home page, the only library's page.</summary>
    [TestMethod]
    public async Task TheAppsSessionOpensTheHome()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        var (admin, session) = await ApiClients.SetUpAsync(factory, Cancel);
        using var _ = admin;
        var library = (await factory.Services.GetRequiredService<AccessService>().ListLibrariesAsync(session.Tenant!.Id, Cancel)).First();

        foreach (var address in new[] { "/e", "/e/" })
        {
            using var response = await admin.GetAsync(address, Cancel);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, address);
            var html = await PlainPageAsync(response, Cancel);
            StringAssert.Contains(html, $"<h1>{WebUtility.HtmlEncode(library.Name)}</h1>", address);
            StringAssert.Contains(html, $"action=\"/e/l/{library.Id}/search\"", address);
            StringAssert.Contains(html, $"href=\"/e/ereader.css?v={EReaderStylesheet.Version}\"", address);
        }
    }

    /// <summary>An account without <c>library.browse</c> gets a plain "not allowed" page, not the API's 403.</summary>
    [TestMethod]
    public async Task WithoutTheRightAPlainPageRefuses()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        await ApiClients.CreateUserAsync(admin, new NewUser("nobody", Password: ApiClients.Password), Cancel);
        using var nobody = await ApiClients.SignInAsync(factory, "nobody", ApiClients.Password, Cancel);

        using var response = await nobody.GetAsync("/e/", Cancel);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        StringAssert.Contains(await PlainPageAsync(response, Cancel), "Not allowed");
    }

    /// <summary>An address under <c>/e</c> that is no page is a plain 404 page: never the client's page, file-like or deep.</summary>
    [TestMethod]
    [DataRow("/e/nothing-here")]
    [DataRow("/e/12/Some%20Name")]
    [DataRow("/e/opensearch.xml")]
    [DataRow("/e/k")]
    public async Task AnUnknownAddressIsAPlainNotFound(string address)
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        using var visitor = Browser(factory);

        foreach (var client in new[] { admin, visitor })
        {
            using var response = await client.GetAsync(address, Cancel);

            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, address);
            StringAssert.Contains(await PlainPageAsync(response, Cancel), "Not found", address);
        }
    }

    /// <summary>
    /// <c>ereader.enabled</c> off: every page of the mode is not found, for the tenant's accounts; before anyone signs in
    /// the server's value decides. The app is untouched.
    /// </summary>
    [TestMethod]
    public async Task TheSwitchTurnsTheModeOff()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        var (admin, session) = await ApiClients.SetUpAsync(factory, Cancel);
        using var _ = admin;
        using var visitor = Browser(factory);
        var settings = factory.Services.GetRequiredService<SettingsService>();
        var tenantId = session.Tenant!.Id;
        var tenant = new SettingOwner(SettingLevel.Tenant, tenantId);
        var server = new SettingOwner(SettingLevel.Server, 0);

        async Task<HttpStatusCode> StatusAsync(HttpClient client)
        {
            using var response = await client.GetAsync("/e/", Cancel);
            if (response.StatusCode == HttpStatusCode.NotFound)
                StringAssert.Contains(await PlainPageAsync(response, Cancel), "Not found");
            return response.StatusCode;
        }

        Assert.AreEqual(HttpStatusCode.OK, await StatusAsync(admin), "on by default");
        Assert.IsTrue((await settings.SaveAsync(tenant, tenantId, new SettingChanges(new() { [SettingCatalog.EReaderEnabled] = Json("\"off\"") }), Cancel)).Status == AccessStatus.Ok);
        Assert.AreEqual(HttpStatusCode.NotFound, await StatusAsync(admin), "off for the tenant");
        Assert.AreEqual(HttpStatusCode.Redirect, await StatusAsync(visitor), "a visitor has no tenant yet: the server's value");
        using (var app = await admin.GetAsync("/api/session", Cancel))
            Assert.AreEqual(HttpStatusCode.OK, app.StatusCode, "the app is untouched");

        Assert.IsTrue((await settings.SaveAsync(server, null, new SettingChanges(new() { [SettingCatalog.EReaderEnabled] = Json("\"off\"") }), Cancel)).Status == AccessStatus.Ok);
        Assert.AreEqual(HttpStatusCode.NotFound, await StatusAsync(visitor), "off for the server");

        Assert.IsTrue((await settings.SaveAsync(server, null, new SettingChanges(new() { [SettingCatalog.EReaderEnabled] = null }), Cancel)).Status == AccessStatus.Ok);
        Assert.IsTrue((await settings.SaveAsync(tenant, tenantId, new SettingChanges(new() { [SettingCatalog.EReaderEnabled] = null }), Cancel)).Status == AccessStatus.Ok);
        Assert.AreEqual(HttpStatusCode.OK, await StatusAsync(admin), "on again");
    }

    /// <summary>The stylesheet: served before the setup too (the pages link it), cached for good, 304 on its ETag.</summary>
    [TestMethod]
    public async Task TheStylesheetIsServedAndCached()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var browser = Browser(factory);

        using var response = await browser.GetAsync($"/e/ereader.css?v={EReaderStylesheet.Version}", Cancel);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/css", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(EReaderStylesheet.Text, await response.Content.ReadAsStringAsync(Cancel));
        StringAssert.Contains(response.Headers.CacheControl?.ToString(), "immutable");

        using var again = new HttpRequestMessage(HttpMethod.Get, "/e/ereader.css");
        again.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        using var notModified = await browser.SendAsync(again, Cancel);
        Assert.AreEqual(HttpStatusCode.NotModified, notModified.StatusCode);
    }

    /// <summary>
    /// Grayscale only and nothing that makes the panel redraw (D-8.9): every colour a hex gray (in fills, borders,
    /// gradients and shadows alike), no colour functions or names but black, white and gray; every gradient with a plain
    /// gray fill in the same rule for browsers without gradients; no custom properties (old e-ink WebKit), transitions,
    /// animations, hover or dark theme.
    /// </summary>
    [TestMethod]
    public void TheStylesheetHasNoColour()
    {
        var css = Comment().Replace(File.ReadAllText(RepositoryFiles.PathOf("src/Librarion.Api/EReader/ereader.css")), "");
        Assert.AreEqual(EReaderStylesheet.Text, File.ReadAllText(RepositoryFiles.PathOf("src/Librarion.Api/EReader/ereader.css")), "the file is what is served");

        var hexes = Hex().Matches(css).Select(m => m.Groups[1].Value).ToList();
        Assert.IsNotEmpty(hexes, "the check found the colours");
        foreach (var hex in hexes)
        {
            var full = hex.Length == 3 ? string.Concat(hex.Select(c => new string(c, 2))) : hex;
            Assert.IsTrue(full[0..2] == full[2..4] && full[2..4] == full[4..6], $"#{hex} is not a gray");
        }

        foreach (var forbidden in new[] { "rgb(", "rgba(", "hsl(", "hsla(", "hwb(", "lab(", "lch(", "color(", "color-mix(", "var(", "--",
                     "transition", "animation", ":hover", "prefers-color-scheme", "filter", "@import", "url(", "@font-face" })
            Assert.IsFalse(css.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);

        var gradients = css.Split('}').Where(rule => rule.Contains("gradient(", StringComparison.Ordinal)).ToList();
        Assert.IsNotEmpty(gradients, "the check found the gradients");
        foreach (var rule in gradients)
            StringAssert.Contains(rule, "background-color:", $"a gradient without a plain fill before it:{Environment.NewLine}{rule.Trim()}");

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "black", "white", "gray", "grey", "transparent", "currentcolor", "inherit", "none", "solid", "dashed", "dotted", "double", "medium", "thin",
            "thick", "initial", "underline", "overline", "line-through", "linear-gradient", "repeating-linear-gradient", "radial-gradient", "to", "top", "bottom", "left", "right", "inset",
        };
        foreach (Match declaration in ColourDeclaration().Matches(css))
            foreach (var word in Word().Matches(declaration.Groups[2].Value).Select(w => w.Value).Where(w => !allowed.Contains(w)))
                Assert.Fail($"{declaration.Value.Trim()}: \"{word}\" may be a colour");
    }

    /// <summary>Every text key the mode's pages and code use is in <c>en.json</c> (they are the app's texts, D-8.10).</summary>
    [TestMethod]
    public void EveryTextKeyTheModeUsesExists()
    {
        var texts = new ServerTexts();
        var folder = RepositoryFiles.PathOf("src/Librarion.Api/EReader");
        var used = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".razor", StringComparison.Ordinal) || p.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(p => TextKey().Matches(File.ReadAllText(p))
                // In the pages also a key written whole as a string (a list of them, SearchHelp); the code has setting keys so.
                .Concat(p.EndsWith(".razor", StringComparison.Ordinal) ? KeyLiteral().Matches(File.ReadAllText(p)) : [])
                .Select(m => (File: Path.GetFileName(p), Key: m.Groups[1].Value)))
            .Distinct()
            .ToList();

        Assert.IsGreaterThan(10, used.Count, "the check found the keys");
        var missing = used.Where(u => !texts.Has(u.Key)).Select(u => $"{u.File}: {u.Key}").Distinct().ToList();
        Assert.IsEmpty(missing, "Missing from src/i18n/en.json:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    // ---- Sign in and out (chunk 2) ----

    /// <summary>The page's form token (the hidden field), as a browser would send it back.</summary>
    internal static async Task<string> FormTokenAsync(HttpClient browser, string address, CancellationToken cancellationToken)
    {
        using var page = await browser.GetAsync(address, cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, address);
        var match = TokenField().Match(await page.Content.ReadAsStringAsync(cancellationToken));
        Assert.IsTrue(match.Success, $"{address} has a form token");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>Posts the sign-in form as a browser does (its token from the page first, unless <paramref name="token"/> says otherwise).</summary>
    internal static async Task<HttpResponseMessage> SignInAsync(HttpClient browser, string userName, string password, CancellationToken cancellationToken,
        bool remember = true, string returnTo = "/e/", string? token = null)
    {
        token ??= await FormTokenAsync(browser, "/e/signin", cancellationToken);
        var fields = new Dictionary<string, string> { ["userName"] = userName, ["password"] = password, ["return"] = returnTo };
        if (token.Length > 0)
            fields["__RequestVerificationToken"] = token;
        if (remember)
            fields["remember"] = "on";
        return await browser.PostAsync("/e/signin", new FormUrlEncodedContent(fields), cancellationToken);
    }

    /// <summary>A browser signed in through the e-reader's own form.</summary>
    internal static async Task<HttpClient> SignedInBrowserAsync(WebApplicationFactory<Program> factory, string userName, CancellationToken cancellationToken)
    {
        var browser = Browser(factory);
        using var response = await SignInAsync(browser, userName, ApiClients.Password, cancellationToken);
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        return browser;
    }

    /// <summary>The form: the app's field names, "Stay signed in" ticked to begin with (Q6), a token, the return address kept.</summary>
    [TestMethod]
    public async Task TheSignInPageIsAPlainForm()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        (await ApiClients.SetUpAsync(factory, Cancel)).Client.Dispose();
        using var browser = Browser(factory);

        using var response = await browser.GetAsync("/e/signin?return=%2Fe%2F%3Fq%3Dx", Cancel);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await PlainPageAsync(response, Cancel);
        StringAssert.Contains(html, "method=\"post\" action=\"/e/signin\">");
        StringAssert.Contains(html, "name=\"userName\"");
        StringAssert.Contains(html, "type=\"password\"");
        StringAssert.Matches(html, new Regex("<input type=\"checkbox\" name=\"remember\" value=\"on\" checked"));
        StringAssert.Contains(html, "name=\"return\" value=\"/e/?q=x\"");
        StringAssert.Matches(html, TokenField());
        Assert.IsTrue(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("librarion.antiforgery=", StringComparison.Ordinal)));
        Assert.IsFalse(html.Contains("Sign out", StringComparison.Ordinal), "nobody to sign out");
    }

    /// <summary>
    /// The right password signs in with the app's cookie (one sign-in for both), logged; back to the return address;
    /// "Stay signed in" makes the cookie outlive the browser, unticked it doesn't.
    /// </summary>
    [TestMethod]
    public async Task TheRightPasswordSignsInAndGoesBack()
    {
        using var catalog = new TestCatalog();
        var (factory, admin, _) = await OpdsTests.ServerWithReaderAsync(catalog, Cancel);
        await using var _f = factory;
        using var _a = admin;

        foreach (var remember in new[] { true, false })
        {
            using var browser = Browser(factory);
            using var response = await SignInAsync(browser, "reader", ApiClients.Password, Cancel, remember, returnTo: "/e/?from=test");

            Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
            Assert.AreEqual("/e/?from=test", response.Headers.Location?.OriginalString);
            var session = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(LibrarionSession.CookieName, StringComparison.Ordinal));
            Assert.AreEqual(remember, session.Contains("expires=", StringComparison.OrdinalIgnoreCase), $"remember {remember}: {session}");

            using var home = await browser.GetAsync("/e/", Cancel);
            Assert.AreEqual(HttpStatusCode.OK, home.StatusCode);
            var html = await PlainPageAsync(home, Cancel);
            StringAssert.Contains(html, "<span class=\"muted\">reader</span>");
            StringAssert.Contains(html, "action=\"/e/signout\"");
            using var app = await browser.GetAsync("/api/session", Cancel);
            StringAssert.Contains(await app.Content.ReadAsStringAsync(Cancel), "\"reader\"", "the app's session too");

            using var again = await browser.GetAsync("/e/signin?return=%2Fe", Cancel);
            Assert.AreEqual(HttpStatusCode.Redirect, again.StatusCode, "already signed in: on to the return address");
            Assert.AreEqual("/e", again.Headers.Location?.OriginalString);
        }

        var events = await factory.Services.GetRequiredService<SecurityLog>().ListAsync(new SecurityEventQuery(null, ServerWide: true, UserName: "reader"), Cancel);
        Assert.AreEqual(2, events.Count(e => e.Kind == SecurityEventKind.SignedIn));
    }

    /// <summary>
    /// A wrong password or a disabled account: the form again (400) with the name kept and never the password, logged;
    /// five wrong ones from an address lock it out, the right password too, with the wait in minutes.
    /// </summary>
    [TestMethod]
    public async Task WrongPasswordsAreRefusedLoggedAndLockedOut()
    {
        using var catalog = new TestCatalog();
        var (factory, admin, _) = await OpdsTests.ServerWithReaderAsync(catalog, Cancel);
        await using var _f = factory;
        using var _a = admin;
        await ApiClients.CreateUserAsync(admin, new NewUser("gone", Password: ApiClients.Password, Disabled: true), Cancel);
        using var browser = Browser(factory);

        using (var disabled = await SignInAsync(browser, "gone", ApiClients.Password, Cancel))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, disabled.StatusCode);
            StringAssert.Contains(await PlainPageAsync(disabled, Cancel), "Wrong name or password.", "a disabled account is not told apart");
        }

        for (var i = 0; i < 4; i++)
        {
            using var refused = await SignInAsync(browser, "reader", "not the password", Cancel);
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
            var html = await PlainPageAsync(refused, Cancel);
            StringAssert.Contains(html, "Wrong name or password.");
            StringAssert.Contains(html, "value=\"reader\"");
            Assert.IsFalse(html.Contains("not the password", StringComparison.Ordinal), "the password is never sent back");
            Assert.IsFalse(refused.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith(LibrarionSession.CookieName, StringComparison.Ordinal)));
        }
        using (var fifth = await SignInAsync(browser, "reader", "not the password", Cancel))
            StringAssert.Contains(await PlainPageAsync(fifth, Cancel), "Too many wrong passwords: try again in");
        using (var locked = await SignInAsync(browser, "reader", ApiClients.Password, Cancel))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, locked.StatusCode, "the address waits, even with the right password");
            StringAssert.Contains(await PlainPageAsync(locked, Cancel), "Too many wrong passwords: try again in");
        }

        var events = await factory.Services.GetRequiredService<SecurityLog>().ListAsync(new SecurityEventQuery(null, ServerWide: true), Cancel);
        Assert.AreEqual(4, events.Count(e => e is { Kind: SecurityEventKind.SignInFailed, UserName: "reader" }));
        Assert.IsGreaterThanOrEqualTo(1, events.Count(e => e is { Kind: SecurityEventKind.LockedOut, UserName: "reader" }));
        Assert.AreEqual(1, events.Count(e => e is { Kind: SecurityEventKind.SignInFailed, UserName: "gone", Detail: "disabled" }));
    }

    /// <summary>
    /// Every <c>/e</c> post needs the page's token (D-8.4): without it, or with a made-up one, a plain 400 page and nothing
    /// done: not signed in with the right password, not signed out.
    /// </summary>
    [TestMethod]
    public async Task APostWithoutTheTokenIsRefused()
    {
        using var catalog = new TestCatalog();
        var (factory, admin, _) = await OpdsTests.ServerWithReaderAsync(catalog, Cancel);
        await using var _f = factory;
        using var _a = admin;

        foreach (var token in new[] { "", "CfDJ8made-up" })
        {
            using var browser = Browser(factory);
            await FormTokenAsync(browser, "/e/signin", Cancel);
            using var response = await SignInAsync(browser, "reader", ApiClients.Password, Cancel, token: token);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, token);
            StringAssert.Contains(await PlainPageAsync(response, Cancel), "The form has expired", token);
            Assert.IsFalse(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith(LibrarionSession.CookieName, StringComparison.Ordinal)), token);
        }

        using var signedIn = await SignedInBrowserAsync(factory, "reader", Cancel);
        using (var refused = await signedIn.PostAsync("/e/signout", new FormUrlEncodedContent(new Dictionary<string, string>()), Cancel))
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        using var still = await signedIn.GetAsync("/e/", Cancel);
        Assert.AreEqual(HttpStatusCode.OK, still.StatusCode, "still signed in");
    }

    /// <summary>Sign out is a form on every page; afterwards the pages send to the sign-in page again; logged.</summary>
    [TestMethod]
    public async Task SignOutEndsTheSession()
    {
        using var catalog = new TestCatalog();
        var (factory, admin, _) = await OpdsTests.ServerWithReaderAsync(catalog, Cancel);
        await using var _f = factory;
        using var _a = admin;
        using var browser = await SignedInBrowserAsync(factory, "reader", Cancel);
        var token = await FormTokenAsync(browser, "/e/", Cancel);

        using var response = await browser.PostAsync("/e/signout",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), Cancel);

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/e/signin", response.Headers.Location?.OriginalString);
        using var home = await browser.GetAsync("/e/", Cancel);
        Assert.AreEqual(HttpStatusCode.Redirect, home.StatusCode, "signed out");
        var events = await factory.Services.GetRequiredService<SecurityLog>().ListAsync(new SecurityEventQuery(null, ServerWide: true, UserName: "reader"), Cancel);
        Assert.AreEqual(1, events.Count(e => e.Kind == SecurityEventKind.SignedOut));
    }

    /// <summary>The return address is a page of the mode, or the home page: never another site, never the sign-in itself.</summary>
    [TestMethod]
    [DataRow(null, "/e/")]
    [DataRow("", "/e/")]
    [DataRow("/e", "/e")]
    [DataRow("/e/", "/e/")]
    [DataRow("/e/books/12?x=1", "/e/books/12?x=1")]
    [DataRow("/e?x=1", "/e?x=1")]
    [DataRow("//evil.example/e/", "/e/")]
    [DataRow("/e//evil.example", "/e/")]
    [DataRow("/e/\\evil.example", "/e/")]
    [DataRow("https://evil.example/e/", "/e/")]
    [DataRow("/api/session", "/e/")]
    [DataRow("/eve", "/e/")]
    [DataRow("/e/signin", "/e/")]
    [DataRow("/e/signin/?return=/e/", "/e/")]
    [DataRow("/e/signout", "/e/")]
    [DataRow("/e/\r\nSet-Cookie: x", "/e/")]
    public void TheReturnAddressStaysInTheMode(string? value, string expected) =>
        Assert.AreEqual(expected, EReaderEndpoints.SafeReturn(value));

    [GeneratedRegex("<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenField();

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [GeneratedRegex(@"<script|\son[a-z]+\s*=|javascript:", RegexOptions.IgnoreCase)]
    private static partial Regex Script();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})\b")]
    private static partial Regex Hex();

    /// <summary>Declarations that can hold a colour: <c>color</c>, <c>background</c>, <c>border…</c>, <c>outline…</c>, <c>…-color</c>.</summary>
    [GeneratedRegex(@"(?<![\w-])((?:background|border|outline|color|fill|stroke|caret-color|accent-color|text-decoration|column-rule)[\w-]*)\s*:\s*([^;}]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ColourDeclaration();

    /// <summary>A bare word (not a number with its unit, not a hex colour).</summary>
    [GeneratedRegex(@"(?<![#\w.-])[a-zA-Z][a-zA-Z-]*")]
    private static partial Regex Word();

    /// <summary><c>Texts["key"]</c> or <c>Texts.Format("key", …)</c> in the pages, <c>texts[…]</c> in the code.</summary>
    [GeneratedRegex(@"[Tt]exts(?:\[|\.Format\()""([a-zA-Z][\w-]*(?:\.[\w-]+)+)""")]
    private static partial Regex TextKey();

    /// <summary>A whole key as a string in a page: <c>"search.help.words"</c> (lower case first, dots between words).</summary>
    [GeneratedRegex(@"""([a-z][a-zA-Z]*(?:\.[a-zA-Z][\w-]*)+)""")]
    private static partial Regex KeyLiteral();
}
