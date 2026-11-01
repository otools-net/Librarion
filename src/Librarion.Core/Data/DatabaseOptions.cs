using Microsoft.Extensions.Configuration;

namespace Librarion.Core.Data;

public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

/// <summary>
/// Which database the server uses: <c>Librarion:Database:Provider</c> (<c>sqlite</c>, the default, or <c>postgres</c>)
/// and the connection string <c>ConnectionStrings:Librarion</c>. Without one, SQLite keeps its file in
/// <c>data/librarion.db</c> next to the application.
/// </summary>
public sealed record DatabaseOptions(DatabaseProvider Provider, string ConnectionString)
{
    public const string ProviderKey = "Librarion:Database:Provider";
    public const string ConnectionStringName = "Librarion";
    public const string DefaultSqliteFile = "data/librarion.db";

    public static DatabaseOptions From(IConfiguration configuration, string contentRoot)
    {
        var providerText = configuration[ProviderKey];
        var provider = string.IsNullOrWhiteSpace(providerText) || providerText.Equals("sqlite", StringComparison.OrdinalIgnoreCase)
            ? DatabaseProvider.Sqlite
            : providerText.Equals("postgres", StringComparison.OrdinalIgnoreCase) || providerText.Equals("postgresql", StringComparison.OrdinalIgnoreCase)
                ? DatabaseProvider.Postgres
                : throw new InvalidOperationException($"{ProviderKey} must be 'sqlite' or 'postgres', not '{providerText}'.");

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (provider == DatabaseProvider.Postgres)
                throw new InvalidOperationException($"PostgreSQL needs the connection string ConnectionStrings:{ConnectionStringName}.");
            var file = Path.GetFullPath(Path.Combine(contentRoot, DefaultSqliteFile));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            connectionString = $"Data Source={file}";
        }

        return new DatabaseOptions(provider, connectionString);
    }
}
