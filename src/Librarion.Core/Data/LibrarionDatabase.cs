using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Librarion.Core.Data;

/// <summary>Registers the provider's context and brings its schema up at start.</summary>
public static class LibrarionDatabase
{
    public static void AddLibrarionDatabase(this IServiceCollection services, DatabaseOptions options)
    {
        services.AddSingleton(options);
        switch (options.Provider)
        {
            case DatabaseProvider.Sqlite:
                services.AddDbContextFactory<SqliteLibrarionDbContext>(builder => ConfigureSqlite(builder, options.ConnectionString));
                services.AddSingleton<IDbContextFactory<LibrarionDbContext>, ProviderFactory<SqliteLibrarionDbContext>>();
                break;
            case DatabaseProvider.Postgres:
                services.AddDbContextFactory<PostgresLibrarionDbContext>(builder => ConfigurePostgres(builder, options.ConnectionString));
                services.AddSingleton<IDbContextFactory<LibrarionDbContext>, ProviderFactory<PostgresLibrarionDbContext>>();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Provider, null);
        }
    }

    internal static void ConfigureSqlite(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlite(connectionString, sqlite => sqlite.MigrationsHistoryTable("__EFMigrationsHistory"))
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, ScopeModelCacheKeyFactory>();

    internal static void ConfigurePostgres(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory"))
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, ScopeModelCacheKeyFactory>();

    /// <summary>
    /// Applies the provider's migrations and records <paramref name="version"/> as the installed one. Before the first
    /// release this runs at every start unasked; afterwards an update gate comes first
    /// (agents/docs/development/production-upgrades.md).
    /// </summary>
    public static async Task InitializeAsync(IDbContextFactory<LibrarionDbContext> factory, string version, ILogger logger, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (db.Database.IsSqlite())
        {
            // Readers don't wait for a writer (the scanner writes while people browse); kept in the file once set.
            await db.Database.OpenConnectionAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
            logger.LogInformation("Bringing the database up: {Migrations}", string.Join(", ", pending));
        await db.Database.MigrateAsync(cancellationToken);

        var row = await db.ServerProperties.FindAsync([ServerProperties.Version], cancellationToken);
        if (row is null)
            db.ServerProperties.Add(new ServerProperty { Key = ServerProperties.Version, Value = version });
        else
            row.Value = version;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Hands out the provider's context as the shared base type.</summary>
    private sealed class ProviderFactory<TContext>(IDbContextFactory<TContext> inner) : IDbContextFactory<LibrarionDbContext>
        where TContext : LibrarionDbContext
    {
        public LibrarionDbContext CreateDbContext() => inner.CreateDbContext();

        public async Task<LibrarionDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            await inner.CreateDbContextAsync(cancellationToken);
    }
}

/// <summary><c>dotnet ef migrations add … --context SqliteLibrarionDbContext</c> (no database is opened).</summary>
internal sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteLibrarionDbContext>
{
    public SqliteLibrarionDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SqliteLibrarionDbContext>();
        LibrarionDatabase.ConfigureSqlite(builder, "Data Source=design-time.db");
        return new SqliteLibrarionDbContext(builder.Options);
    }
}

/// <summary><c>dotnet ef migrations add … --context PostgresLibrarionDbContext</c> (no database is opened).</summary>
internal sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresLibrarionDbContext>
{
    public PostgresLibrarionDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<PostgresLibrarionDbContext>();
        LibrarionDatabase.ConfigurePostgres(builder, "Host=localhost;Database=librarion_design");
        return new PostgresLibrarionDbContext(builder.Options);
    }
}
