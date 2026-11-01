using Librarion.Api.Security;
using Librarion.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

[TestClass]
public sealed class RouteRightsTests
{
    /// <summary>
    /// Every route of the API and of its document names the right(s) it needs, or says it is anonymous
    /// (agents/docs/features/rights.md). Stage 1 adds: every named right is in the catalog.
    /// </summary>
    [TestMethod]
    public void EveryRouteNamesARight()
    {
        using var catalog = new TestCatalog();
        using var factory = ApiClients.CreateFactory(catalog);
        using var client = ApiClients.Create(factory);

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.TrimStart('/') is { } path
                        && (path.StartsWith("api/", StringComparison.Ordinal) || path.StartsWith("openapi", StringComparison.Ordinal)))
            .ToList();
        var unnamed = routes
            .Where(e => e.Metadata.GetMetadata<RightMetadata>() is null && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.IsGreaterThanOrEqualTo(2, routes.Count, "The check found the routes");
        Assert.IsEmpty(unnamed, "Routes without a right: " + string.Join(", ", unnamed));
    }
}
