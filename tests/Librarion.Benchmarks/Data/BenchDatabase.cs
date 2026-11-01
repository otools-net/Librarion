using Librarion.Core;
using Librarion.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Librarion.Benchmarks.Data;

/// <param name="Provider"><c>sqlite</c> or <c>postgres</c>.</param>
/// <param name="ConnectionString">SQLite: <c>Data Source=file</c>; PostgreSQL: the catalog's database (made fresh by <c>generate</c>).</param>
public sealed record BenchTarget(string Provider, string ConnectionString)
{
    public bool IsPostgres => Provider == "postgres";
}

/// <summary>The benchmark's database: made fresh, brought to the current schema by Core's own migrations, opened with Core's services.</summary>
public static class BenchDatabase
{
    /// <summary>Deletes the SQLite file (and its WAL), or drops and creates the PostgreSQL database.</summary>
    public static async Task RecreateAsync(BenchTarget target, CancellationToken cancellationToken)
    {
        if (target.IsPostgres)
        {
            var builder = new NpgsqlConnectionStringBuilder(target.ConnectionString);
            var name = builder.Database ?? throw new InvalidOperationException("The connection string names no database.");
            builder.Database = "postgres";
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            foreach (var sql in new[] { $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", $"CREATE DATABASE \"{name}\"" })
            {
                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return;
        }

        var file = new SqliteConnectionStringBuilder(target.ConnectionString).DataSource;
        // This file's pool only: tests running alongside open their own (agents/docs/development/testing-backend.md).
        using (var connection = new SqliteConnection(target.ConnectionString))
            SqliteConnection.ClearPool(connection);
        foreach (var path in new[] { file, file + "-wal", file + "-shm" })
            File.Delete(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
    }

    /// <summary>Core's services on the target, the schema migrated (nothing started: no background tasks run).</summary>
    public static async Task<IHost> OpenAsync(BenchTarget target, string workFolder, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, ContentRootPath = workFolder });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DatabaseOptions.ProviderKey] = target.Provider,
            ["ConnectionStrings:" + DatabaseOptions.ConnectionStringName] = target.ConnectionString,
            ["Librarion:Setup:Code"] = "benchmark-setup-code",
            ["Librarion:Setup:CodeFile"] = Path.Combine(workFolder, "setup-code.txt"),
        });
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true).SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddLibrarionCore(builder.Configuration, workFolder);
        var host = builder.Build();
        var factory = host.Services.GetRequiredService<IDbContextFactory<LibrarionDbContext>>();
        await LibrarionDatabase.InitializeAsync(factory, "benchmark", host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Benchmark"), cancellationToken);
        return host;
    }
}
