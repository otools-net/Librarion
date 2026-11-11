using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Librarion.Benchmarks.Bench;

/// <summary>
/// Asks the database how it runs a captured command and flags a full scan of a big table
/// (agents/docs/development/scale.md: a query on a big table that can't use an index is a bug).
/// </summary>
public static partial class PlanCheck
{
    /// <summary>Tables where a full scan is never acceptable at 2M books.</summary>
    public static readonly string[] BigTables = ["Books", "BookFiles", "BookIdentifiers", "Tags", "TagAliases", "TagClosure", "BookTags", "TagCounts"];

    /// <returns>The plan's text and the full scans found in it.</returns>
    public static async Task<(string Plan, List<string> Scans)> ExplainAsync(string provider, string connectionString, CapturedCommand captured, CancellationToken cancellationToken)
    {
        await using DbConnection connection = provider == "postgres" ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = (provider == "postgres" ? "EXPLAIN " : "EXPLAIN QUERY PLAN ") + captured.Sql;
        foreach (var (name, value) in captured.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var plan = new StringBuilder();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                plan.AppendLine(reader.GetValue(provider == "postgres" ? 0 : reader.FieldCount - 1)?.ToString());
        }

        var text = plan.ToString();
        var aliases = Aliases(captured.Sql);
        var scans = new List<string>();
        // EF answers an empty list (`ids.Contains(x)` with no ids) with `WHERE 0`: SQLite still prints `SCAN`, but reads no
        // row (PostgreSQL shows a one-time false filter). Not a scan.
        if (provider != "postgres" && ConstantFalse().IsMatch(captured.Sql))
            return (text, scans);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var table = provider == "postgres"
                ? PostgresSeqScan().Match(line) is { Success: true } pg ? pg.Groups[1].Value : null
                : SqliteScan().Match(line) is { Success: true } lite ? lite.Groups[1].Value : null;
            if (table is null)
                continue;
            table = aliases.GetValueOrDefault(table, table);
            if (BigTables.Contains(table))
                scans.Add($"{table}: {line}");
        }

        return (text, scans);
    }

    /// <summary>EF's aliases (<c>"Books" AS b</c>) back to table names: SQLite's plans name the alias.</summary>
    private static Dictionary<string, string> Aliases(string sql)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in AliasPattern().Matches(sql))
            result[match.Groups[2].Value] = match.Groups[1].Value;
        return result;
    }

    [GeneratedRegex(@"\bWHERE 0\s*$", RegexOptions.Multiline)]
    private static partial Regex ConstantFalse();

    [GeneratedRegex("""Seq Scan on "?(\w+)"?""")]
    private static partial Regex PostgresSeqScan();

    // "SCAN b" is a full scan; "SCAN b USING [COVERING] INDEX" walks an index in order, which keyset pages want.
    [GeneratedRegex(@"^SCAN (\w+)(?! USING)(?:\s|$)")]
    private static partial Regex SqliteScan();

    [GeneratedRegex("""
                    "(\w+)" AS "?(\w+)"?
                    """)]
    private static partial Regex AliasPattern();
}
