using Librarion.Core.Data;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Librarion.Tests.Infrastructure;

/// <summary>
/// One test's own world: a temporary folder (the library root and the server's data) and its own database. SQLite by
/// default; a database of its own on PostgreSQL when <see cref="PostgresVariable"/> holds a connection string (to a
/// server where that user may create databases), dropped at the end. The whole suite runs once per provider
/// (agents/docs/development/testing.md).
/// </summary>
public sealed class TestCatalog : IDisposable
{
    public const string PostgresVariable = "LIBRARION_TEST_POSTGRES";

    /// <summary>The PostgreSQL server of this run, or null for SQLite.</summary>
    public static string? PostgresServer { get; } = Environment.GetEnvironmentVariable(PostgresVariable) is { Length: > 0 } value ? value : null;

    public static DatabaseProvider Provider => PostgresServer is null ? DatabaseProvider.Sqlite : DatabaseProvider.Postgres;

    private readonly string? _postgresDatabase;

    public TestCatalog()
    {
        BasePath = Path.Combine(Path.GetTempPath(), "librarion-tests", Guid.NewGuid().ToString("N"));
        Root = Path.Combine(BasePath, "Root");
        Directory.CreateDirectory(Root);
        if (PostgresServer is null)
        {
            ConnectionString = $"Data Source={Path.Combine(BasePath, "librarion.db")}";
        }
        else
        {
            _postgresDatabase = "librarion_test_" + Guid.NewGuid().ToString("N");
            ConnectionString = new NpgsqlConnectionStringBuilder(PostgresServer) { Database = _postgresDatabase }.ConnectionString;
        }
    }

    public string BasePath { get; }

    /// <summary>The library folder of the test (books go here from stage 3 on).</summary>
    public string Root { get; }

    public string ConnectionString { get; }

    /// <summary>The configuration of a server on this catalog (<see cref="ApiClients.CreateFactory"/>).</summary>
    public Dictionary<string, string?> Settings() => new()
    {
        [DatabaseOptions.ProviderKey] = Provider == DatabaseProvider.Sqlite ? "sqlite" : "postgres",
        ["ConnectionStrings:" + DatabaseOptions.ConnectionStringName] = ConnectionString,
    };

    public void Dispose()
    {
        if (_postgresDatabase is null)
        {
            // Only this file's pool: clearing every pool disposes handles that tests running alongside are opening.
            using (var connection = new SqliteConnection(ConnectionString))
                SqliteConnection.ClearPool(connection);
        }
        else
        {
            NpgsqlConnection.ClearAllPools();
            using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(PostgresServer) { Database = "postgres" }.ConnectionString);
            connection.Open();
            using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_postgresDatabase}\" WITH (FORCE)", connection);
            drop.ExecuteNonQuery();
        }

        try
        {
            Directory.Delete(BasePath, recursive: true);
        }
        catch (IOException)
        {
            // A file still held (an antivirus, a late handle): the temp folder is cleaned by the system.
        }
    }
}
