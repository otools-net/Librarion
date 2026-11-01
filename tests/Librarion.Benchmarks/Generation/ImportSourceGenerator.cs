using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Librarion.Benchmarks.Generation;

/// <summary>
/// The sources of the <c>import</c> benchmark, shaped as the generated catalog (<see cref="CatalogGenerator.Plan"/>: the
/// same authors, series, tags and languages by Zipf): a Calibre library (its <c>metadata.db</c> and one EPUB per book,
/// empty, a folder per thousand books) and an INPX catalog with its zips (10,000 entries each). Written once and kept.
/// </summary>
public static class ImportSourceGenerator
{
    public const int InpxEntriesPerZip = 10_000;

    /// <summary>Real FB2 genre codes, picked by a book's categories.</summary>
    private static readonly string[] GenreCodes =
        ["sf_fantasy", "sf", "det_classic", "detective", "prose_classic", "prose_contemporary", "love_contemporary", "adv_history", "child_tale", "poetry",
         "sci_history", "sci_philosophy", "thriller", "humor", "nonf_biography", "religion", "home_cooking", "computers", "dramaturgy", "antique"];

    private static CatalogGenerator Generator(CatalogShape shape) => new(shape, new CatalogTarget(1, 1, 1, new Dictionary<Librarion.Core.Data.BuiltInTag, int>()));

    /// <summary>A Calibre library of <paramref name="shape"/>'s books in <paramref name="folder"/>; its <c>metadata.db</c>.</summary>
    public static string Calibre(CatalogShape shape, string folder, Action<string> report, CancellationToken cancellationToken)
    {
        var db = Path.Combine(folder, "metadata.db");
        if (File.Exists(db))
        {
            Indexes(db);
            return db;
        }

        Directory.CreateDirectory(folder);
        var temp = db + ".tmp";
        File.Delete(temp);
        var generator = Generator(shape);
        using (var connection = new SqliteConnection($"Data Source={temp};Pooling=False"))
        {
            connection.Open();
            Execute(connection, """
                PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;
                CREATE TABLE books (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL DEFAULT 'Unknown', sort TEXT, timestamp TIMESTAMP,
                    pubdate TIMESTAMP DEFAULT '0101-01-01 00:00:00+00:00', series_index REAL NOT NULL DEFAULT 1.0, author_sort TEXT, isbn TEXT DEFAULT '',
                    lccn TEXT DEFAULT '', path TEXT NOT NULL DEFAULT '', flags INTEGER NOT NULL DEFAULT 1, uuid TEXT, has_cover BOOL DEFAULT 0, last_modified TIMESTAMP);
                CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT NOT NULL, sort TEXT, link TEXT NOT NULL DEFAULT '');
                CREATE TABLE books_authors_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, author INTEGER NOT NULL);
                CREATE TABLE series (id INTEGER PRIMARY KEY, name TEXT NOT NULL, sort TEXT);
                CREATE TABLE books_series_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, series INTEGER NOT NULL);
                CREATE TABLE publishers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, sort TEXT);
                CREATE TABLE books_publishers_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, publisher INTEGER NOT NULL);
                CREATE TABLE languages (id INTEGER PRIMARY KEY, lang_code TEXT NOT NULL);
                CREATE TABLE books_languages_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, lang_code INTEGER NOT NULL, item_order INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE tags (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE books_tags_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, tag INTEGER NOT NULL);
                CREATE TABLE ratings (id INTEGER PRIMARY KEY, rating INTEGER);
                CREATE TABLE books_ratings_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, rating INTEGER NOT NULL);
                CREATE TABLE comments (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, text TEXT NOT NULL);
                CREATE TABLE identifiers (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, type TEXT NOT NULL DEFAULT 'isbn', val TEXT NOT NULL);
                CREATE TABLE data (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, format TEXT NOT NULL, uncompressed_size INTEGER NOT NULL, name TEXT NOT NULL);
                """);
            using (var transaction = connection.BeginTransaction())
            {
                Rows(connection, "INSERT INTO authors (id, name, sort) VALUES ($1, $2, $3)", Enumerable.Range(0, shape.Authors)
                    .Select(i => new object?[] { i + 1, Names.Author(i), Librarion.Core.Tagging.TagNames.PersonSortName(Names.Author(i)) }));
                Rows(connection, "INSERT INTO series (id, name, sort) VALUES ($1, $2, $2)", Enumerable.Range(0, shape.Series).Select(i => new object?[] { i + 1, Names.Series(i) }));
                Rows(connection, "INSERT INTO publishers (id, name, sort) VALUES ($1, $2, $2)", Enumerable.Range(0, shape.Publishers).Select(i => new object?[] { i + 1, Names.Publisher(i) }));
                Rows(connection, "INSERT INTO languages (id, lang_code) VALUES ($1, $2)", Names.Languages.Select((l, i) => new object?[] { i + 1, l }));
                Rows(connection, "INSERT INTO tags (id, name) VALUES ($1, $2)", Enumerable.Range(0, shape.PlainTags).Select(i => new object?[] { i + 1, Names.PlainTag(i) })
                    .Concat(Enumerable.Range(0, shape.Categories).Select(i => new object?[] { shape.PlainTags + i + 1, Names.Category(i).Name })));
                Rows(connection, "INSERT INTO ratings (id, rating) VALUES ($1, $1)", Enumerable.Range(1, 10).Select(i => new object?[] { i }));
                transaction.Commit();
            }

            var started = DateTime.UtcNow;
            for (var from = 1; from <= shape.Books; from += 10_000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var transaction = connection.BeginTransaction();
                var plans = Enumerable.Range(from, Math.Min(10_000, shape.Books - from + 1)).Select(generator.Plan).ToList();
                Rows(connection, "INSERT INTO books (id, title, sort, pubdate, series_index, path, uuid, has_cover) VALUES ($1, $2, $2, $3, $4, $5, $6, 0)", plans.Select(p => new object?[]
                {
                    p.Id, p.Title ?? "Unknown", p.Year is { } y ? $"{CatalogShape.FirstYear + y}-06-01 00:00:00+00:00" : "0101-01-01 00:00:00+00:00", p.SeriesNumber ?? 1.0,
                    $"b{p.Id / 1000:D4}", $"uuid-{p.Id}",
                }));
                Rows(connection, "INSERT INTO books_authors_link (book, author) VALUES ($1, $2)", plans.SelectMany(p => p.Authors.Select(a => new object?[] { p.Id, a + 1 })));
                Rows(connection, "INSERT INTO books_series_link (book, series) VALUES ($1, $2)", plans.Where(p => p.Series is not null).Select(p => new object?[] { p.Id, p.Series + 1 }));
                Rows(connection, "INSERT INTO books_publishers_link (book, publisher) VALUES ($1, $2)", plans.Where(p => p.Publisher is not null).Select(p => new object?[] { p.Id, p.Publisher + 1 }));
                Rows(connection, "INSERT INTO books_languages_link (book, lang_code) VALUES ($1, $2)", plans.Select(p => new object?[] { p.Id, p.Language + 1 }));
                Rows(connection, "INSERT INTO books_tags_link (book, tag) VALUES ($1, $2)", plans.SelectMany(p => p.PlainTags.Select(t => new object?[] { p.Id, t + 1 })
                    .Concat(p.Categories.Select(c => new object?[] { p.Id, shape.PlainTags + c + 1 }))));
                Rows(connection, "INSERT INTO books_ratings_link (book, rating) VALUES ($1, $2)", plans.Where(p => p.Rating is > 0).Select(p => new object?[] { p.Id, (int)p.Rating! }));
                Rows(connection, "INSERT INTO comments (book, text) VALUES ($1, $2)", plans.Where(p => p.Description is not null).Select(p => new object?[] { p.Id, $"<p>{p.Description}</p>" }));
                Rows(connection, "INSERT INTO identifiers (book, type, val) VALUES ($1, 'isbn', $2)", plans.Where(p => p.Isbn is not null).Select(p => new object?[] { p.Id, p.Isbn }));
                Rows(connection, "INSERT INTO data (book, format, uncompressed_size, name) VALUES ($1, 'EPUB', 0, $2)", plans.Select(p => new object?[] { p.Id, p.Id.ToString(CultureInfo.InvariantCulture) }));
                transaction.Commit();
                foreach (var plan in plans)
                {
                    var bookFolder = Path.Combine(folder, $"b{plan.Id / 1000:D4}");
                    if (plan.Id % 1000 == 0 || plan.Id == from)
                        Directory.CreateDirectory(bookFolder);
                    File.Create(Path.Combine(bookFolder, $"{plan.Id}.epub")).Dispose();
                }

                if (from % 100_000 == 1)
                    report($"calibre: {from + plans.Count - 1:N0} books ({(DateTime.UtcNow - started).TotalSeconds:0} s)");
            }
        }

        Indexes(temp);
        File.Move(temp, db);
        return db;
    }

    /// <summary>The indexes Calibre makes on its link tables (by book and by value): its database always has them, and reading by book needs them.</summary>
    private static void Indexes(string db)
    {
        using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
        connection.Open();
        Execute(connection, """
            CREATE INDEX IF NOT EXISTS books_authors_link_aidx ON books_authors_link (author);
            CREATE INDEX IF NOT EXISTS books_authors_link_bidx ON books_authors_link (book);
            CREATE INDEX IF NOT EXISTS books_series_link_bidx ON books_series_link (book);
            CREATE INDEX IF NOT EXISTS books_publishers_link_bidx ON books_publishers_link (book);
            CREATE INDEX IF NOT EXISTS books_languages_link_bidx ON books_languages_link (book);
            CREATE INDEX IF NOT EXISTS books_tags_link_bidx ON books_tags_link (book);
            CREATE INDEX IF NOT EXISTS books_ratings_link_bidx ON books_ratings_link (book);
            CREATE INDEX IF NOT EXISTS comments_idx ON comments (book);
            CREATE INDEX IF NOT EXISTS formats_idx ON data (format);
            CREATE INDEX IF NOT EXISTS data_idx ON data (book);
            CREATE INDEX IF NOT EXISTS identifiers_bidx ON identifiers (book);
            """);
    }

    /// <summary>An INPX of <paramref name="shape"/>'s books and its zips in <paramref name="folder"/>; the <c>.inpx</c> path. One record in a hundred is deleted.</summary>
    public static string Inpx(CatalogShape shape, string folder, Action<string> report, CancellationToken cancellationToken)
    {
        var inpx = Path.Combine(folder, "bench.inpx");
        if (File.Exists(inpx))
            return inpx;
        Directory.CreateDirectory(folder);
        var generator = Generator(shape);
        var temp = inpx + ".tmp";
        var started = DateTime.UtcNow;
        using (var file = File.Create(temp))
        using (var catalog = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using (var info = new StreamWriter(catalog.CreateEntry("collection.info").Open(), new UTF8Encoding(false)))
                info.Write("Benchmark collection\nbench\n65536\nGenerated\n");
            for (var from = 1; from <= shape.Books; from += InpxEntriesPerZip)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var to = Math.Min(shape.Books, from + InpxEntriesPerZip - 1);
                var name = $"fb2-{from:D7}-{to:D7}";
                using var lines = new StreamWriter(catalog.CreateEntry(name + ".inp").Open(), new UTF8Encoding(false));
                using var zipFile = File.Create(Path.Combine(folder, name + ".zip"));
                using var zip = new ZipArchive(zipFile, ZipArchiveMode.Create);
                for (var id = from; id <= to; id++)
                {
                    var plan = generator.Plan(id);
                    using (var entry = zip.CreateEntry($"{id}.fb2", CompressionLevel.NoCompression).Open())
                        entry.WriteByte((byte)'x');
                    var authors = string.Concat(plan.Authors.Select(a => Names.Author(a).Split(' ', 2) is [var first, var last] ? $"{last},{first},:" : $"{Names.Author(a)},,:"));
                    var genres = string.Concat(plan.Categories.Select(c => GenreCodes[c % GenreCodes.Length] + ":"));
                    var keywords = string.Join(", ", plan.PlainTags.Select(Names.PlainTag));
                    lines.Write(string.Join('\x04', authors, genres, plan.Title ?? "", plan.Series is { } s ? Names.Series(s) : "",
                        plan.SeriesNumber is { } n ? ((int)n).ToString(CultureInfo.InvariantCulture) : "", id.ToString(CultureInfo.InvariantCulture), "1",
                        id.ToString(CultureInfo.InvariantCulture), id % 100 == 50 ? "1" : "0", "fb2", "2024-01-01", Names.Languages[plan.Language],
                        plan.Rating is { } r ? (r / 2).ToString(CultureInfo.InvariantCulture) : "", keywords) + "\x04\r\n");
                }

                if (to % 100_000 == 0 || to == shape.Books)
                    report($"inpx: {to:N0} records ({(DateTime.UtcNow - started).TotalSeconds:0} s)");
            }
        }

        File.Move(temp, inpx);
        return inpx;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>One prepared insert for many rows (parameters <c>$1</c>…).</summary>
    private static void Rows(SqliteConnection connection, string sql, IEnumerable<object?[]> rows)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        SqliteParameter[]? parameters = null;
        foreach (var row in rows)
        {
            if (parameters is null)
            {
                parameters = Enumerable.Range(1, row.Length).Select(i => command.Parameters.Add(new SqliteParameter($"${i}", null))).ToArray();
                command.Prepare();
            }

            for (var i = 0; i < row.Length; i++)
                parameters[i].Value = row[i] ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
    }
}
