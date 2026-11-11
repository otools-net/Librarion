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

    /// <summary>The app's cookie opens the mode (one sign-in for both): the home page with the libraries one may browse.</summary>
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
            StringAssert.Contains(html, "<h1>Library</h1>", address);
            StringAssert.Contains(html, $"<li>{WebUtility.HtmlEncode(library.Name)}</li>", address);
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
    /// Grayscale only and nothing that smears on e-ink (D-8.9): every colour a hex gray, no colour functions or names
    /// but black, white and gray, no shadows, gradients, transitions, animations, hover or dark theme.
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

        foreach (var forbidden in new[] { "rgb(", "rgba(", "hsl(", "hsla(", "hwb(", "lab(", "lch(", "color(", "color-mix(", "gradient(", "shadow",
                     "transition", "animation", ":hover", "prefers-color-scheme", "filter", "@import", "url(", "@font-face" })
            Assert.IsFalse(css.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "black", "white", "gray", "grey", "transparent", "currentcolor", "inherit", "none", "solid", "dashed", "dotted", "double", "medium", "thin", "thick", "initial" };
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
            .SelectMany(p => TextKey().Matches(File.ReadAllText(p)).Select(m => (File: Path.GetFileName(p), Key: m.Groups[1].Value)))
            .ToList();

        Assert.IsGreaterThan(10, used.Count, "the check found the keys");
        var missing = used.Where(u => !texts.Has(u.Key)).Select(u => $"{u.File}: {u.Key}").Distinct().ToList();
        Assert.IsEmpty(missing, "Missing from src/i18n/en.json:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

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

    /// <summary><c>Texts["key"]</c> in the pages, <c>texts["key"]</c> in the code.</summary>
    [GeneratedRegex(@"[Tt]exts\[""([a-zA-Z][\w-]*(?:\.[\w-]+)+)""\]")]
    private static partial Regex TextKey();
}
