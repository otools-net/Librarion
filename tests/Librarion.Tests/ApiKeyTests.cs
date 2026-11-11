using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>API keys: made, listed, revoked, and signing in by scope on <c>/api</c> and <c>/opds</c> (agents/docs/features/opds-auth.md).</summary>
[TestClass]
public sealed class ApiKeyTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private sealed class Server : IAsyncDisposable
    {
        public required WebApplicationFactory<Program> Factory { get; init; }
        public required HttpClient Admin { get; init; }
        public required HttpClient Reader { get; init; }
        public required UserInfo ReaderUser { get; init; }

        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            Reader.Dispose();
            await Factory.DisposeAsync();
        }
    }

    private async Task<Server> StartAsync(TestCatalog catalog)
    {
        var (factory, admin, reader) = await OpdsTests.ServerWithReaderAsync(catalog, Cancel);
        return new Server { Factory = factory, Admin = admin, ReaderUser = reader, Reader = await ApiClients.SignInAsync(factory, "reader", ApiClients.Password, Cancel) };
    }

    private async Task<NewApiKey> CreateAsync(HttpClient client, ApiKeyInput input)
    {
        using var response = await client.PostAsJsonAsync("/api/account/api-keys", input, ApiClients.Json, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
        return (await response.Content.ReadFromJsonAsync<NewApiKey>(ApiClients.Json, Cancel))!;
    }

    /// <summary>A client sending only the key, as a script would: no cookie, no <see cref="RequestGuard.Header"/>.</summary>
    private static HttpClient Bearer(WebApplicationFactory<Program> factory, string key, bool header = false)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        if (header)
            client.DefaultRequestHeaders.Add(ApiKeySignIn.Header, key);
        else
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    [TestMethod]
    public async Task AKeyIsShownOnceAndListedWithoutItsSecret()
    {
        using var catalog = new TestCatalog();
        await using var server = await StartAsync(catalog);

        var made = await CreateAsync(server.Reader, new ApiKeyInput(" Phone ", [ApiKeyScopes.Opds, ApiKeyScopes.ApiRead], PathAllowed: true));
        using var listed = await server.Reader.GetAsync("/api/account/api-keys", Cancel);
        var text = await listed.Content.ReadAsStringAsync(Cancel);

        Assert.MatchesRegex(@"^lib_\d+_[A-Za-z0-9_-]{43}$", made.Key);
        Assert.AreEqual("Phone", made.Info.Name);
        CollectionAssert.AreEqual(new[] { ApiKeyScopes.Opds, ApiKeyScopes.ApiRead }, made.Info.Scopes.ToArray());
        Assert.IsTrue(made.Info.PathAllowed);
        Assert.IsNull(made.Info.Expires);
        StringAssert.Contains(text, "\"Phone\"");
        Assert.IsFalse(text.Contains(made.Key[^20..]), "The secret is never listed");
        var stored = await (await server.Factory.Services.GetRequiredService<TenantDb>().ForServerAsync(Cancel)).ApiKeys.SingleAsync(Cancel);
        Assert.IsFalse(stored.SecretHash.Contains(made.Key[^20..]), "Only its hash is kept");
        var events = await server.Factory.Services.GetRequiredService<SecurityLog>().ListAsync(new SecurityEventQuery(null, ServerWide: true), Cancel);
        Assert.IsTrue(events.Any(e => e is { Kind: SecurityEventKind.ApiKeyCreated, UserName: "reader" } && e.Detail == "Phone (opds api:read)"));
        Assert.IsFalse(events.Any(e => e.Detail?.Contains(made.Key[^20..]) == true));
    }

    [TestMethod]
    [DataRow("", "opds", "name")]
    [DataRow("Phone", "", "scopes")]
    [DataRow("Phone", "everything", "scopes")]
    public async Task BadInputIsRefused(string name, string scope, string field)
    {
        using var catalog = new TestCatalog();
        await using var server = await StartAsync(catalog);

        using var response = await server.Reader.PostAsJsonAsync("/api/account/api-keys",
            new ApiKeyInput(name, scope.Length == 0 ? [] : [scope]), ApiClients.Json, Cancel);
        using var past = await server.Reader.PostAsJsonAsync("/api/account/api-keys",
            new ApiKeyInput("Phone", [ApiKeyScopes.Opds], Expires: DateTime.UtcNow.AddDays(-1)), ApiClients.Json, Cancel);
        using var library = await server.Reader.PostAsJsonAsync("/api/account/api-keys",
            new ApiKeyInput("Phone", [ApiKeyScopes.Opds], LibraryIds: [987654]), ApiClients.Json, Cancel);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(Cancel), field);
        Assert.AreEqual(HttpStatusCode.BadRequest, past.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, library.StatusCode);
    }

    /// <summary>
    /// <c>api:read</c> reads the API (Bearer or <c>X-Api-Key</c>) and changes nothing; <c>api:write</c> changes things without
    /// the anti-CSRF header (a key is no ambient credential); neither opens the feeds.
    /// </summary>
    [TestMethod]
    public async Task KeysSignInOnTheApiByTheirScope()
    {
        using var catalog = new TestCatalog();
        await using var server = await StartAsync(catalog);
        var reading = await CreateAsync(server.Reader, new ApiKeyInput("Read", [ApiKeyScopes.ApiRead]));
        var writing = await CreateAsync(server.Reader, new ApiKeyInput("Write", [ApiKeyScopes.ApiWrite]));
        using var reader = Bearer(server.Factory, reading.Key);
        using var readerByHeader = Bearer(server.Factory, reading.Key, header: true);
        using var writer = Bearer(server.Factory, writing.Key);
        var change = new PasswordChange(ApiClients.Password, "another long password");

        using (var session = await reader.GetAsync("/api/session", Cancel))
            StringAssert.Contains(await session.Content.ReadAsStringAsync(Cancel), "\"reader\"");
        using (var libraries = await readerByHeader.GetAsync("/api/libraries", Cancel))
            Assert.AreEqual(HttpStatusCode.OK, libraries.StatusCode);
        using (var refused = await reader.PutAsJsonAsync("/api/session/password", change, ApiClients.Json, Cancel))
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode, "api:read changes nothing");
        using (var feeds = await reader.GetAsync("/opds", Cancel))
            Assert.AreEqual(HttpStatusCode.Forbidden, feeds.StatusCode, "No opds scope");
        using (var changed = await writer.PutAsJsonAsync("/api/session/password", change, ApiClients.Json, Cancel))
            Assert.AreEqual(HttpStatusCode.NoContent, changed.StatusCode, "api:write, without the anti-CSRF header");

        // The new password gave the account a new stamp: its keys end with its sessions.
        using var after = await reader.GetAsync("/api/libraries", Cancel);
        Assert.AreEqual(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    /// <summary>An <c>opds</c> key opens the feeds as the Basic password (its owner's name only), Bearer or <c>X-Api-Key</c>; never the API.</summary>
    [TestMethod]
    public async Task AnOpdsKeyOpensTheFeedsOnly()
    {
        using var catalog = new TestCatalog();
        await using var server = await StartAsync(catalog);
        var key = (await CreateAsync(server.Reader, new ApiKeyInput("Phone", [ApiKeyScopes.Opds]))).Key;

        foreach (var (client, expected) in new[]
                 {
                     (OpdsTests.Reader(server.Factory, "Reader", key), HttpStatusCode.OK),
                     (Bearer(server.Factory, key), HttpStatusCode.OK),
                     (Bearer(server.Factory, key, header: true), HttpStatusCode.OK),
                     (OpdsTests.Reader(server.Factory, ApiClients.AdminName, key), HttpStatusCode.Unauthorized),
                 })
        {
            using (client)
            using (var response = await client.GetAsync("/opds", Cancel))
                Assert.AreEqual(expected, response.StatusCode);
        }
        using var api = Bearer(server.Factory, key);
        using var refused = await api.GetAsync("/api/libraries", Cancel);
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [TestMethod]
    public async Task ARevokedExpiredOrWrongKeyIsRefused()
    {
        using var catalog = new TestCatalog();
        await using var server = await StartAsync(catalog);
        var revoked = await CreateAsync(server.Reader, new ApiKeyInput("Old", [ApiKeyScopes.ApiRead]));
        var expiring = await CreateAsync(server.Reader, new ApiKeyInput("Soon", [ApiKeyScopes.ApiRead], Expires: DateTime.UtcNow.AddDays(1)));
        using var first = Bearer(server.Factory, revoked.Key);
        using (var works = await first.GetAsync("/api/libraries", Cancel))
            Assert.AreEqual(HttpStatusCode.OK, works.StatusCode);

        using (var revoke = await server.Reader.DeleteAsync($"/api/account/api-keys/{revoked.Info.Id}", Cancel))
            Assert.AreEqual(HttpStatusCode.OK, revoke.StatusCode);
        await using (var db = await server.Factory.Services.GetRequiredService<TenantDb>().ForServerAsync(Cancel))
            await db.ApiKeys.Where(k => k.Id == expiring.Info.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.Expires, DateTime.UtcNow.AddMinutes(-1)), Cancel);
        server.Factory.Services.GetRequiredService<AccessChanges>().Changed();

        foreach (var key in new[] { revoked.Key, expiring.Key, revoked.Key[..^2] + "xx", "lib_999_" + new string('a', 43), "nonsense" })
        {
            using var client = Bearer(server.Factory, key);
            using var response = await client.GetAsync("/api/libraries", Cancel);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, key);
        }
        var listed = await server.Reader.GetAsync<List<ApiKeyInfo>>("/api/account/api-keys", Cancel);
        Assert.IsNotNull(listed.Single(k => k.Id == revoked.Info.Id).Revoked);
        Assert.IsNotNull(listed.Single(k => k.Id == revoked.Info.Id).LastUsed);
        var events = await server.Factory.Services.GetRequiredService<SecurityLog>().ListAsync(new SecurityEventQuery(null, ServerWide: true), Cancel);
        Assert.IsTrue(events.Any(e => e.Kind == SecurityEventKind.ApiKeyRevoked && e.Detail == "Old"));
        Assert.IsTrue(events.Any(e => e.Kind is SecurityEventKind.SignInFailed or SecurityEventKind.LockedOut && e.Detail == ApiKeySignIn.LogDetail));
    }

    /// <summary>A stale key failing again and again locks itself out, not the other keys used from the same address.</summary>
    [TestMethod]
    public async Task AStaleKeyLocksOutOnlyItself()
    {
        using var catalog = new TestCatalog();
        await using var server = await StartAsync(catalog);
        var stale = await CreateAsync(server.Reader, new ApiKeyInput("Old", [ApiKeyScopes.ApiRead]));
        var good = await CreateAsync(server.Reader, new ApiKeyInput("New", [ApiKeyScopes.ApiRead]));
        using (await server.Reader.DeleteAsync($"/api/account/api-keys/{stale.Info.Id}", Cancel)) { }
        using var staleClient = Bearer(server.Factory, stale.Key);
        using var goodClient = Bearer(server.Factory, good.Key);

        for (var i = 0; i < 8; i++)
            using (await staleClient.GetAsync("/api/libraries", Cancel)) { }
        using var response = await goodClient.GetAsync("/api/libraries", Cancel);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A key can't manage keys (it could make one wider than itself): the web session only.</summary>
    [TestMethod]
    public async Task KeysCannotManageKeys()
    {
        using var catalog = new TestCatalog();
        await using var server = await StartAsync(catalog);
        var key = await CreateAsync(server.Reader, new ApiKeyInput("Script", [ApiKeyScopes.ApiWrite]));
        using var client = Bearer(server.Factory, key.Key);

        using var made = await client.PostAsJsonAsync("/api/account/api-keys", new ApiKeyInput("Wider", [ApiKeyScopes.ApiWrite]), ApiClients.Json, Cancel);
        using var listed = await client.GetAsync("/api/account/api-keys", Cancel);

        Assert.AreEqual(HttpStatusCode.Forbidden, made.StatusCode);
        StringAssert.Contains(await made.Content.ReadAsStringAsync(Cancel), ApiKeyEndpoints.NeedsSessionType);
        Assert.AreEqual(HttpStatusCode.Forbidden, listed.StatusCode);
    }

    [TestMethod]
    public async Task AdministratorsListAndRevokeAnAccountsKeys()
    {
        using var catalog = new TestCatalog();
        await using var server = await StartAsync(catalog);
        var key = await CreateAsync(server.Reader, new ApiKeyInput("Phone", [ApiKeyScopes.ApiRead]));

        var listed = await server.Admin.GetAsync<List<ApiKeyInfo>>($"/api/users/{server.ReaderUser.Id}/api-keys", Cancel);
        using var revoked = await server.Admin.DeleteAsync($"/api/users/{server.ReaderUser.Id}/api-keys/{key.Info.Id}", Cancel);
        using var missing = await server.Admin.GetAsync("/api/users/987654/api-keys", Cancel);
        using var notTheirs = await server.Reader.GetAsync($"/api/users/{server.ReaderUser.Id}/api-keys", Cancel);
        using var client = Bearer(server.Factory, key.Key);
        using var after = await client.GetAsync("/api/libraries", Cancel);

        Assert.AreEqual("Phone", listed.Single().Name);
        Assert.AreEqual(HttpStatusCode.OK, revoked.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, notTheirs.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    /// <summary>A key limited to libraries holds library rights there only; tenant rights stay; scopes decide by place and method.</summary>
    [TestMethod]
    public void AGrantNarrowsRightsToItsLibrariesAndScopes()
    {
        var access = new EffectiveAccess(
            new HashSet<string> { Rights.Browse, Rights.Download, Rights.OwnPassword },
            new Dictionary<int, IReadOnlySet<string>> { [1] = new HashSet<string> { Rights.Browse }, [2] = new HashSet<string> { Rights.Browse, Rights.Download } },
            false);
        var limited = new ApiKeyGrant(1, 1, new HashSet<string> { ApiKeyScopes.Opds, ApiKeyScopes.ApiRead }, new HashSet<int> { 1 }, false);
        var whole = limited with { LibraryIds = new HashSet<int>() };

        var narrowed = limited.Narrow(access);

        CollectionAssert.AreEquivalent(new[] { 1 }, narrowed.Libraries.Keys.ToList());
        Assert.IsTrue(narrowed.Has(Rights.Browse));
        Assert.IsFalse(narrowed.Has(Rights.Download), "Held in library 2 only");
        Assert.IsTrue(narrowed.Has(Rights.OwnPassword));
        Assert.AreSame(access, whole.Narrow(access));
        Assert.IsTrue(limited.Allows(opds: true, reading: true));
        Assert.IsTrue(limited.Allows(opds: false, reading: true));
        Assert.IsFalse(limited.Allows(opds: false, reading: false));
        Assert.IsTrue((limited with { Scopes = new HashSet<string> { ApiKeyScopes.ApiWrite } }).Allows(opds: false, reading: true));
        Assert.IsFalse((limited with { Scopes = new HashSet<string> { ApiKeyScopes.ApiWrite } }).Allows(opds: true, reading: true));
    }

    [TestMethod]
    [DataRow("lib_12_abcdefghijklmnopqrstuvwxyz", 12)]
    [DataRow("lib_7_a_b-c_defghijklmnopqrstuvwx", 7)]
    [DataRow("lib_x_abcdefghijklmnopqrstuvwxyz", -1)]
    [DataRow("lib_12_short", -1)]
    [DataRow("key_12_abcdefghijklmnopqrstuvwxyz", -1)]
    [DataRow("lib__abcdefghijklmnopqrstuvwxyz", -1)]
    public void KeysAreParsedStrictly(string text, int id) =>
        Assert.AreEqual(id, ApiKeys.Parse(text)?.Id ?? -1);
}
