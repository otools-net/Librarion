using System.Globalization;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Import.Metadata;
using Microsoft.Data.Sqlite;

namespace Librarion.Core.Imports.Calibre;

/// <summary>A Calibre library, by its <c>metadata.db</c> (agents/docs/features/imports.md#calibre).</summary>
public sealed class CalibreFormat : ImportFormat
{
    public override string Key => "calibre";

    public override string? Check(string path)
    {
        try
        {
            using var source = new CalibreSource(path);
            return source.Problem();
        }
        catch (SqliteException ex)
        {
            return ex.Message;
        }
    }

    public override IImportSource Open(string path) => new CalibreSource(path);
}

/// <summary>
/// Reads a Calibre library's <c>metadata.db</c> (opened read-only, never written; behaviour learnt from calibre-web's
/// <c>cps/db.py</c>, no code taken): the books in id order, a batch at a time, with one query per link table per batch.
/// Tables and columns are looked up first: an older or newer Calibre's database is read for what it has, unknown columns
/// are ignored. The files are <c>&lt;library&gt;/&lt;books.path&gt;/&lt;data.name&gt;.&lt;format&gt;</c>.
/// </summary>
public sealed class CalibreSource : IImportSource
{
    public const int BatchSize = 1000;
    private const int MaxDescription = 64 * 1024;

    private readonly SqliteConnection connection;
    private readonly string folder;
    private readonly HashSet<string> tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> bookColumns = new(StringComparer.OrdinalIgnoreCase);

    public CalibreSource(string path)
    {
        folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        // Read-only and unpooled: the database stays Calibre's, and the file isn't held once the import ends.
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 30,
        }.ToString());
        try
        {
            connection.Open();
            foreach (var name in Strings("SELECT name FROM sqlite_master WHERE type = 'table'"))
                tables.Add(name);
            if (tables.Contains("books"))
                foreach (var name in Strings("SELECT name FROM pragma_table_info('books')"))
                    bookColumns.Add(name);
        }
        catch
        {
            // Not a database (or not readable): the file isn't held.
            connection.Dispose();
            throw;
        }
    }

    public MetadataSource Owner => MetadataSource.Calibre;
    public string IdScheme => "calibre-uuid";

    /// <summary>Null when the database is a Calibre library's; else what is missing.</summary>
    public string? Problem()
    {
        foreach (var table in new[] { "books", "authors", "books_authors_link", "data" })
            if (!tables.Contains(table))
                return $"no table {table}: not a Calibre library's metadata.db";
        foreach (var column in new[] { "id", "title", "path" })
            if (!bookColumns.Contains(column))
                return $"no column books.{column}";
        return null;
    }

    public IEnumerable<ImportRecord> Read(ImportReport report, CancellationToken cancellationToken)
    {
        if (Problem() is { } problem)
        {
            report.Problem(problem);
            yield break;
        }

        var columns = CustomColumns(report);
        var after = 0L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<ImportRecord> batch;
            try
            {
                batch = Batch(after, columns, report, out after);
            }
            catch (SqliteException ex)
            {
                // A damaged database: what was read is kept, the rest reported.
                report.Problem($"metadata.db: {ex.Message}");
                yield break;
            }

            foreach (var record in batch)
                yield return record;
            if (after < 0)
                yield break;
        }
    }

    private sealed record Column(long Id, string Key, string Name, string Type, bool Normalized, bool Multiple);

    private sealed class Row
    {
        public long Id;
        public string? Title, Sort, Path, Uuid, Pubdate, Isbn;
        public double? SeriesIndex;
        public bool HasCover;
        public readonly List<FieldValue> Values = [];
        public readonly List<string> Tags = [];
        public readonly List<(string, string)> Identifiers = [];
        public readonly List<(string, string)> Properties = [];
        public readonly List<string> Files = [];
        public string? Description;
        public byte? Rating;
    }

    /// <summary>Books after <paramref name="after"/>; <paramref name="next"/> is the last id read, or -1 after the last batch.</summary>
    private List<ImportRecord> Batch(long after, List<Column> columns, ImportReport report, out long next)
    {
        string Optional(string column, string otherwise = "NULL") => bookColumns.Contains(column) ? column : otherwise;
        var rows = new Dictionary<long, Row>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT id, title, {Optional("sort")}, {Optional("pubdate")}, {Optional("series_index")}, path, {Optional("uuid")}, {Optional("has_cover", "0")}, {Optional("isbn")}
                FROM books WHERE id > $after ORDER BY id LIMIT {BatchSize}
                """;
            command.Parameters.AddWithValue("$after", after);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new Row
                {
                    Id = reader.GetInt64(0), Title = Text(reader, 1), Sort = Text(reader, 2), Pubdate = Text(reader, 3),
                    SeriesIndex = reader.IsDBNull(4) ? null : reader.GetDouble(4), Path = Text(reader, 5), Uuid = Text(reader, 6),
                    HasCover = !reader.IsDBNull(7) && reader.GetInt64(7) != 0, Isbn = Text(reader, 8),
                };
                rows[row.Id] = row;
            }
        }

        if (rows.Count == 0)
        {
            next = -1;
            return [];
        }

        next = rows.Keys.Max();
        var ids = string.Join(',', rows.Keys);
        Links(rows, $"SELECT l.book, a.name, a.sort FROM books_authors_link l JOIN authors a ON a.id = l.author WHERE l.book IN ({ids}) ORDER BY l.id", (row, r) =>
        {
            // Calibre keeps a comma inside an author's name as '|'.
            if (Text(r, 1)?.Replace('|', ',') is { } name)
                row.Values.Add(new FieldValue("author", name, row.Values.Count(v => v.Field == "author"), SortName: Text(r, 2)?.Replace('|', ',')));
        });
        if (tables.Contains("books_series_link") && tables.Contains("series"))
            Links(rows, $"SELECT l.book, s.name FROM books_series_link l JOIN series s ON s.id = l.series WHERE l.book IN ({ids}) ORDER BY l.id", (row, r) =>
            {
                if (Text(r, 1) is { } name)
                    row.Values.Add(new FieldValue("series", name, Number: row.SeriesIndex));
            });
        if (tables.Contains("books_publishers_link") && tables.Contains("publishers"))
            Links(rows, $"SELECT l.book, p.name FROM books_publishers_link l JOIN publishers p ON p.id = l.publisher WHERE l.book IN ({ids}) ORDER BY l.id", (row, r) =>
            {
                if (Text(r, 1) is { } name)
                    row.Values.Add(new FieldValue("publisher", name));
            });
        if (tables.Contains("books_languages_link") && tables.Contains("languages"))
            Links(rows, $"SELECT l.book, g.lang_code FROM books_languages_link l JOIN languages g ON g.id = l.lang_code WHERE l.book IN ({ids}) ORDER BY l.item_order, l.id", (row, r) =>
            {
                if (Languages.Normalize(Text(r, 1)) is { } language)
                    row.Values.Add(new FieldValue("language", language));
            });
        if (tables.Contains("books_tags_link") && tables.Contains("tags"))
            Links(rows, $"SELECT l.book, t.name FROM books_tags_link l JOIN tags t ON t.id = l.tag WHERE l.book IN ({ids}) ORDER BY l.id", (row, r) =>
            {
                if (Text(r, 1) is { } name)
                    row.Tags.Add(name);
            });
        if (tables.Contains("books_ratings_link") && tables.Contains("ratings"))
            Links(rows, $"SELECT l.book, g.rating FROM books_ratings_link l JOIN ratings g ON g.id = l.rating WHERE l.book IN ({ids})", (row, r) =>
            {
                if (!r.IsDBNull(1) && r.GetInt64(1) is > 0 and <= 10 and var rating)
                    row.Rating = (byte)rating;
            });
        if (tables.Contains("comments"))
            Links(rows, $"SELECT book, text FROM comments WHERE book IN ({ids})", (row, r) =>
            {
                if (Text(r, 1) is { } html && EpubReader.Html(html.Length > MaxDescription ? html[..MaxDescription] : html) is { } text)
                    row.Description = text;
            });
        if (tables.Contains("identifiers"))
            Links(rows, $"SELECT book, type, val FROM identifiers WHERE book IN ({ids}) ORDER BY id", (row, r) =>
            {
                if (Identifier(Text(r, 1), Text(r, 2)) is { } identifier)
                    row.Identifiers.Add(identifier);
            });
        Links(rows, $"SELECT book, format, name FROM data WHERE book IN ({ids}) ORDER BY id", (row, r) =>
        {
            if (Text(r, 1) is { } format && Text(r, 2) is { } name && row.Path is { } path)
                row.Files.Add(System.IO.Path.Combine(folder, path.Replace('/', System.IO.Path.DirectorySeparatorChar), $"{name}.{format.ToLowerInvariant()}"));
        });
        foreach (var column in columns)
        {
            try
            {
                Custom(rows, ids, column);
            }
            catch (SqliteException ex)
            {
                report.Problem($"custom column {column.Name}: {ex.Message}");
            }
        }

        return rows.Values.Select(row => Record(row)).ToList();
    }

    private ImportRecord Record(Row row)
    {
        if (Year(row.Pubdate) is { } year)
            row.Values.Add(new FieldValue("year", year));
        if (row.Isbn is not null && Isbns.Normalize(row.Isbn) is { } isbn && !row.Identifiers.Contains(("isbn", isbn)))
            row.Identifiers.Add(("isbn", isbn));
        var metadata = new BookMetadata(ImportTagSourceKind.Calibre, row.Title, row.Description, row.Values, row.Tags, row.Identifiers, row.Rating, row.Sort,
            row.Properties.Count > 0 ? row.Properties : null);
        return new ImportRecord(row.Uuid ?? $"id:{row.Id}", metadata, row.Files.Select(f => new ImportFile(f)).ToList(), row.HasCover);
    }

    /// <summary>The library's custom columns (not marked for deletion), with a field key from their label.</summary>
    private List<Column> CustomColumns(ImportReport report)
    {
        var columns = new List<Column>();
        if (!tables.Contains("custom_columns"))
            return columns;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, label, name, datatype, is_multiple, normalized FROM custom_columns WHERE mark_for_delete = 0 ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var label = Text(reader, 1) ?? "";
                var key = new string(label.ToLowerInvariant().Select(c => c == '_' ? '-' : c).Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-').ToArray());
                if (key.Length > Tag.MaxFieldKeyLength)
                    key = key[..Tag.MaxFieldKeyLength];
                columns.Add(new Column(reader.GetInt64(0), key, Text(reader, 2) ?? label, Text(reader, 3) ?? "", !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
                    !reader.IsDBNull(4) && reader.GetInt64(4) != 0));
            }
        }
        catch (SqliteException ex)
        {
            report.Problem($"custom columns: {ex.Message}");
        }

        return columns.Where(c => c.Type != "composite" && tables.Contains($"custom_column_{c.Id}")).ToList();
    }

    /// <summary>
    /// A custom column's values of the batch: text, enumeration, series and rating ones become field values (a field per
    /// column, as authors are); long text, yes/no, numbers and dates become properties shown on the book's page.
    /// </summary>
    private void Custom(Dictionary<long, Row> rows, string ids, Column column)
    {
        var values = $"custom_column_{column.Id}";
        var link = $"books_custom_column_{column.Id}_link";
        if (column.Normalized)
        {
            if (!tables.Contains(link) || column.Key.Length == 0)
                return;
            var extra = column.Type == "series" ? "l.extra" : "NULL";
            Links(rows, $"SELECT l.book, v.value, {extra} FROM {link} l JOIN {values} v ON v.id = l.value WHERE l.book IN ({ids}) ORDER BY l.id", (row, r) =>
            {
                if (r.IsDBNull(1))
                    return;
                var value = column.Type == "rating"
                    ? (r.GetDouble(1) / 2).ToString("0.#", CultureInfo.InvariantCulture)
                    : Text(r, 1);
                if (value is not null)
                    row.Values.Add(new FieldValue(column.Key, value, Number: r.IsDBNull(2) ? null : r.GetDouble(2)));
            });
            return;
        }

        Links(rows, $"SELECT book, value FROM {values} WHERE book IN ({ids})", (row, r) =>
        {
            if (r.IsDBNull(1))
                return;
            var value = column.Type switch
            {
                "bool" => r.GetInt64(1) != 0 ? "true" : "false",
                "int" => r.GetInt64(1).ToString(CultureInfo.InvariantCulture),
                "float" => r.GetDouble(1).ToString("0.###", CultureInfo.InvariantCulture),
                "datetime" => Text(r, 1) is { Length: >= 10 } date && !date.StartsWith("0101-", StringComparison.Ordinal) ? date[..10] : null,
                "comments" => Text(r, 1) is { } html ? EpubReader.Html(html) : null,
                _ => Text(r, 1),
            };
            if (value is not null)
                row.Properties.Add((column.Name, value));
        });
    }

    private void Links(Dictionary<long, Row> rows, string sql, Action<Row, SqliteDataReader> each)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(0) && rows.TryGetValue(reader.GetInt64(0), out var row))
                each(row, reader);
    }

    private IEnumerable<string> Strings(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
            list.Add(reader.GetString(0));
        return list;
    }

    private static string? Text(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } text ? text : null;

    /// <summary>The year of Calibre's <c>pubdate</c>; its "undefined" date (year 101) is none.</summary>
    private static string? Year(string? pubdate) =>
        pubdate is { Length: >= 4 } && int.TryParse(pubdate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year > 101 ? year.ToString(CultureInfo.InvariantCulture) : null;

    private static (string, string)? Identifier(string? type, string? value)
    {
        if (type is null || value is null)
            return null;
        var scheme = type.Trim().ToLowerInvariant();
        if (scheme == "isbn")
            return Isbns.Normalize(value) is { } isbn ? ("isbn", isbn) : null;
        return scheme.Length is > 0 and <= 20 ? (scheme, value) : null;
    }

    public void Dispose() => connection.Dispose();
}
