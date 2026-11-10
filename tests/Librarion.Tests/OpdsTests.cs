using System.Net;
using Librarion.Api.Opds;
using Librarion.Api.Security;
using Librarion.Tests.Infrastructure;

namespace Librarion.Tests;

/// <summary>The reader apps' feeds under <c>/opds</c> (agents/docs/features/opds.md, opds-auth.md).</summary>
[TestClass]
public sealed class OpdsTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

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
}
