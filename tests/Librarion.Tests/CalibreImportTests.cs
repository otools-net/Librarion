using System.Net;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Imports;
using Librarion.Core.Imports.Calibre;
using Librarion.Core.Tasks;
using Librarion.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Librarion.Tests;

/// <summary>
/// Calibre libraries imported (agents/docs/features/imports.md#calibre): every kind of value a <c>metadata.db</c> holds,
/// older and newer schemas, a database in WAL mode being written meanwhile, the cover, and the route.
/// </summary>
[TestClass]
public sealed class CalibreImportTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    [TestMethod]
    public async Task EveryValueOfAModernLibraryComesIn()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var calibre = CalibreLibraries.Modern(Path.Combine(library.Root, "Calibre"));

        var result = await ImportAsync(library, calibre);
        Assert.AreEqual(2, result.Added, string.Join("; ", result.Problems));
        Assert.AreEqual(1, result.Skipped, "the book without a file");

        await using var db = await library.DbAsync(Cancel);
        var book = await db.Books.Include(b => b.Files).Include(b => b.Identifiers).Include(b => b.Properties).SingleAsync(b => b.Title == "The Hobbit", Cancel);
        Assert.AreEqual(MetadataSource.Calibre, book.MetadataSource);
        Assert.HasCount(2, book.Files, "epub and fb2 of one book");
        Assert.AreEqual((byte)8, book.Rating);
        Assert.AreEqual("Hobbit, The there & back", book.Description, "comments as text");
        Assert.AreEqual(CoverSource.Calibre, book.CoverSource);
        Assert.AreEqual("Tolkien, J. R. R.", book.SortAuthor);
        Assert.AreEqual(2.5, book.SeriesNumber);
        CollectionAssert.IsSubsetOf(new[] { ("isbn", "9780261103344"), ("goodreads", "5907"), ("calibre-uuid", "uuid-hobbit") },
            book.Identifiers.Select(i => (i.Scheme, i.Value)).ToList());
        var properties = book.Properties.ToDictionary(p => p.Name, p => p.Value);
        Assert.AreEqual("true", properties["Read"]);
        Assert.AreEqual("310", properties["Pages"]);
        Assert.AreEqual("Re-read in winter.", properties["Notes"]);
        Assert.AreEqual("2020-01-02", properties["Bought"]);
        Assert.IsFalse(properties.ContainsKey("Gone"), "a column marked for deletion is not read");

        var tags = await db.BookTags.Where(l => l.BookId == book.Id).Select(l => new { Field = l.Tag!.Field!.FieldKey, l.Tag.Name, l.Number, l.Ordinal }).ToListAsync(Cancel);
        CollectionAssert.AreEqual(new[] { "J. R. R. Tolkien", "Christopher Tolkien, Jr." },
            tags.Where(t => t.Field == "author").OrderBy(t => t.Ordinal).Select(t => t.Name).ToList(), "authors in order, '|' back to a comma");
        Assert.IsTrue(tags.Any(t => t is { Field: "series", Name: "Middle-earth", Number: 2.5 }));
        Assert.IsTrue(tags.Any(t => t is { Field: "publisher", Name: "Allen & Unwin" }));
        Assert.IsTrue(tags.Any(t => t is { Field: "language", Name: "en" }), "eng becomes en");
        Assert.IsTrue(tags.Any(t => t is { Field: "year", Name: "1937" }));
        Assert.IsTrue(tags.Any(t => t is { Field: null, Name: "Fantasy" }), "a Calibre tag is a plain tag");
        Assert.IsTrue(tags.Any(t => t is { Field: "genre", Name: "Adventure" }) && tags.Any(t => t is { Field: "genre", Name: "Classic" }), "a multiple text column: a field");
        Assert.IsTrue(tags.Any(t => t is { Field: "my-series", Name: "Reading list", Number: 3 }), "a series column with its number");
        Assert.IsTrue(tags.Any(t => t is { Field: "stars", Name: "4.5" }), "a rating column in stars");

        var plain = await db.Books.SingleAsync(b => b.Title == "Untitled date", Cancel);
        Assert.IsFalse(await db.BookTags.AnyAsync(l => l.BookId == plain.Id && l.Tag!.Field!.FieldKey == "year", Cancel), "Calibre's undefined date is no year");
        Assert.AreEqual(CoverSource.Generated, plain.CoverSource, "no cover in Calibre, a PDF has none of its own");
    }

    [TestMethod]
    public async Task AnUnchangedReImportIsSkippedAndTheCoverIsCalibres()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var calibre = CalibreLibraries.Modern(Path.Combine(library.Root, "Calibre"));
        await ImportAsync(library, calibre);

        var again = await ImportAsync(library, calibre);
        Assert.AreEqual((0, 0, 2), (again.Added, again.Changed, again.Unchanged));

        int hobbit;
        await using (var db = await library.DbAsync(Cancel))
            hobbit = await db.Books.Where(b => b.Title == "The Hobbit").Select(b => b.Id).SingleAsync(Cancel);
        var cover = await library.Service<Librarion.Core.Covers.CoverService>().GetAsync(library.TenantId, new BookVisibility(new Dictionary<int, IReadOnlyList<FilterProfile>> { [library.LibraryId] = [new FilterProfile([], [])] }), hobbit,
            Librarion.Core.Covers.CoverSize.M, Cancel);
        Assert.IsNotNull(cover);
        using var bitmap = SKBitmap.Decode(cover.Path);
        var pixel = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        Assert.IsTrue(pixel.Red > 200 && pixel.Green < 80 && pixel.Blue < 80, $"Calibre's red cover.jpg: {pixel}");
    }

    [TestMethod]
    public async Task AnOldLibraryWithoutIdentifiersLanguagesOrCustomColumnsIsRead()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var calibre = CalibreLibraries.Old(Path.Combine(library.Root, "Old"));

        var result = await ImportAsync(library, calibre);
        Assert.AreEqual(1, result.Added, string.Join("; ", result.Problems));
        await using var db = await library.DbAsync(Cancel);
        var book = await db.Books.Include(b => b.Identifiers).SingleAsync(Cancel);
        Assert.AreEqual("Old Book", book.Title);
        CollectionAssert.IsSubsetOf(new[] { ("isbn", "0261103342"), ("calibre-uuid", "id:1") }, book.Identifiers.Select(i => (i.Scheme, i.Value)).ToList(),
            "the isbn column of old versions; no uuid: the id");
    }

    [TestMethod]
    public async Task AWalDatabaseIsReadWhileCalibreWritesIt()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var calibre = CalibreLibraries.Modern(Path.Combine(library.Root, "Calibre"));
        await using var writer = new SqliteConnection($"Data Source={calibre};Pooling=False");
        await writer.OpenAsync(Cancel);
        await using (var wal = writer.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL; BEGIN IMMEDIATE; UPDATE books SET title = 'Being edited' WHERE id = 1;";
            await wal.ExecuteNonQueryAsync(Cancel);
        }

        var result = await ImportAsync(library, calibre);
        Assert.AreEqual(2, result.Added, string.Join("; ", result.Problems));
        await using var db = await library.DbAsync(Cancel);
        Assert.IsTrue(await db.Books.AnyAsync(b => b.Title == "The Hobbit", Cancel), "what was committed is read; the open write isn't");
    }

    [TestMethod]
    public void OnlyACalibreDatabaseIsAccepted()
    {
        var folder = Path.Combine(Path.GetTempPath(), "librarion-calibre-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var format = new CalibreFormat();
            Assert.IsNull(format.Check(CalibreLibraries.Old(Path.Combine(folder, "lib"))));
            var text = Path.Combine(folder, "text.db");
            File.WriteAllText(text, "not a database at all, just some text that is long enough to be read as a header");
            Assert.IsNotNull(format.Check(text));
            var other = Path.Combine(folder, "other.db");
            using (var connection = new SqliteConnection($"Data Source={other};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE things (id INTEGER PRIMARY KEY)";
                command.ExecuteNonQuery();
            }

            StringAssert.Contains(format.Check(other), "books");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task TheRouteImportsACalibreLibrary()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var (admin, _) = await library.AdminAsync(Cancel);
        var calibre = CalibreLibraries.Modern(Path.Combine(Path.GetDirectoryName(library.Root)!, "Calibre Library"));

        using var response = await admin.PostAsJsonAsync($"/api/libraries/{library.LibraryId}/import", new ImportInput("calibre", calibre), ApiClients.Json, Cancel);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
        var run = (await response.Content.ReadFromJsonAsync<TaskStarted>(ApiClients.Json, Cancel))!;
        TaskRunInfo info = null!;
        for (var i = 0; i < 300; i++)
        {
            info = await admin.GetAsync<TaskRunInfo>($"/api/tasks/{run.RunId}", Cancel);
            if (info.Outcome != TaskOutcome.Running)
                break;
            await Task.Delay(50, Cancel);
        }

        Assert.AreEqual(TaskOutcome.Succeeded, info.Outcome, info.Summary);
        Assert.AreEqual((2, 1), (info.Counts!.Added, info.Counts.Skipped));
        var page = await admin.GetAsync<Page<BookListItem>>($"/api/books?library={library.LibraryId}", Cancel);
        Assert.IsTrue(page.Items.Any(b => b.Title == "The Hobbit"));
        var detail = await admin.GetAsync<BookDetail>($"/api/books/{page.Items.Single(b => b.Title == "The Hobbit").Id}", Cancel);
        Assert.IsTrue(detail.Properties!.Any(p => p.Key == "Pages" && p.Value == "310"), "properties on the book's page");
    }

    private static async Task<Librarion.Core.Import.ScanResult> ImportAsync(ScannedLibrary library, string metadataDb)
    {
        var (_, root, _) = await library.Service<Librarion.Core.Import.LibraryRoots>().RootForAsync(library.TenantId, library.LibraryId, Path.GetDirectoryName(metadataDb), true,
            CancellationToken.None);
        return await library.Service<CatalogImporter>().ImportAsync(library.TenantId, library.LibraryId, root!.Id, () => new CalibreSource(metadataDb), null, CancellationToken.None);
    }
}

/// <summary>Calibre libraries written by the tests from SQL (never committed binaries): the tables Calibre makes, and the books' files.</summary>
internal static class CalibreLibraries
{
    private const string CoreTables = """
        CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT NOT NULL, sort TEXT, link TEXT NOT NULL DEFAULT '');
        CREATE TABLE books_authors_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, author INTEGER NOT NULL);
        CREATE TABLE series (id INTEGER PRIMARY KEY, name TEXT NOT NULL, sort TEXT);
        CREATE TABLE books_series_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, series INTEGER NOT NULL);
        CREATE TABLE publishers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, sort TEXT);
        CREATE TABLE books_publishers_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, publisher INTEGER NOT NULL);
        CREATE TABLE tags (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE books_tags_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, tag INTEGER NOT NULL);
        CREATE TABLE ratings (id INTEGER PRIMARY KEY, rating INTEGER);
        CREATE TABLE books_ratings_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, rating INTEGER NOT NULL);
        CREATE TABLE comments (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, text TEXT NOT NULL);
        CREATE TABLE data (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, format TEXT NOT NULL, uncompressed_size INTEGER NOT NULL, name TEXT NOT NULL);
        """;

    /// <summary>A library of a recent Calibre: identifiers, languages, custom columns of every kind, a cover; its <c>metadata.db</c> path.</summary>
    public static string Modern(string folder)
    {
        Directory.CreateDirectory(folder);
        var db = Path.Combine(folder, "metadata.db");
        Run(db, CoreTables + """
            CREATE TABLE books (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL DEFAULT 'Unknown', sort TEXT, timestamp TIMESTAMP,
                pubdate TIMESTAMP DEFAULT '0101-01-01 00:00:00+00:00', series_index REAL NOT NULL DEFAULT 1.0, author_sort TEXT, isbn TEXT DEFAULT '',
                lccn TEXT DEFAULT '', path TEXT NOT NULL DEFAULT '', flags INTEGER NOT NULL DEFAULT 1, uuid TEXT, has_cover BOOL DEFAULT 0, last_modified TIMESTAMP);
            CREATE TABLE languages (id INTEGER PRIMARY KEY, lang_code TEXT NOT NULL);
            CREATE TABLE books_languages_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, lang_code INTEGER NOT NULL, item_order INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE identifiers (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, type TEXT NOT NULL DEFAULT 'isbn', val TEXT NOT NULL);
            CREATE TABLE custom_columns (id INTEGER PRIMARY KEY AUTOINCREMENT, label TEXT NOT NULL, name TEXT NOT NULL, datatype TEXT NOT NULL,
                mark_for_delete BOOL DEFAULT 0 NOT NULL, editable BOOL DEFAULT 1 NOT NULL, display TEXT DEFAULT '{}' NOT NULL, is_multiple BOOL DEFAULT 0 NOT NULL, normalized BOOL NOT NULL);
            CREATE TABLE some_future_table (id INTEGER PRIMARY KEY);

            INSERT INTO books (id, title, sort, pubdate, series_index, path, uuid, has_cover) VALUES
                (1, 'The Hobbit', 'Hobbit, The', '1937-09-21 00:00:00+00:00', 2.5, 'J. R. R. Tolkien/The Hobbit (1)', 'uuid-hobbit', 1),
                (2, 'Untitled date', 'Untitled date', '0101-01-01 00:00:00+00:00', 1.0, 'Nobody/Untitled date (2)', 'uuid-plain', 0),
                (3, 'No file', 'No file', '2001-01-01 00:00:00+00:00', 1.0, 'Nobody/No file (3)', 'uuid-nofile', 0);
            INSERT INTO authors (id, name, sort) VALUES (1, 'J. R. R. Tolkien', 'Tolkien, J. R. R.'), (2, 'Christopher Tolkien| Jr.', 'Tolkien, Christopher| Jr.'), (3, 'Nobody', 'Nobody');
            INSERT INTO books_authors_link (id, book, author) VALUES (1, 1, 1), (2, 1, 2), (3, 2, 3), (4, 3, 3);
            INSERT INTO series VALUES (1, 'Middle-earth', 'Middle-earth');
            INSERT INTO books_series_link (book, series) VALUES (1, 1);
            INSERT INTO publishers VALUES (1, 'Allen & Unwin', 'Allen & Unwin');
            INSERT INTO books_publishers_link (book, publisher) VALUES (1, 1);
            INSERT INTO languages VALUES (1, 'eng');
            INSERT INTO books_languages_link (book, lang_code) VALUES (1, 1);
            INSERT INTO tags VALUES (1, 'Fantasy');
            INSERT INTO books_tags_link (book, tag) VALUES (1, 1);
            INSERT INTO ratings VALUES (1, 8);
            INSERT INTO books_ratings_link (book, rating) VALUES (1, 1);
            INSERT INTO comments (book, text) VALUES (1, '<div><p>Hobbit, The</p><p>there &amp; back</p></div>');
            INSERT INTO identifiers (book, type, val) VALUES (1, 'isbn', '978-0-261-10334-4'), (1, 'goodreads', '5907');
            INSERT INTO data (book, format, uncompressed_size, name) VALUES (1, 'EPUB', 10, 'The Hobbit - J. R. R. Tolkien'), (1, 'FB2', 10, 'The Hobbit - J. R. R. Tolkien'),
                (2, 'PDF', 10, 'Untitled date - Nobody'), (3, 'EPUB', 10, 'No file - Nobody');

            INSERT INTO custom_columns (id, label, name, datatype, is_multiple, normalized, mark_for_delete) VALUES
                (1, 'genre', 'Genre', 'text', 1, 1, 0), (2, 'read', 'Read', 'bool', 0, 0, 0), (3, 'pages', 'Pages', 'int', 0, 0, 0),
                (4, 'notes', 'Notes', 'comments', 0, 0, 0), (5, 'my_series', 'My series', 'series', 0, 1, 0), (6, 'stars', 'Stars', 'rating', 0, 1, 0),
                (7, 'gone', 'Gone', 'text', 0, 0, 1), (8, 'computed', 'Computed', 'composite', 0, 0, 0), (9, 'bought', 'Bought', 'datetime', 0, 0, 0);
            CREATE TABLE custom_column_1 (id INTEGER PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE books_custom_column_1_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, value INTEGER NOT NULL);
            INSERT INTO custom_column_1 VALUES (1, 'Adventure'), (2, 'Classic');
            INSERT INTO books_custom_column_1_link (book, value) VALUES (1, 1), (1, 2);
            CREATE TABLE custom_column_2 (id INTEGER PRIMARY KEY, book INTEGER, value BOOL NOT NULL);
            INSERT INTO custom_column_2 (book, value) VALUES (1, 1);
            CREATE TABLE custom_column_3 (id INTEGER PRIMARY KEY, book INTEGER, value INTEGER NOT NULL);
            INSERT INTO custom_column_3 (book, value) VALUES (1, 310);
            CREATE TABLE custom_column_4 (id INTEGER PRIMARY KEY, book INTEGER, value TEXT NOT NULL);
            INSERT INTO custom_column_4 (book, value) VALUES (1, '<p>Re-read in winter.</p>');
            CREATE TABLE custom_column_5 (id INTEGER PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE books_custom_column_5_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, value INTEGER NOT NULL, extra REAL);
            INSERT INTO custom_column_5 VALUES (1, 'Reading list');
            INSERT INTO books_custom_column_5_link (book, value, extra) VALUES (1, 1, 3);
            CREATE TABLE custom_column_6 (id INTEGER PRIMARY KEY, value INTEGER NOT NULL);
            CREATE TABLE books_custom_column_6_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, value INTEGER NOT NULL);
            INSERT INTO custom_column_6 VALUES (1, 9);
            INSERT INTO books_custom_column_6_link (book, value) VALUES (1, 1);
            CREATE TABLE custom_column_7 (id INTEGER PRIMARY KEY, book INTEGER, value TEXT NOT NULL);
            INSERT INTO custom_column_7 (book, value) VALUES (1, 'never read');
            CREATE TABLE custom_column_9 (id INTEGER PRIMARY KEY, book INTEGER, value TIMESTAMP);
            INSERT INTO custom_column_9 (book, value) VALUES (1, '2020-01-02 10:00:00+00:00');
            """);
        Book(folder, "J. R. R. Tolkien/The Hobbit (1)", "The Hobbit - J. R. R. Tolkien.epub", "epub");
        Book(folder, "J. R. R. Tolkien/The Hobbit (1)", "The Hobbit - J. R. R. Tolkien.fb2", "fb2");
        Book(folder, "Nobody/Untitled date (2)", "Untitled date - Nobody.pdf", "%PDF-1.4");
        File.WriteAllBytes(Path.Combine(folder, "J. R. R. Tolkien", "The Hobbit (1)", "cover.jpg"), Jpeg(SKColors.Red));
        return db;
    }

    /// <summary>A library of an old Calibre (before identifiers, languages, uuids and custom columns): an <c>isbn</c> column in books.</summary>
    public static string Old(string folder)
    {
        Directory.CreateDirectory(folder);
        var db = Path.Combine(folder, "metadata.db");
        Run(db, CoreTables + """
            CREATE TABLE books (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL DEFAULT 'Unknown', sort TEXT, timestamp TIMESTAMP, uri TEXT,
                series_index INTEGER NOT NULL DEFAULT 1, author_sort TEXT, isbn TEXT DEFAULT '', path TEXT NOT NULL DEFAULT '');
            INSERT INTO books (id, title, sort, isbn, path) VALUES (1, 'Old Book', 'Old Book', '0-261-10334-2', 'Someone/Old Book (1)');
            INSERT INTO authors (id, name, sort) VALUES (1, 'Someone', 'Someone');
            INSERT INTO books_authors_link (book, author) VALUES (1, 1);
            INSERT INTO data (book, format, uncompressed_size, name) VALUES (1, 'EPUB', 10, 'Old Book - Someone');
            """);
        Book(folder, "Someone/Old Book (1)", "Old Book - Someone.epub", "epub");
        return db;
    }

    private static void Book(string library, string path, string name, string text)
    {
        var folder = Path.Combine(library, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), text);
    }

    private static void Run(string db, string sql)
    {
        File.Delete(db);
        using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static byte[] Jpeg(SKColor color)
    {
        using var bitmap = new SKBitmap(60, 90);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }
}
