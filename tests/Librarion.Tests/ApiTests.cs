using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Librarion.Api;
using Librarion.Api.Endpoints;
using Librarion.Api.Security;
using Librarion.Tests.Infrastructure;

namespace Librarion.Tests;

[TestClass]
public sealed class ApiTests
{
    public TestContext TestContext { get; set; } = null!;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [TestMethod]
    public async Task SystemInfoTellsTheVersionAndTheDatabase()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var client = ApiClients.Create(factory);

        var info = await client.GetFromJsonAsync<SystemInfo>("/api/system/info", Json, TestContext.CancellationToken);

        Assert.IsNotNull(info);
        Assert.AreEqual(AppVersion.Version, info.Version);
        Assert.AreEqual(TestCatalog.Provider, info.Database);
    }

    [TestMethod]
    public async Task EveryAnswerCarriesTheSecurityHeaders()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var client = ApiClients.Create(factory);

        foreach (var path in new[] { "/api/system/info", "/api/nothing-here", "/openapi/v1.json" })
        {
            using var response = await client.GetAsync(path, TestContext.CancellationToken);
            Assert.AreEqual(SecurityHeaders.AppPolicy, string.Join(";", response.Headers.GetValues("Content-Security-Policy")), path);
            Assert.AreEqual("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single(), path);
            Assert.AreEqual("DENY", response.Headers.GetValues("X-Frame-Options").Single(), path);
            Assert.AreEqual("same-origin", response.Headers.GetValues("Referrer-Policy").Single(), path);
        }
    }

    [TestMethod]
    public async Task ARequestChangingSomethingNeedsTheHeader()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var bare = factory.CreateClient();

        using var refused = await bare.PostAsync("/api/system/info", null, TestContext.CancellationToken);
        using var reading = await bare.GetAsync("/api/system/info", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        StringAssert.Contains(await refused.Content.ReadAsStringAsync(TestContext.CancellationToken), RequestGuard.Header);
        Assert.AreEqual(HttpStatusCode.OK, reading.StatusCode);
    }

    [TestMethod]
    public async Task AnUnknownApiRouteIsNotFoundRatherThanThePage()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var client = ApiClients.Create(factory);

        using var response = await client.GetAsync("/api/no/such/route", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A client address at any depth gets the page with the app's root as its base: with <c>./</c>, <c>/a/b</c>
    /// would load <c>/a/main.js</c> (found by the e2e smoke set).
    /// </summary>
    [TestMethod]
    public async Task ADeepClientAddressGetsThePageWithTheAppsRootAsBase()
    {
        RepositoryFiles.WebRoot();
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var client = ApiClients.Create(factory);

        using var response = await client.GetAsync("/authors/12/books", TestContext.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(html, "<base href=\"/\">");
        StringAssert.Contains(html, "<lib-root>");
        Assert.AreEqual(SecurityHeaders.AppPolicy, string.Join(";", response.Headers.GetValues("Content-Security-Policy")));
    }

    [TestMethod]
    public async Task TheOpenApiDocumentCarriesTheVersion()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var client = ApiClients.Create(factory);

        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json", TestContext.CancellationToken));

        Assert.AreEqual(AppVersion.Version, document.RootElement.GetProperty("info").GetProperty("version").GetString());
        Assert.IsTrue(document.RootElement.GetProperty("paths").TryGetProperty("/api/system/info", out _));
    }
}
