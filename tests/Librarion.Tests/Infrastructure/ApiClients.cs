using Librarion.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Librarion.Tests.Infrastructure;

/// <summary>Test servers and their HTTP clients (as SFE): clients keep cookies and send the header changes need.</summary>
public static class ApiClients
{
    /// <summary>A server on <paramref name="catalog"/>, with changes to its configuration.</summary>
    public static WebApplicationFactory<Program> CreateFactory(TestCatalog catalog, Action<IDictionary<string, string?>>? configure = null)
    {
        var settings = catalog.Settings();
        configure?.Invoke(settings);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        });
    }

    /// <summary>A client that is not signed in. Sign-in helpers come with accounts (stage 1).</summary>
    public static HttpClient Create(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add(RequestGuard.Header, "1");
        return client;
    }
}
