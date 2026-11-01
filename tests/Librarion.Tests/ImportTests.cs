using System.Net;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Imports;
using Librarion.Core.Tasks;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>
/// Imports from another source (agents/docs/features/imports.md): records become books with their files, matched to
/// books already there, owned by the source afterwards (a scan keeps their metadata), skipped when unchanged, never
/// removed.
/// </summary>
[TestClass]
public sealed class ImportTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static ImportRecord Record(string id, string title, string author, params ImportFile[] files) =>
        new(id, new BookMetadata(ImportTagSourceKind.Calibre, title, Values: [new FieldValue("author", author, 0, SortName: SortOf(author))], Tags: ["Imported tag"],
            Rating: 8, SortTitle: title.StartsWith("The ", StringComparison.Ordinal) ? title[4..] + ", The" : null), files);

    private static string SortOf(string author) => author.Split(' ') is [var first, var last] ? $"{last}, {first}" : author;

    private static Task<ScanResult> ImportAsync(ScannedLibrary library, params ImportRecord[] records) => ImportAsync(library, null, records);

    private static async Task<ScanResult> ImportAsync(ScannedLibrary library, int? rootId, params ImportRecord[] records)
    {
        if (rootId is null)
        {
            await using var db = await library.DbAsync(CancellationToken.None);
            rootId = await db.LibraryRoots.Where(r => r.LibraryId == library.LibraryId).Select(r => r.Id).FirstAsync();
        }

        return await library.Service<CatalogImporter>().ImportAsync(library.TenantId, library.LibraryId, rootId.Value, () => new TestSource(records), null, CancellationToken.None);
    }

    [TestMethod]
    public async Task RecordsBecomeBooksWithAllTheirFilesOwnedByTheSource()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var epub = library.Write("Tolkien/The Hobbit/The Hobbit.epub", "epub bytes");
        var fb2 = library.Write("Tolkien/The Hobbit/The Hobbit.fb2", "fb2 bytes");
        var other = library.Write("King/It/It.fb2", "it");

        var result = await ImportAsync(library,
            Record("uuid-1", "The Hobbit", "John Tolkien", new ImportFile(epub), new ImportFile(fb2)),
            Record("uuid-2", "It", "Stephen King", new ImportFile(other)),
            Record("uuid-3", "Gone", "Nobody", new ImportFile(Path.Combine(library.Root, "nowhere.epub"))),
            Record("uuid-4", "Outside", "Nobody", new ImportFile(Path.Combine(Path.GetTempPath(), "outside.epub"))));

        Assert.AreEqual(2, result.Added);
        Assert.AreEqual(2, result.Skipped, "a record without a file found is never a book");
        Assert.IsTrue(result.Problems.Any(p => p.Contains("uuid-4") && p.Contains("not inside")), string.Join("; ", result.Problems));
        await using var db = await library.DbAsync(Cancel);
        var hobbit = await db.Books.Include(b => b.Files).Include(b => b.Identifiers).SingleAsync(b => b.Title == "The Hobbit", Cancel);
        Assert.HasCount(2, hobbit.Files, "the record's formats are one book");
        Assert.AreEqual(MetadataSource.Calibre, hobbit.MetadataSource);
        Assert.AreEqual((byte)8, hobbit.Rating);
        Assert.AreEqual("Tolkien, John", hobbit.SortAuthor);
        Assert.StartsWith("hobbit", hobbit.SortTitle, "the source's sort title");
        Assert.IsTrue(hobbit.Identifiers.Any(i => i.Scheme == TestSource.Scheme && i.Value == "uuid-1"));
        Assert.AreEqual(2, (await db.Libraries.SingleAsync(l => l.Id == library.LibraryId, Cancel)).BookCount);
        var author = await db.Tags.SingleAsync(t => t.Name == "John Tolkien", Cancel);
        Assert.AreEqual(1, (await db.TagCounts.SingleAsync(c => c.TagId == author.Id, Cancel)).BookCount);
    }

    [TestMethod]
    public async Task AnUnchangedReImportWritesNothingAndAChangedOneKeepsManualTags()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var path = library.Write("a/book.epub", "bytes");
        await ImportAsync(library, Record("u1", "First title", "Ann Writer", new ImportFile(path)));
        int bookId, manual;
        await using (var db = await library.DbAsync(Cancel))
        {
            bookId = await db.Books.Select(b => b.Id).SingleAsync(Cancel);
            var tag = new Tag { TenantId = library.TenantId, Name = "Favourite", NormalizedName = "favourite", SortName = "favourite" };
            db.Tags.Add(tag);
            await db.SaveChangesAsync(Cancel);
            manual = tag.Id;
            db.BookTags.Add(new BookTag { TenantId = library.TenantId, BookId = bookId, TagId = manual, Source = TagSource.Manual });
            await db.SaveChangesAsync(Cancel);
        }

        var again = await ImportAsync(library, Record("u1", "First title", "Ann Writer", new ImportFile(path)));
        Assert.AreEqual((0, 0, 1), (again.Added, again.Changed, again.Unchanged));

        var changed = await ImportAsync(library, Record("u1", "Second title", "Bob Writer", new ImportFile(path)));
        Assert.AreEqual((0, 1), (changed.Added, changed.Changed));
        await using (var db = await library.DbAsync(Cancel))
        {
            var book = await db.Books.SingleAsync(Cancel);
            Assert.AreEqual((bookId, "Second title"), (book.Id, book.Title));
            Assert.IsTrue(await db.BookTags.AnyAsync(l => l.BookId == bookId && l.TagId == manual && l.Source == TagSource.Manual, Cancel), "manual tags stay");
            var authors = await db.BookTags.Where(l => l.BookId == bookId && l.Tag!.Field!.FieldKey == "author").Select(l => l.Tag!.Name).ToListAsync(Cancel);
            CollectionAssert.AreEqual(new[] { "Bob Writer" }, authors, "the imported links are the record's");
            var ann = await db.Tags.Where(t => t.Name == "Ann Writer").Select(t => t.Id).SingleAsync(Cancel);
            Assert.IsFalse(await db.TagCounts.AnyAsync(c => c.TagId == ann, Cancel), "the old author's count went");
        }
    }

    [TestMethod]
    public async Task AScannedBookIsTakenOverAndALaterScanKeepsTheImportedMetadata()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var path = library.Write("a/book.fb2", "first");
        var heads = new FixedReader("Title from the head");
        await library.ScanAsync(Cancel, heads);
        int bookId;
        await using (var db = await library.DbAsync(Cancel))
        {
            var book = await db.Books.SingleAsync(Cancel);
            (bookId, var title, var source) = (book.Id, book.Title, book.MetadataSource);
            Assert.AreEqual(("Title from the head", MetadataSource.Heads), (title, source));
        }

        var imported = await ImportAsync(library, Record("u1", "Title from Calibre", "Ann Writer", new ImportFile(path)));
        Assert.AreEqual((0, 1), (imported.Added, imported.Changed), "matched by its file, not a new book");

        File.WriteAllText(path, "changed bytes, longer");
        var scan = await library.ScanAsync(Cancel, heads);
        Assert.AreEqual(1, scan.Changed);
        await using (var db = await library.DbAsync(Cancel))
        {
            var book = await db.Books.SingleAsync(Cancel);
            Assert.AreEqual((bookId, "Title from Calibre", MetadataSource.Calibre), (book.Id, book.Title, book.MetadataSource), "the scan updated the file only");
            Assert.AreEqual(new FileInfo(path).Length, (await db.BookFiles.SingleAsync(Cancel)).Size);
            Assert.IsTrue(await db.BookTags.AnyAsync(l => l.BookId == bookId && l.Tag!.Name == "Ann Writer", Cancel));
        }
    }

    [TestMethod]
    public async Task ArchiveEntriesAreImportedAndALaterScanSkipsTheArchive()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var zip = library.Zip("lib/fb2-000001-000003.zip", ("1.fb2", "one"), ("2.fb2", "two"), ("3.fb2", "three, deleted in the catalog"));

        var result = await ImportAsync(library,
            Record("lib:1", "One", "Ann Writer", new ImportFile(zip, "1.fb2")),
            Record("lib:2", "Two", "Ann Writer", new ImportFile(zip, "2.fb2")),
            Record("lib:9", "Not in the zip", "Ann Writer", new ImportFile(zip, "9.fb2")));
        Assert.AreEqual((2, 1), (result.Added, result.Skipped));
        await using (var db = await library.DbAsync(Cancel))
        {
            var archive = await db.Archives.SingleAsync(Cancel);
            Assert.AreEqual((ArchiveScanState.Done, 2), (archive.ScanState, archive.EntryCount));
            var file = await db.BookFiles.SingleAsync(f => f.Path == "1.fb2", Cancel);
            Assert.IsNotNull(file.Crc32);
            Assert.IsNotNull(file.Locator);
        }

        var scan = await library.ScanAsync(Cancel);
        Assert.AreEqual((1, 0, 0), (scan.ArchivesSkipped, scan.Added, scan.Missing), "an imported archive is not read again by a scan");
    }

    [TestMethod]
    public async Task AMovedBookIsFoundBySourceIdAndGetsItsNewFile()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var old = library.Write("old/book.epub", "bytes");
        await ImportAsync(library, Record("u1", "Book", "Ann Writer", new ImportFile(old)));
        File.Delete(old);
        await library.ScanAsync(Cancel);
        var moved = library.Write("new/book.epub", "bytes");

        var result = await ImportAsync(library, Record("u1", "Book", "Ann Writer", new ImportFile(moved)));
        Assert.AreEqual((0, 1), (result.Added, result.Changed));
        await using var db = await library.DbAsync(Cancel);
        var book = await db.Books.Include(b => b.Files).SingleAsync(Cancel);
        Assert.IsNull(book.MissingSince, "the missing book is back");
        Assert.IsTrue(book.Files.Any(f => f.Path == "new/book.epub" && f.MissingSince == null));
        Assert.AreEqual(1, (await db.Libraries.SingleAsync(l => l.Id == library.LibraryId, Cancel)).BookCount);
    }

    [TestMethod]
    public async Task TheRouteChecksTheSourceAndAddsTheFilesFolderAsARoot()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel, services => services.AddSingleton<ImportFormat, TestFormat>());
        var (admin, _) = await library.AdminAsync(Cancel);
        var folder = Path.Combine(Path.GetDirectoryName(library.Root)!, "calibre");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "book.epub"), "bytes");
        var source = Path.Combine(folder, "catalog.test");
        File.WriteAllText(source, "u1|A book|book.epub");
        File.WriteAllText(Path.Combine(folder, "wrong.test"), "not a catalog");

        async Task<HttpResponseMessage> Post(ImportInput input) => await admin.PostAsJsonAsync($"/api/libraries/{library.LibraryId}/import", input, ApiClients.Json, Cancel);

        Assert.AreEqual(HttpStatusCode.OK, (await admin.GetAsync($"/api/libraries/{library.LibraryId}/import/kinds", Cancel)).StatusCode);
        using (var unknown = await Post(new ImportInput("nope", source)))
            StringAssert.Contains(await unknown.Content.ReadAsStringAsync(Cancel), "kind.unknown");
        using (var missing = await Post(new ImportInput("test", Path.Combine(folder, "none.test"))))
            StringAssert.Contains(await missing.Content.ReadAsStringAsync(Cancel), "source.missing");
        using (var wrong = await Post(new ImportInput("test", Path.Combine(folder, "wrong.test"))))
            StringAssert.Contains(await wrong.Content.ReadAsStringAsync(Cancel), "source.invalid");

        using var started = await Post(new ImportInput("test", source));
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode, await started.Content.ReadAsStringAsync(Cancel));
        var run = (await started.Content.ReadFromJsonAsync<TaskStarted>(ApiClients.Json, Cancel))!;
        TaskRunInfo info = null!;
        for (var i = 0; i < 300; i++)
        {
            info = await admin.GetAsync<TaskRunInfo>($"/api/tasks/{run.RunId}", Cancel);
            if (info.Outcome != TaskOutcome.Running)
                break;
            await Task.Delay(50, Cancel);
        }

        Assert.AreEqual(TaskOutcome.Succeeded, info.Outcome, info.Summary);
        Assert.AreEqual(BackgroundTaskKind.Import, info.Task);
        Assert.AreEqual(1, info.Counts!.Added);
        await using var db = await library.DbAsync(Cancel);
        Assert.IsTrue(await db.LibraryRoots.AnyAsync(r => r.Path == folder, Cancel), "the files' folder became a root of the library");
        Assert.AreEqual("A book", (await db.Books.SingleAsync(Cancel)).Title);
    }

    /// <summary>A source of records given by the test.</summary>
    private sealed class TestSource(IReadOnlyList<ImportRecord> records) : IImportSource
    {
        public const string Scheme = "test-id";

        public MetadataSource Owner => MetadataSource.Calibre;
        public string IdScheme => Scheme;

        public IEnumerable<ImportRecord> Read(ImportReport report, CancellationToken cancellationToken) => records;

        public void Dispose() { }
    }

    /// <summary>A kind of source for the route: lines of <c>id|title|file</c>, the files next to it.</summary>
    private sealed class TestFormat : ImportFormat
    {
        public override string Key => "test";

        public override string? Check(string path) => File.ReadAllText(path).Contains('|') ? null : "no records";

        public override IImportSource Open(string path, string filesFolder) => new TestSource(File.ReadAllLines(path).Select(line => line.Split('|'))
            .Select(p => new ImportRecord(p[0], new BookMetadata(ImportTagSourceKind.Calibre, p[1]), [new ImportFile(Path.Combine(Path.GetDirectoryName(path)!, p[2]))]))
            .ToList());
    }

    /// <summary>Every book's head says the same title.</summary>
    private sealed class FixedReader(string title) : IMetadataReader
    {
        public BookMetadata? Read(string format, Func<Stream> open, long size, CancellationToken cancellationToken) => new(ImportTagSourceKind.Fb2, title);
    }
}
