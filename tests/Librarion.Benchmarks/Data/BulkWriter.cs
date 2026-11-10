using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Librarion.Benchmarks.Data;

/// <summary>
/// Loads millions of rows fast, past EF: the big tables' secondary indexes are dropped first and rebuilt after the
/// load (one sort instead of millions of index inserts), statistics refreshed, identity counters moved past the ids
/// the generator gave.
/// </summary>
public abstract class BulkWriter : IAsyncDisposable
{
    /// <summary>Tables whose secondary indexes are rebuilt after the load.</summary>
    public static readonly string[] BigTables =
        ["Books", "BookFiles", "Archives", "BookIdentifiers", "Tags", "TagAliases", "TagParents", "TagClosure", "BookTags", "TagCounts"];

    /// <summary>Tables with a generated <c>Id</c> the generator fills itself.</summary>
    protected static readonly string[] IdentityTables = ["Books", "BookFiles", "Archives", "Folders", "Tags", "TagAliases", "Groups"];

    public static BulkWriter Open(string provider, string connectionString) => provider switch
    {
        "postgres" => new PostgresBulkWriter(connectionString),
        _ => new SqliteBulkWriter(connectionString),
    };

    public abstract Task BeginAsync(CancellationToken cancellationToken);

    public abstract Task<long> WriteAsync(string table, string[] columns, IEnumerable<object?[]> rows, CancellationToken cancellationToken);

    public abstract Task EndAsync(Action<string> report, CancellationToken cancellationToken);

    /// <summary>Runs a statement (small updates after the load).</summary>
    public abstract Task ExecuteAsync(string sql, CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();

    protected static string Quote(string name) => $"\"{name}\"";

    protected static string Columns(string[] columns) => string.Join(", ", columns.Select(Quote));
}

internal sealed class PostgresBulkWriter(string connectionString) : BulkWriter
{
    // No command timeout: completing a COPY of 15M rows (its foreign keys checked) takes minutes, past the 30 s default.
    private readonly NpgsqlConnection _connection = new(new NpgsqlConnectionStringBuilder(connectionString) { CommandTimeout = 0 }.ConnectionString);
    private readonly List<string> _indexes = [];

    public override async Task BeginAsync(CancellationToken cancellationToken)
    {
        await _connection.OpenAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(
                         "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = ANY(@tables) AND indexname NOT LIKE 'PK_%'", _connection))
        {
            command.Parameters.AddWithValue("tables", BigTables);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var names = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                names.Add(reader.GetString(0));
                _indexes.Add(reader.GetString(1));
            }

            await reader.CloseAsync();
            foreach (var name in names)
                await ExecuteAsync($"DROP INDEX {Quote(name)}", cancellationToken);
        }
    }

    public override async Task<long> WriteAsync(string table, string[] columns, IEnumerable<object?[]> rows, CancellationToken cancellationToken)
    {
        long count = 0;
        await using var importer = await _connection.BeginBinaryImportAsync($"COPY {Quote(table)} ({Columns(columns)}) FROM STDIN (FORMAT BINARY)", cancellationToken);
        foreach (var row in rows)
        {
            await importer.StartRowAsync(cancellationToken);
            foreach (var value in row)
            {
                if (value is null)
                    await importer.WriteNullAsync(cancellationToken);
                else
                    await WriteValueAsync(importer, value, cancellationToken);
            }

            count++;
        }

        await importer.CompleteAsync(cancellationToken);
        return count;
    }

    private static Task WriteValueAsync(NpgsqlBinaryImporter importer, object value, CancellationToken cancellationToken) => value switch
    {
        int v => importer.WriteAsync(v, cancellationToken),
        long v => importer.WriteAsync(v, cancellationToken),
        short v => importer.WriteAsync(v, cancellationToken),
        byte v => importer.WriteAsync((short)v, cancellationToken),
        bool v => importer.WriteAsync(v, cancellationToken),
        double v => importer.WriteAsync(v, cancellationToken),
        string v => importer.WriteAsync(v, NpgsqlTypes.NpgsqlDbType.Text, cancellationToken),
        DateTime v => importer.WriteAsync(v, NpgsqlTypes.NpgsqlDbType.TimestampTz, cancellationToken),
        byte[] v => importer.WriteAsync(v, cancellationToken),
        _ => throw new NotSupportedException(value.GetType().Name),
    };

    public override async Task EndAsync(Action<string> report, CancellationToken cancellationToken)
    {
        foreach (var definition in _indexes)
        {
            report(definition);
            await ExecuteAsync(definition, cancellationToken);
        }

        foreach (var table in IdentityTables)
            await ExecuteAsync($"SELECT setval(pg_get_serial_sequence('{Quote(table)}', 'Id'), GREATEST(1, (SELECT MAX(\"Id\") FROM {Quote(table)})))", cancellationToken);
        report("ANALYZE");
        await ExecuteAsync("ANALYZE", cancellationToken);
    }

    public override async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, _connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override ValueTask DisposeAsync() => _connection.DisposeAsync();
}

internal sealed class SqliteBulkWriter(string connectionString) : BulkWriter
{
    private const int RowsPerTransaction = 200_000;

    private readonly SqliteConnection _connection = new(connectionString);
    private readonly List<string> _indexes = [];

    public override async Task BeginAsync(CancellationToken cancellationToken)
    {
        await _connection.OpenAsync(cancellationToken);
        await ExecuteAsync("PRAGMA synchronous = OFF; PRAGMA cache_size = -500000; PRAGMA temp_store = MEMORY; PRAGMA foreign_keys = OFF;", cancellationToken);
        var names = new List<string>();
        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = $"SELECT name, sql FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL AND tbl_name IN ({string.Join(", ", BigTables.Select(t => $"'{t}'"))})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                names.Add(reader.GetString(0));
                _indexes.Add(reader.GetString(1));
            }
        }

        foreach (var name in names)
            await ExecuteAsync($"DROP INDEX {Quote(name)}", cancellationToken);
    }

    public override async Task<long> WriteAsync(string table, string[] columns, IEnumerable<object?[]> rows, CancellationToken cancellationToken)
    {
        long count = 0;
        var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await using var command = _connection.CreateCommand();
        command.CommandText = $"INSERT INTO {Quote(table)} ({Columns(columns)}) VALUES ({string.Join(", ", columns.Select((_, i) => "$p" + i))})";
        var parameters = columns.Select((_, i) => command.Parameters.Add(new SqliteParameter("$p" + i, null))).ToArray();
        command.Transaction = transaction;
        command.Prepare();
        try
        {
            foreach (var row in rows)
            {
                for (var i = 0; i < row.Length; i++)
                    parameters[i].Value = row[i] ?? DBNull.Value;
                await command.ExecuteNonQueryAsync(cancellationToken);
                if (++count % RowsPerTransaction == 0)
                {
                    await transaction.CommitAsync(cancellationToken);
                    await transaction.DisposeAsync();
                    transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
                    command.Transaction = transaction;
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await transaction.DisposeAsync();
        }

        return count;
    }

    public override async Task EndAsync(Action<string> report, CancellationToken cancellationToken)
    {
        foreach (var definition in _indexes)
        {
            report(definition);
            await ExecuteAsync(definition, cancellationToken);
        }

        report("ANALYZE");
        await ExecuteAsync("ANALYZE; PRAGMA optimize;", cancellationToken);
    }

    public override async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using DbCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override ValueTask DisposeAsync() => _connection.DisposeAsync();
}
