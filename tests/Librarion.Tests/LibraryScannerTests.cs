using System.IO.Compression;
using Librarion.Core.Access;
using Librarion.Core.Containers;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Librarion.Tests;

/// <summary>
/// Scanning a library's roots (<see cref="LibraryScanner"/>, agents/docs/features/archives.md): loose books and ZIP
/// containers in, unchanged ones skipped, changed ones diffed, gone ones out, counts kept by differences.
/// </summary>
[TestClass]
public sealed class LibraryScannerTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    [TestMethod]
    public async Task LooseBooksAndTheBooksOfAZipComeInWithTheirFileNamesAsTitles()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        library.Write("Fantasy/the_hobbit.fb2", "hobbit");
        library.Write("Fantasy/notes.txt.bak", "not a book");
        library.Zip("lib/fb2-001.zip", ("1.fb2", "one"), ("sub/2.epub", "two"), ("readme.nfo", "not a book"));

        var result = await library.ScanAsync(Cancel);

        Assert.AreEqual(3, result.Added);
        Assert.AreEqual(1, result.ArchivesRead);
        await using var db = await library.DbAsync(Cancel);
        var titles = await db.Books.OrderBy(b => b.SortTitle).Select(b => new { b.Title, b.SortTitle }).ToListAsync(Cancel);
        CollectionAssert.AreEqual(new[] { "1", "2", "hobbit" }, titles.Select(t => t.SortTitle).ToList(), "the file name, extension and article off");
        Assert.IsTrue(titles.All(t => t.Title is null), "no title in the metadata: none stored, the name is shown");
        var files = await db.BookFiles.OrderBy(f => f.Path).ToListAsync(Cancel);
        Assert.AreEqual("Fantasy/the_hobbit.fb2", files.Single(f => f.ArchiveId is null).Path, "relative to the root, / separators");
        var entry = files.Single(f => f.Path == "sub/2.epub");
        Assert.AreEqual("epub", entry.Format);
        Assert.IsNotNull(entry.Locator);
        Assert.AreEqual(System.IO.Hashing.Crc32.HashToUInt32("two"u8), (uint)entry.Crc32!.Value);
        Assert.AreEqual(3, (await db.Libraries.SingleAsync(l => l.Id == library.LibraryId, Cancel)).BookCount);

        // The stored locator opens the entry with one seek.
        var archive = await db.Archives.SingleAsync(Cancel);
        Assert.AreEqual(ArchiveScanState.Done, archive.ScanState);
        Assert.AreEqual(2, archive.EntryCount);
        Assert.AreEqual("two", library.ReadEntry("lib/fb2-001.zip", entry));
    }

    [TestMethod]
    public async Task AnUnchangedLibraryIsSkippedAndATouchedArchiveIsNotReadAgain()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        library.Write("a.fb2", "a");
        var zip = library.Zip("b.zip", ("1.fb2", "one"), ("2.fb2", "two"));
        await library.ScanAsync(Cancel);

        var again = await library.ScanAsync(Cancel);
        Assert.AreEqual(0, again.Added + again.Changed + again.Removed);
        Assert.AreEqual(1, again.ArchivesSkipped);
        Assert.AreEqual(1, again.Unchanged, "the loose file, by size and time; the archive's entries aren't looked at");

        File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddHours(-5));
        var touched = new CountingReader();
        var third = await library.ScanAsync(Cancel, touched);
        Assert.AreEqual(1, third.ArchivesSkipped, "same directory fingerprint");
        Assert.AreEqual(0, touched.Reads);
    }

    [TestMethod]
    public async Task AChangedArchiveIsDiffedByNameCrcAndSize()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var zip = library.Zip("b.zip", ("kept.fb2", "kept"), ("changed.fb2", "old"), ("gone.fb2", "gone"));
        await library.ScanAsync(Cancel);
        await using (var db = await library.DbAsync(Cancel))
            Assert.AreEqual(3, await db.Books.CountAsync(Cancel));
        int changedBook;
        await using (var db = await library.DbAsync(Cancel))
            changedBook = (await db.BookFiles.SingleAsync(f => f.Path == "changed.fb2", Cancel)).BookId;

        File.Delete(zip);
        library.Zip("b.zip", ("new.fb2", "new"), ("kept.fb2", "kept"), ("changed.fb2", "new content"));
        var reads = new CountingReader();
        var result = await library.ScanAsync(Cancel, reads);

        Assert.AreEqual(1, result.Added);
        Assert.AreEqual(1, result.Changed);
        Assert.AreEqual(1, result.Missing, "the entry gone is kept as missing, not removed");
        Assert.AreEqual(0, result.Removed);
        Assert.AreEqual(2, reads.Reads, "only the new and the changed entry are read");
        await using var check = await library.DbAsync(Cancel);
        var files = await check.BookFiles.Where(f => f.MissingSince == null).OrderBy(f => f.Path).ToListAsync(Cancel);
        CollectionAssert.AreEqual(new[] { "changed.fb2", "kept.fb2", "new.fb2" }, files.Select(f => f.Path).ToList());
        Assert.IsNotNull((await check.BookFiles.SingleAsync(f => f.Path == "gone.fb2", Cancel)).MissingSince);
        Assert.AreEqual(changedBook, files[0].BookId, "a changed file stays the same book");
        Assert.AreEqual("kept", library.ReadEntry("b.zip", files[1]), "the kept entry moved (new.fb2 is first now): its locator follows");
        Assert.AreEqual(3, (await check.Libraries.SingleAsync(l => l.Id == library.LibraryId, Cancel)).BookCount);
    }

    [TestMethod]
    public async Task BooksGoneFromTheRootAreKeptAsMissingAndAMissingRootKeepsItsBooks()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var loose = library.Write("a.fb2", "a");
        library.Write("b.fb2", "b");
        var zip = library.Zip("c.zip", ("1.fb2", "one"));
        await library.ScanAsync(Cancel);

        File.Delete(loose);
        File.Delete(zip);
        var result = await library.ScanAsync(Cancel);
        Assert.AreEqual(2, result.Missing);
        Assert.AreEqual(0, result.Removed, "nothing is removed without a user");
        await using (var db = await library.DbAsync(Cancel))
        {
            Assert.AreEqual(3, await db.Books.CountAsync(Cancel));
            Assert.AreEqual("b", (await db.Books.SingleAsync(b => b.MissingSince == null, Cancel)).SortTitle);
            Assert.IsNotNull((await db.Archives.SingleAsync(Cancel)).MissingSince);
            Assert.AreEqual(1, (await db.Libraries.SingleAsync(l => l.Id == library.LibraryId, Cancel)).BookCount, "missing books don't count");
        }

        Directory.Delete(library.Root, recursive: true);
        var unplugged = await library.ScanAsync(Cancel);
        Assert.AreEqual(0, unplugged.Removed, "an unplugged disk is not a deletion");
        Assert.HasCount(1, unplugged.MissingRoots);
    }

    [TestMethod]
    public async Task FieldValuesAndTagsBecomeTagsWithTheirCounts()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        library.Write("1.fb2", "1");
        library.Write("2.fb2", "2");
        var reader = new FakeReader(name => new BookMetadata(ImportTagSourceKind.Fb2, Title: $"Book {name}",
            Values: [new FieldValue("author", "Stephen King", 0), new FieldValue("author", name == "1.fb2" ? "Peter Straub" : "Owen King", 1),
                new FieldValue("series", "The Talisman", Number: name == "1.fb2" ? 1 : 2), new FieldValue("category", "Horror")],
            Tags: ["favourite", "Favourite "], Identifiers: [("isbn", "978" + name[0])]));

        var result = await library.ScanAsync(Cancel, reader);

        Assert.AreEqual(2, result.Added);
        await using var db = await library.DbAsync(Cancel);
        var king = await db.Tags.SingleAsync(t => t.Name == "Stephen King", Cancel);
        Assert.AreEqual("King, Stephen", king.SortName);
        Assert.AreEqual(await db.Tags.Where(t => t.FieldKey == "author").Select(t => t.Id).SingleAsync(Cancel), king.FieldId);
        Assert.AreEqual(2, (await db.TagCounts.SingleAsync(c => c.TagId == king.Id, Cancel)).BookCount);
        var favourite = await db.Tags.SingleAsync(t => t.NormalizedName == "favourite", Cancel);
        Assert.IsNull(favourite.FieldId);
        Assert.IsTrue(await db.TagParents.AnyAsync(p => p.ChildId == favourite.Id && p.Parent!.Name == "Imported: FB2", Cancel), "new plain tags wait in the source's category");
        Assert.AreEqual(2, (await db.TagCounts.SingleAsync(c => c.TagId == favourite.Id, Cancel)).BookCount, "one link per book for two spellings");
        var first = await db.Books.SingleAsync(b => b.Title == "Book 1.fb2", Cancel);
        Assert.AreEqual("King, Stephen", first.SortAuthor);
        Assert.AreEqual(1, first.SeriesNumber);
        Assert.IsNotNull(first.SeriesId);
        Assert.AreEqual(2, await db.BookIdentifiers.CountAsync(Cancel));

        // Read again: imported links replaced, a manual one kept, counts follow.
        var horror = await db.Tags.SingleAsync(t => t.Name == "Horror", Cancel);
        db.BookTags.Add(new BookTag { TenantId = first.TenantId, BookId = first.Id, TagId = horror.Id, Source = TagSource.Manual });
        await db.SaveChangesAsync(Cancel);
        File.WriteAllText(Path.Combine(library.Root, "1.fb2"), "changed");
        var changedReader = new FakeReader(name => new BookMetadata(ImportTagSourceKind.Fb2, Title: "Renamed", Values: [new FieldValue("author", "Stephen King", 0)]));
        await library.ScanAsync(Cancel, changedReader);

        await using var after = await library.DbAsync(Cancel);
        var renamed = await after.Books.SingleAsync(b => b.Id == first.Id, Cancel);
        Assert.AreEqual("Renamed", renamed.Title);
        Assert.IsNull(renamed.SeriesId);
        var links = await after.BookTags.Where(l => l.BookId == first.Id).Select(l => new { l.TagId, l.Source }).ToListAsync(Cancel);
        Assert.HasCount(2, links);
        Assert.IsTrue(links.Any(l => l.TagId == horror.Id && l.Source == TagSource.Manual), "manual links stay");
        Assert.AreEqual(2, (await after.TagCounts.SingleAsync(c => c.TagId == horror.Id, Cancel)).BookCount, "this book through its manual link, the other through its imported one");
        Assert.AreEqual(1, (await after.TagCounts.SingleAsync(c => c.TagId == favourite.Id, Cancel)).BookCount);
        Assert.AreEqual(2, (await after.TagCounts.SingleAsync(c => c.TagId == king.Id, Cancel)).BookCount);
    }

    [TestMethod]
    public async Task AnFb2InAZipComesInWithItsMetadataThroughTheRealReaders()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        library.Zip("fb2.zip", ("1.fb2", """
            <?xml version="1.0" encoding="utf-8"?>
            <FictionBook><description><title-info><genre>sf_fantasy</genre>
            <author><first-name>Terry</first-name><last-name>Pratchett</last-name></author>
            <book-title>Guards! Guards!</book-title><lang>en</lang><sequence name="Discworld" number="8"/>
            </title-info></description><body/></FictionBook>
            """));

        await library.ScanAsync(Cancel, new Librarion.Core.Import.Metadata.MetadataReaders());

        await using var db = await library.DbAsync(Cancel);
        var book = await db.Books.SingleAsync(Cancel);
        Assert.AreEqual("Guards! Guards!", book.Title);
        Assert.AreEqual("Pratchett, Terry", book.SortAuthor);
        Assert.AreEqual(8, book.SeriesNumber);
        var names = await db.BookTags.Where(l => l.BookId == book.Id).Select(l => l.Tag!.Field!.FieldKey + ":" + l.Tag.Name).ToListAsync(Cancel);
        CollectionAssert.AreEquivalent(new[] { "category:Fantasy", "author:Terry Pratchett", "language:en", "series:Discworld" }, names);
    }

    [TestMethod]
    public async Task EveryFb2OfAZipComesInWithItsMetadata()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        static string Fb2(string title) => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <FictionBook><description><title-info><book-title>{title}</book-title></title-info></description><body/></FictionBook>
            """;
        library.Zip("fb2.zip", ("1.fb2", Fb2("One")), ("2.fb2", Fb2("Two")), ("3.fb2", Fb2("Three")));

        var result = await library.ScanAsync(Cancel, new Librarion.Core.Import.Metadata.MetadataReaders());

        Assert.IsEmpty(result.Problems, string.Join("; ", result.Problems));
        await using var db = await library.DbAsync(Cancel);
        CollectionAssert.AreEquivalent(new[] { "One", "Two", "Three" }, await db.Books.Select(b => b.Title).ToListAsync(Cancel),
            "the archive stays open after each entry is read");
    }

    [TestMethod]
    public async Task TheSameFileInTwoArchivesOrTwiceInOneIsOneBookWithSeveralFiles()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var text = string.Concat(Enumerable.Repeat("the same book ", 500));
        library.Zip("a.zip", ("Author/Book.fb2", text));
        library.Zip("b.zip", ("copy of book.fb2", text), ("unique.fb2", "unique"));
        library.Zip("c.zip", ("twice-1.fb2", "twice"), ("twice-2.fb2", "twice"));

        var result = await library.ScanAsync(Cancel);

        Assert.AreEqual(3, result.Added, "the book, the unique one, the one twice");
        await using (var db = await library.DbAsync(Cancel))
        {
            Assert.AreEqual(3, await db.Books.CountAsync(Cancel));
            Assert.AreEqual(5, await db.BookFiles.CountAsync(Cancel));
            var copies = await db.BookFiles.Where(f => f.Size == text.Length).ToListAsync(Cancel);
            Assert.AreEqual(1, copies.Select(f => f.BookId).Distinct().Count());
            Assert.IsTrue(copies.All(f => f.ContentHash is { Length: 32 }), "both hashed, the stored one kept for the next time");
            Assert.IsNull((await db.BookFiles.SingleAsync(f => f.Path == "unique.fb2", Cancel)).ContentHash, "no other file of its size and CRC: never hashed");
            Assert.AreEqual(1, await db.BookFiles.Where(f => f.Path.StartsWith("twice")).Select(f => f.BookId).Distinct().CountAsync(Cancel));
            Assert.AreEqual(3, (await db.Libraries.SingleAsync(l => l.Id == library.LibraryId, Cancel)).BookCount);
        }

        File.Delete(Path.Combine(library.Root, "a.zip"));
        await library.ScanAsync(Cancel);
        await using (var db = await library.DbAsync(Cancel))
        {
            Assert.AreEqual(3, await db.Books.CountAsync(Cancel), "the book keeps its other copy");
            Assert.AreEqual(0, await db.Books.CountAsync(b => b.MissingSince != null, Cancel), "one copy left: the book is not missing");
            Assert.AreEqual("copy of book.fb2", (await db.BookFiles.SingleAsync(f => f.Size == text.Length && f.MissingSince == null, Cancel)).Path);
        }
    }

    [TestMethod]
    public async Task AFileWithTheSameSizeAndCrcButOtherBytesIsAnotherBook()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        library.Zip("a.zip", ("first.fb2", "AAAA"));
        await library.ScanAsync(Cancel);
        await using var db = await library.DbAsync(Cancel);
        var stored = await db.BookFiles.SingleAsync(Cancel);
        var writer = new CatalogWriter(library.TenantId, library.LibraryId, await ImportTagResolver.CreateAsync(db, library.TenantId, Cancel), TimeProvider.System);
        ScannedFile Lookalike(string name, string bytes) => new(
            new BookFile { ArchiveId = stored.ArchiveId, Path = name, PathKey = Librarion.Core.Catalog.PathKeys.Of(name), Format = "fb2", Size = stored.Size, Crc32 = stored.Crc32 },
            null, () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bytes)));

        await writer.AddAsync(db, [Lookalike("forged.fb2", "BBBB"), Lookalike("real-copy.fb2", "AAAA")], Cancel);

        Assert.AreEqual(1, writer.Added, "the forged one is a book of its own");
        Assert.AreEqual(1, writer.Copies);
        Assert.AreEqual(stored.BookId, (await db.BookFiles.SingleAsync(f => f.Path == "real-copy.fb2", Cancel)).BookId);
        Assert.AreNotEqual(stored.BookId, (await db.BookFiles.SingleAsync(f => f.Path == "forged.fb2", Cancel)).BookId);
    }

    [TestMethod]
    public async Task HostileAndBrokenArchivesAreReportedAndTheRestIsRead()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        library.Zip("names.zip", ("../escape.fb2", "x"), ("ok.fb2", "ok"));
        File.WriteAllText(Path.Combine(library.Root, "broken.zip"), "not a zip at all, but long enough to be looked at");
        library.Write("broken.fb2", "metadata reader throws");
        var reader = new FakeReader(name => name.EndsWith("broken.fb2") ? throw new InvalidDataException("bad XML") : null);

        var result = await library.ScanAsync(Cancel, reader);

        Assert.AreEqual(2, result.Added, "ok.fb2, and broken.fb2 by its file name");
        Assert.IsTrue(result.Problems.Any(p => p.Contains("goes up")));
        Assert.IsTrue(result.Problems.Any(p => p.StartsWith("broken.zip")));
        Assert.IsTrue(result.Problems.Any(p => p.Contains("bad XML")));
        await using var db = await library.DbAsync(Cancel);
        Assert.AreEqual(ArchiveScanState.Incomplete, (await db.Archives.SingleAsync(a => a.Path == "broken.zip", Cancel)).ScanState);
        Assert.AreEqual(2, (await library.ScanAsync(Cancel, reader)).ArchivesSkipped, "both archives skipped while unchanged, the broken one too");
    }

    private sealed class CountingReader : IMetadataReader
    {
        public int Reads { get; private set; }

        public BookMetadata? Read(string format, Func<Stream> open, long size, CancellationToken cancellationToken)
        {
            Reads++;
            using var data = open();
            data.CopyTo(Stream.Null);
            return null;
        }
    }

    private sealed class FakeReader(Func<string, BookMetadata?> answer) : IMetadataReader
    {
        public BookMetadata? Read(string format, Func<Stream> open, long size, CancellationToken cancellationToken)
        {
            using var data = open();
            if (data is FileStream file)
                return answer(Path.GetFileName(file.Name));
            data.CopyTo(Stream.Null);
            return answer(format);
        }
    }
}

/// <summary>A tenant with a library whose one root is a temp folder, and a scanner over it.</summary>
internal sealed class ScannedLibrary : IAsyncDisposable
{
    private readonly TestCatalog catalog;
    private readonly WebApplicationFactory<Program> factory;

    private ScannedLibrary(TestCatalog catalog, WebApplicationFactory<Program> factory, string root, int tenantId, int libraryId)
    {
        this.catalog = catalog;
        this.factory = factory;
        Root = root;
        TenantId = tenantId;
        LibraryId = libraryId;
    }

    public string Root { get; }
    public int TenantId { get; }
    public int LibraryId { get; }

    public static async Task<ScannedLibrary> CreateAsync(CancellationToken cancellationToken, Action<IServiceCollection>? services = null)
    {
        var catalog = new TestCatalog();
        var factory = ApiClients.CreateFactory(catalog, services: services);
        using (ApiClients.Create(factory)) { }
        var tenant = (await factory.Services.GetRequiredService<AccessService>().CreateTenantAsync(new TenantInput("Shelf"), cancellationToken)).Value!;
        var root = Path.Combine(catalog.BasePath, "books");
        Directory.CreateDirectory(root);
        await using var db = await factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(tenant.Id, cancellationToken);
        var library = await db.Libraries.SingleAsync(cancellationToken);
        db.LibraryRoots.Add(new LibraryRoot { TenantId = tenant.Id, LibraryId = library.Id, Path = root, Created = DateTime.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        return new ScannedLibrary(catalog, factory, root, tenant.Id, library.Id);
    }

    public Task<ScanResult> ScanAsync(CancellationToken cancellationToken, IMetadataReader? reader = null) =>
        new LibraryScanner(factory.Services.GetRequiredService<TenantDb>(), reader ?? new NoMetadataReader(), factory.Services.GetRequiredService<Librarion.Core.Merging.DuplicateIndex>(),
                factory.Services.GetRequiredService<Librarion.Core.Settings.SettingsService>(), TimeProvider.System, NullLogger<LibraryScanner>.Instance)
            .ScanAsync(TenantId, LibraryId, null, cancellationToken);

    public T Service<T>() where T : notnull => factory.Services.GetRequiredService<T>();

    public WebApplicationFactory<Program> Factory => factory;

    public Task<LibrarionDbContext> DbAsync(CancellationToken cancellationToken) =>
        factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(TenantId, cancellationToken);

    public string Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public string WriteBytes(string relative, byte[] bytes)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>The configured cache folder of this server (<c>Librarion:Cache:Directory</c>, in the test's folder).</summary>
    public string CacheDirectory => Path.Combine(catalog.BasePath, "cache");

    /// <summary>The server set up, its administrator signed in and working in this library's tenant.</summary>
    public async Task<(HttpClient Client, Librarion.Api.Endpoints.SessionInfo Session)> AdminAsync(CancellationToken cancellationToken)
    {
        var (admin, session) = await ApiClients.SetUpAsync(factory, cancellationToken);
        using var switched = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(admin, "/api/session/tenant",
            new Librarion.Api.Endpoints.TenantChoice(TenantId), ApiClients.Json, cancellationToken);
        switched.EnsureSuccessStatusCode();
        return (admin, session);
    }

    public string Zip(string relative, params (string Name, string? Text)[] entries)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        return TestZips.Zip(Path.GetDirectoryName(path)!, Path.GetFileName(path), CompressionLevel.Optimal, entries);
    }

    /// <summary>An entry read as a reader would: one seek to its stored locator.</summary>
    public string ReadEntry(string archive, BookFile file)
    {
        using var stream = File.OpenRead(Path.Combine(Root, archive));
        var directory = ZipDirectory.Locate(stream);
        var entry = new ArchiveEntry(file.Path, ArchiveEntryKind.File, file.Size, file.PackedSize!.Value, null, false, file.Locator!.Value, file.Method!.Value, (uint)file.Crc32!.Value);
        using var data = directory.OpenEntry(stream, entry);
        return new StreamReader(data).ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        await factory.DisposeAsync();
        catalog.Dispose();
    }
}
