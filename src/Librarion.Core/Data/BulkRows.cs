using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Librarion.Core.Data;

/// <summary>
/// Many rows inserted past EF's change tracking (agents/docs/development/scale.md#writes): PostgreSQL's binary
/// <c>COPY</c>, SQLite's one prepared insert run per row inside a transaction. For rows whose keys are known (links,
/// identifiers, files of books just saved); a row EF would track is still written through EF. Values: int, long, short,
/// byte (as smallint), bool, double, string, UTC DateTime, byte[], null.
/// </summary>
public static class BulkRows
{
    public static async Task InsertAsync(LibrarionDbContext db, string table, string[] columns, IReadOnlyCollection<object?[]> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
            return;
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            var names = string.Join(", ", columns.Select(c => $"\"{c}\""));
            if (connection is NpgsqlConnection postgres)
            {
                await using var importer = await postgres.BeginBinaryImportAsync($"COPY \"{table}\" ({names}) FROM STDIN (FORMAT BINARY)", cancellationToken);
                foreach (var row in rows)
                {
                    await importer.StartRowAsync(cancellationToken);
                    foreach (var value in row)
                        await WriteAsync(importer, value, cancellationToken);
                }

                await importer.CompleteAsync(cancellationToken);
                return;
            }

            IDbContextTransaction? own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
                command.CommandText = $"INSERT INTO \"{table}\" ({names}) VALUES ({string.Join(", ", columns.Select((_, i) => $"$p{i}"))})";
                var parameters = columns.Select((_, i) =>
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = $"$p{i}";
                    command.Parameters.Add(parameter);
                    return parameter;
                }).ToArray();
                foreach (var row in rows)
                {
                    for (var i = 0; i < row.Length; i++)
                        parameters[i].Value = row[i] switch { null => DBNull.Value, byte b => (short)b, var v => v };
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                if (own is not null)
                    await own.CommitAsync(cancellationToken);
            }
            finally
            {
                if (own is not null)
                    await own.DisposeAsync();
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Adds each delta to its tag's <see cref="TagCount"/> in the library (a missing row is made with the delta; a count
    /// never goes below zero by an update), in one statement on PostgreSQL, one prepared upsert per tag on SQLite. Rows
    /// left at zero or below are the caller's to delete.
    /// </summary>
    public static async Task AddCountsAsync(LibrarionDbContext db, int tenantId, int libraryId, IReadOnlyCollection<KeyValuePair<int, int>> deltas,
        CancellationToken cancellationToken)
    {
        if (deltas.Count == 0)
            return;
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            if (connection is NpgsqlConnection postgres)
            {
                await using var command = new NpgsqlCommand("""
                    INSERT INTO "TagCounts" ("TenantId", "LibraryId", "TagId", "BookCount")
                    SELECT $1, $2, d.tag, d.delta FROM unnest($3::integer[], $4::integer[]) AS d(tag, delta)
                    ON CONFLICT ("LibraryId", "TagId") DO UPDATE SET "BookCount" = GREATEST(0, "TagCounts"."BookCount" + EXCLUDED."BookCount")
                    """, postgres);
                command.Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
                command.Parameters.Add(new NpgsqlParameter { Value = tenantId });
                command.Parameters.Add(new NpgsqlParameter { Value = libraryId });
                command.Parameters.Add(new NpgsqlParameter { Value = deltas.Select(d => d.Key).ToArray() });
                command.Parameters.Add(new NpgsqlParameter { Value = deltas.Select(d => d.Value).ToArray() });
                await command.ExecuteNonQueryAsync(cancellationToken);
                return;
            }

            IDbContextTransaction? own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
                command.CommandText = """
                    INSERT INTO "TagCounts" ("TenantId", "LibraryId", "TagId", "BookCount") VALUES ($tenant, $library, $tag, $delta)
                    ON CONFLICT ("LibraryId", "TagId") DO UPDATE SET "BookCount" = MAX(0, "BookCount" + excluded."BookCount")
                    """;
                DbParameter Add(string name, object value)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value;
                    command.Parameters.Add(parameter);
                    return parameter;
                }

                Add("$tenant", tenantId);
                Add("$library", libraryId);
                var tag = Add("$tag", 0);
                var delta = Add("$delta", 0);
                foreach (var (tagId, value) in deltas)
                {
                    tag.Value = tagId;
                    delta.Value = value;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                if (own is not null)
                    await own.CommitAsync(cancellationToken);
            }
            finally
            {
                if (own is not null)
                    await own.DisposeAsync();
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// PostgreSQL: <paramref name="count"/> ids taken from <paramref name="table"/>'s <c>Id</c> sequence, for rows then
    /// copied with their ids. Null on SQLite, whose ids come from the insert (taking the next ids by hand could meet
    /// another writer's).
    /// </summary>
    public static async Task<long[]?> ReserveIdsAsync(LibrarionDbContext db, string table, int count, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (db.Database.GetDbConnection() is not NpgsqlConnection connection)
                return null;
            await using var command = new NpgsqlCommand($"SELECT nextval(pg_get_serial_sequence('\"{table}\"', 'Id')) FROM generate_series(1, $1)", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = count });
            var ids = new long[count];
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            for (var i = 0; i < count && await reader.ReadAsync(cancellationToken); i++)
                ids[i] = reader.GetInt64(0);
            return ids;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static Task WriteAsync(NpgsqlBinaryImporter importer, object? value, CancellationToken cancellationToken) => value switch
    {
        null => importer.WriteNullAsync(cancellationToken),
        int v => importer.WriteAsync(v, NpgsqlDbType.Integer, cancellationToken),
        long v => importer.WriteAsync(v, NpgsqlDbType.Bigint, cancellationToken),
        short v => importer.WriteAsync(v, NpgsqlDbType.Smallint, cancellationToken),
        byte v => importer.WriteAsync((short)v, NpgsqlDbType.Smallint, cancellationToken),
        bool v => importer.WriteAsync(v, NpgsqlDbType.Boolean, cancellationToken),
        double v => importer.WriteAsync(v, NpgsqlDbType.Double, cancellationToken),
        string v => importer.WriteAsync(v, NpgsqlDbType.Text, cancellationToken),
        DateTime v => importer.WriteAsync(v, NpgsqlDbType.TimestampTz, cancellationToken),
        byte[] v => importer.WriteAsync(v, NpgsqlDbType.Bytea, cancellationToken),
        _ => throw new NotSupportedException(value.GetType().Name),
    };
}
