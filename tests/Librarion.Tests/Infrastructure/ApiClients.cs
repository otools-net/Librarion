using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Librarion.Api.Endpoints;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests.Infrastructure;

/// <summary>Test servers and their HTTP clients (as SFE): clients keep cookies and send the header changes need.</summary>
public static class ApiClients
{
    public const string AdminName = "admin";
    public const string Password = "correct horse battery";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>A server on <paramref name="catalog"/>, with changes to its configuration.</summary>
    /// <param name="services">Services added to the server's (a test's own import source).</param>
    public static WebApplicationFactory<Program> CreateFactory(TestCatalog catalog, Action<IDictionary<string, string?>>? configure = null,
        Action<IServiceCollection>? services = null)
    {
        var settings = catalog.Settings();
        configure?.Invoke(settings);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            if (services is not null)
                builder.ConfigureServices(services);
        });
    }

    /// <summary>A client that is not signed in.</summary>
    public static HttpClient Create(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add(RequestGuard.Header, "1");
        return client;
    }

    /// <summary>
    /// Sets the server up (first server administrator <see cref="AdminName"/>, first tenant "Home" with its library)
    /// and returns the administrator's signed-in client and the session.
    /// </summary>
    public static async Task<(HttpClient Client, SessionInfo Session)> SetUpAsync(WebApplicationFactory<Program> factory, CancellationToken cancellationToken)
    {
        var client = Create(factory);
        var code = factory.Services.GetRequiredService<GeneratedSetupCode>().Ensure();
        using var response = await client.PostAsJsonAsync("/api/setup", new SetupInput(code, AdminName, Password), Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (client, (await response.Content.ReadFromJsonAsync<SessionInfo>(Json, cancellationToken))!);
    }

    /// <summary>A client signed in as <paramref name="userName"/> (fails the test when the sign-in is refused).</summary>
    public static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> factory, string userName, string password, CancellationToken cancellationToken)
    {
        var client = Create(factory);
        using var response = await client.PostAsJsonAsync("/api/session/login", new SignInRequest(userName, password), Json, cancellationToken);
        Assert.IsTrue(response.IsSuccessStatusCode, $"{userName} signs in: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}");
        return client;
    }

    /// <summary>Creates an account through the API (as <paramref name="admin"/>) and returns it.</summary>
    public static async Task<UserInfo> CreateUserAsync(HttpClient admin, NewUser user, CancellationToken cancellationToken)
    {
        using var response = await admin.PostAsJsonAsync("/api/users", user, Json, cancellationToken);
        Assert.IsTrue(response.IsSuccessStatusCode, $"{user.UserName} is created: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<UserInfo>(Json, cancellationToken))!;
    }

    public static async Task<T> GetAsync<T>(this HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        Assert.IsTrue(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken))!;
    }
}
