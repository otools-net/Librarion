using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Librarion.Api.Endpoints;
using Librarion.Api.Opds;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>The reader apps' feeds under <c>/opds</c> and their sign-in (agents/docs/features/opds.md, opds-auth.md).</summary>
[TestClass]
public sealed class OpdsTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    /// <summary>A reader app: no cookies, Basic on every request.</summary>
    internal static HttpClient Reader(WebApplicationFactory<Program> factory, string userName, string secret)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{secret}")));
        return client;
    }

    /// <summary>A set-up server with an account "reader" in the Users group.</summary>
    internal static async Task<(WebApplicationFactory<Program> Factory, HttpClient Admin, UserInfo Reader)> ServerWithReaderAsync(TestCatalog catalog,
        CancellationToken cancellationToken, Action<IDictionary<string, string?>>? configure = null)
    {
        var factory = ApiClients.CreateFactory(catalog, configure);
        var admin = (await ApiClients.SetUpAsync(factory, cancellationToken)).Client;
        var users = (await admin.GetAsync<List<GroupInfo>>("/api/groups", cancellationToken)).Single(g => g.BuiltIn == BuiltInGroup.Users);
        var reader = await ApiClients.CreateUserAsync(admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [users.Id]), cancellationToken);
        return (factory, admin, reader);
    }

    // ---- The frame (chunk 1) ----

    /// <summary>
    /// Without credentials the feeds ask for Basic, so reader apps prompt for a name and password; the web app's cookie is
    /// never accepted there, even an administrator's.
    /// </summary>
    [TestMethod]
    public async Task TheFeedsAskForBasicAndNeverTakeTheCookie()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        using var visitor = ApiClients.Create(factory);

        foreach (var client in new[] { visitor, admin })
        {
            using var response = await client.GetAsync("/opds", Cancel);

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.AreEqual(OpdsAuthenticationHandler.Challenge, response.Headers.WwwAuthenticate.ToString());
        }
        // The cookie still signs in the app.
        using var app = await admin.GetAsync("/api/session", Cancel);
        Assert.AreEqual(HttpStatusCode.OK, app.StatusCode);
    }

    /// <summary>An address under <c>/opds</c> that is no feed is not found: never the client's page, file-like or deep.</summary>
    [TestMethod]
    [DataRow("/opds/nothing-here")]
    [DataRow("/opds/12/Some%20Name")]
    [DataRow("/opds/opensearch.xml")]
    public async Task AnUnknownFeedAddressIsNotFoundRatherThanThePage(string address)
    {
        RepositoryFiles.WebRoot();
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var client = (await ApiClients.SetUpAsync(factory, Cancel)).Client;

        using var response = await client.GetAsync(address, Cancel);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.IsFalse((await response.Content.ReadAsStringAsync(Cancel)).Contains("<lib-root>"));
    }

    [TestMethod]
    public async Task TheFeedsWaitForTheSetup()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var client = ApiClients.Create(factory);

        using var response = await client.GetAsync("/opds", Cancel);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(Cancel), RequestGuard.SetupRequiredType);
    }

    // ---- Basic with the account's password (chunk 2) ----

    [TestMethod]
    public async Task TheRightPasswordOpensTheCatalog()
    {
        using var catalog = new TestCatalog();
        var (factory, admin, _) = await ServerWithReaderAsync(catalog, Cancel);
        await using var _f = factory;
        using var _a = admin;

        foreach (var (name, password) in new[] { ("reader", ApiClients.Password), ("READER", ApiClients.Password), (ApiClients.AdminName, ApiClients.Password) })
        {
            using var client = Reader(factory, name, password);
            using var response = await client.GetAsync("/opds", Cancel);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, name);
            Assert.AreEqual("opds-catalog", response.Content.Headers.ContentType?.Parameters.Single(p => p.Name == "profile").Value, name);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(Cancel), "<feed xmlns=\"http://www.w3.org/2005/Atom\">");
        }
        // Remembered: the next request needs no PBKDF2.
        Assert.IsNotNull(factory.Services.GetRequiredService<BasicVerificationCache>().Find("reader", ApiClients.Password));
    }

    /// <summary>Wrong passwords answer the Basic challenge again, go to the security log, and lock the address out as on the web sign-in.</summary>
    [TestMethod]
    public async Task WrongPasswordsAreRefusedLoggedAndThrottled()
    {
        using var catalog = new TestCatalog();
        var (factory, admin, _) = await ServerWithReaderAsync(catalog, Cancel);
        await using var _f = factory;
        using var _a = admin;
        using var wrong = Reader(factory, "reader", "not the password");
        using var right = Reader(factory, "reader", ApiClients.Password);

        for (var i = 0; i < 5; i++)
        {
            using var refused = await wrong.GetAsync("/opds", Cancel);
            Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.AreEqual(OpdsAuthenticationHandler.Challenge, refused.Headers.WwwAuthenticate.ToString());
        }
        using var locked = await right.GetAsync("/opds", Cancel);

        Assert.AreEqual(HttpStatusCode.Unauthorized, locked.StatusCode, "The address waits, even with the right password");
        var events = await factory.Services.GetRequiredService<SecurityLog>().ListAsync(new SecurityEventQuery(null, ServerWide: true, UserName: "reader"), Cancel);
        Assert.AreEqual(4, events.Count(e => e is { Kind: SecurityEventKind.SignInFailed, Detail: OpdsAuthenticationHandler.LogDetail }));
        Assert.AreEqual(1, events.Count(e => e is { Kind: SecurityEventKind.LockedOut, Detail: OpdsAuthenticationHandler.LogDetail }));
    }

    /// <summary>A remembered password ends with a new password, and a disabled account is refused at once.</summary>
    [TestMethod]
    public async Task ANewPasswordOrADisabledAccountEndsAccess()
    {
        using var catalog = new TestCatalog();
        var (factory, admin, reader) = await ServerWithReaderAsync(catalog, Cancel);
        await using var _f = factory;
        using var _a = admin;
        using var old = Reader(factory, "reader", ApiClients.Password);
        using (var first = await old.GetAsync("/opds", Cancel))
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);

        using (var web = await ApiClients.SignInAsync(factory, "reader", ApiClients.Password, Cancel))
        using (var changed = await web.PutAsJsonAsync("/api/session/password", new PasswordChange(ApiClients.Password, "another long password"), ApiClients.Json, Cancel))
            Assert.AreEqual(HttpStatusCode.NoContent, changed.StatusCode);
        using var renewed = Reader(factory, "reader", "another long password");

        using (var stale = await old.GetAsync("/opds", Cancel))
            Assert.AreEqual(HttpStatusCode.Unauthorized, stale.StatusCode);
        using (var fresh = await renewed.GetAsync("/opds", Cancel))
            Assert.AreEqual(HttpStatusCode.OK, fresh.StatusCode);

        using (var disabled = await admin.PutAsJsonAsync($"/api/users/{reader.Id}", new UserInput("reader", Disabled: true), ApiClients.Json, Cancel))
            Assert.IsTrue(disabled.IsSuccessStatusCode, await disabled.Content.ReadAsStringAsync(Cancel));
        using var after = await renewed.GetAsync("/opds", Cancel);
        Assert.AreEqual(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [TestMethod]
    public async Task WithoutTheRightTheCatalogIsForbidden()
    {
        using var catalog = new TestCatalog();
        var (factory, admin, _) = await ServerWithReaderAsync(catalog, Cancel, settings => settings["Librarion:Rights:Disabled:0"] = Rights.Opds);
        await using var _f = factory;
        using var _a = admin;
        using var client = Reader(factory, "reader", ApiClients.Password);

        using var response = await client.GetAsync("/opds", Cancel);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    [DataRow("Basic")]
    [DataRow("Basic !!!")]
    [DataRow("Basic cmVhZGVy")]
    [DataRow("Bearer something")]
    public async Task AMalformedHeaderIsAskedAgain(string header)
    {
        using var catalog = new TestCatalog();
        var (factory, admin, _) = await ServerWithReaderAsync(catalog, Cancel);
        await using var _f = factory;
        using var _a = admin;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/opds");
        request.Headers.TryAddWithoutValidation("Authorization", header);

        using var response = await client.SendAsync(request, Cancel);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task ReaderAppsAreRateLimitedPerAccount()
    {
        using var catalog = new TestCatalog();
        var (factory, admin, _) = await ServerWithReaderAsync(catalog, Cancel, settings => settings["Librarion:Security:OpdsRequestsPerMinute"] = "3");
        await using var _f = factory;
        using var _a = admin;
        using var client = Reader(factory, "reader", ApiClients.Password);

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await client.GetAsync("/opds", Cancel);
            codes.Add(response.StatusCode);
        }

        CollectionAssert.AreEqual(new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests }, codes);
    }
}
