using Librarion.Core.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Core services of the server; relative paths in the settings are relative to <paramref name="contentRoot"/>.</summary>
    public static IServiceCollection AddLibrarionCore(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        services.AddLibrarionDatabase(DatabaseOptions.From(configuration, contentRoot));
        return services;
    }
}
