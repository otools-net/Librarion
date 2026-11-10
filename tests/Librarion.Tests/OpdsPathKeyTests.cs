using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using Librarion.Api.Endpoints;
using Librarion.Api.Opds;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Settings;
using Librarion.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Librarion.Tests;

/// <summary>
/// A key in the catalog's address (<c>/opds/k/&lt;key&gt;/…</c>, agents/docs/features/opds-auth.md): only for keys that
/// allow it, while the tenant allows it; every link keeps the prefix; the key is never in a log.
/// </summary>
[TestClass]
public sealed class OpdsPathKeyTests
{
    private static readonly XNamespace Atom = OpdsFeed.AtomNamespace;

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private const string Book = """
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0"><description><title-info>
        <author><first-name>Ann</first-name><last-name>Writer</last-name></author><book-title>The Hobbit</book-title><lang>en</lang>
        </title-info></description><body><section><p>The text.</p></section></body></FictionBook>
        """;

    private async Task<Shelf> ShelfAsync(Action<IServiceCollection>? services = null)
    {
        var shelf = await Shelf.CreateAsync(Cancel, services);
        var groups = await shelf.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        shelf.Write("The Hobbit.fb2", Book);
        await shelf.ScanAsync(Cancel);
        return shelf;
    }

    /// <summary>A key of "reader", an account of the shelf's tenant (a server administrator's keys follow the server's settings).</summary>
    private async Task<NewApiKey> KeyAsync(Shelf shelf, bool pathAllowed, params string[] scopes)
    {
        using var web = await ApiClients.SignInAsync(shelf.Factory, "reader", ApiClients.Password, Cancel);
        using var created = await web.PostAsJsonAsync("/api/account/api-keys",
            new ApiKeyInput("App", scopes.Length == 0 ? [ApiKeyScopes.Opds] : scopes, PathAllowed: pathAllowed), ApiClients.Json, Cancel);
        Assert.IsTrue(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Cancel));
        return (await created.Content.ReadFromJsonAsync<NewApiKey>(ApiClients.Json, Cancel))!;
    }

    /// <summary>A reader app with nothing but the address: no cookie, no header.</summary>
    private static HttpClient Bare(Shelf shelf) => shelf.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

    [TestMethod]
    public async Task AKeyThatAllowsItOpensTheCatalogFromItsAddressAndEveryLinkKeepsIt()
    {
        await using var shelf = await ShelfAsync();
        var key = (await KeyAsync(shelf, pathAllowed: true)).Key;
        using var app = Bare(shelf);
        var prefix = $"/opds/k/{key}";

        using var response = await app.GetAsync(prefix, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var catalog = XDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).Root!;
        var hrefs = catalog.Descendants(Atom + "link").Select(l => l.Attribute("href")!.Value).ToList();
        Assert.IsNotEmpty(hrefs);
        Assert.IsTrue(hrefs.All(h => h.StartsWith(prefix + "/", StringComparison.Ordinal) || h == prefix), string.Join("\n", hrefs));

        // Down to a book's file, always by the feed's own links.
        var titles = catalog.Descendants(Atom + "link").Single(l => l.Attribute("href")!.Value.EndsWith("/titles", StringComparison.Ordinal)).Attribute("href")!.Value;
        var books = XDocument.Parse(await app.GetStringAsync(titles, Cancel)).Root!;
        var file = books.Descendants(Atom + "link").First(l => (string?)l.Attribute("rel") == OpdsFeed.AcquisitionRel).Attribute("href")!.Value;
        StringAssert.StartsWith(file, prefix + "/l/");
        using var download = await app.GetAsync(file, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, download.StatusCode);
        StringAssert.Contains(await download.Content.ReadAsStringAsync(Cancel), "The text.");

        using var unknown = await app.GetAsync(prefix + "/nothing-here", Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    /// <summary>
    /// Refused (401, no Basic challenge: a password would not help): a key that doesn't allow the address, one only for
    /// the API, a revoked one, a wrong one, any key while the tenant has switched path keys off; the address's key
    /// decides even when a valid Basic comes with it.
    /// </summary>
    [TestMethod]
    public async Task OnlyAKeyAllowedInTheAddressWorksThere()
    {
        await using var shelf = await ShelfAsync();
        var allowed = await KeyAsync(shelf, pathAllowed: true);
        var notAllowed = await KeyAsync(shelf, pathAllowed: false);
        var apiOnly = await KeyAsync(shelf, true, ApiKeyScopes.ApiRead);
        using var app = Bare(shelf);

        async Task<HttpStatusCode> StatusAsync(string key, HttpClient? client = null)
        {
            using var response = await (client ?? app).GetAsync($"/opds/k/{key}", Cancel);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                Assert.AreEqual(OpdsAuthenticationHandler.PathKeyChallenge, response.Headers.WwwAuthenticate.ToString());
            return response.StatusCode;
        }

        Assert.AreEqual(HttpStatusCode.OK, await StatusAsync(allowed.Key));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await StatusAsync(notAllowed.Key));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await StatusAsync(apiOnly.Key));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await StatusAsync(allowed.Key[..^2] + "xx"));
        using (var withBasic = OpdsTests.Reader(shelf.Factory, "reader", ApiClients.Password))
            Assert.AreEqual(HttpStatusCode.Unauthorized, await StatusAsync(notAllowed.Key, withBasic), "the address's key decides");

        var settings = shelf.Factory.Services.GetRequiredService<SettingsService>();
        var tenant = new SettingOwner(SettingLevel.Tenant, shelf.TenantId);
        await settings.SaveAsync(tenant, shelf.TenantId, new SettingChanges(new() { [SettingCatalog.OpdsPathKeys] = Json("\"off\"") }), Cancel);
        Assert.AreEqual(HttpStatusCode.Unauthorized, await StatusAsync(allowed.Key), "switched off for the tenant");
        await settings.SaveAsync(tenant, shelf.TenantId, new SettingChanges(new() { [SettingCatalog.OpdsPathKeys] = null }), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, await StatusAsync(allowed.Key), "on again");

        using (var web = await ApiClients.SignInAsync(shelf.Factory, "reader", ApiClients.Password, Cancel))
        using (var revoke = await web.DeleteAsync($"/api/account/api-keys/{allowed.Info.Id}", Cancel))
            Assert.IsTrue(revoke.IsSuccessStatusCode, $"{revoke.StatusCode} {await revoke.Content.ReadAsStringAsync(Cancel)}");
        Assert.AreEqual(HttpStatusCode.Unauthorized, await StatusAsync(allowed.Key), "revoked");
    }

    /// <summary>
    /// Every log entry (every category, Trace and up, with its values and scopes, the hosting layer's request lines
    /// included) is read back: the key's secret is in none, for a feed, a download, a refused key and an unknown address.
    /// </summary>
    [TestMethod]
    public async Task TheKeyInTheAddressIsNeverInALog()
    {
        var capture = new CapturingLogs();
        await using var shelf = await ShelfAsync(services =>
        {
            services.AddSingleton<ILoggerProvider>(capture);
            services.Configure<LoggerFilterOptions>(options => options.Rules.Add(new LoggerFilterRule(typeof(CapturingLogs).FullName, null, LogLevel.Trace, null)));
        });
        var key = (await KeyAsync(shelf, pathAllowed: true)).Key;
        var refused = (await KeyAsync(shelf, pathAllowed: false)).Key;
        using var app = Bare(shelf);

        foreach (var address in new[] { $"/opds/k/{key}", $"/opds/k/{key}/l/{shelf.LibraryId}/titles?after=x", $"/opds/k/{refused}/l/{shelf.LibraryId}", $"/opds/k/{key}/nothing" })
            using (await app.GetAsync(address, Cancel)) { }

        var lines = capture.Lines.ToList();
        Assert.IsTrue(lines.Any(l => l.Contains("/opds/k/" + LogRedaction.Mask, StringComparison.Ordinal)), "the hosting layer's request lines were captured:\n" + string.Join("\n", lines.Take(20)));
        foreach (var secret in new[] { key, refused }.Select(k => k[(k.LastIndexOf('_') + 1)..]))
            Assert.IsFalse(lines.Any(l => l.Contains(secret, StringComparison.Ordinal)), string.Join("\n", lines.Where(l => l.Contains(secret, StringComparison.Ordinal))));
    }

    [TestMethod]
    [DataRow("GET /opds/k/lib_12_abcDEF-_0123456789abcdefghij/l/3 x", "GET /opds/k/***/l/3 x")]
    [DataRow("/opds/k/not-a-key?x=1", "/opds/k/***?x=1")]
    [DataRow("Bearer lib_7_0123456789abcdefghijKLMN", "Bearer lib_7_***")]
    [DataRow("nothing here", "nothing here")]
    public void TextsAreRedacted(string text, string expected) => Assert.AreEqual(expected, LogRedaction.Redact(text));

    private static System.Text.Json.JsonElement Json(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();

    /// <summary>Every entry as text: the message, each value, and each scope with its values.</summary>
    private sealed class CapturingLogs : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var parts = new List<string> { category, formatter(state, exception) };
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                    parts.AddRange(values.Select(v => $"{v.Key}={v.Value}"));
                owner._scopes.ForEachScope((scope, list) =>
                {
                    list.Add("scope " + scope);
                    if (scope is IEnumerable<KeyValuePair<string, object?>> scopeValues)
                        list.AddRange(scopeValues.Select(v => $"{v.Key}={v.Value}"));
                }, parts);
                owner.Lines.Enqueue(string.Join(" | ", parts));
            }
        }
    }
}
