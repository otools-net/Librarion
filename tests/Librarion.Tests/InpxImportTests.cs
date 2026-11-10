using System.IO.Compression;
using System.Text;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Imports;
using Librarion.Core.Imports.Inpx;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Tests;

/// <summary>
/// INPX catalogs imported (agents/docs/features/imports.md#inpx): the default layout and one from
/// <c>structure.info</c> with a <c>FOLDER</c> field, deleted and broken records, an older windows-1251 file, and the
/// zips written as done so a later scan skips them.
/// </summary>
[TestClass]
public sealed class InpxImportTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Line(params string[] fields) => string.Join('\x04', fields) + "\x04";

    [TestMethod]
    public async Task ACatalogInTheDefaultLayoutComesInWithItsZips()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        library.Zip("lib/fb2-000001-000003.zip", ("1.fb2", "one"), ("2.fb2", "two"), ("3.fb2", "deleted"));
        library.Zip("lib/fb2-000004-000004.zip", ("4.fb2", "four"));
        // AUTHOR;GENRE;TITLE;SERIES;SERNO;FILE;SIZE;LIBID;DEL;EXT;DATE;LANG;LIBRATE;KEYWORDS
        var inpx = Inpx(Path.Combine(library.Root, "lib", "librusec.inpx"), "Lib.rus.ec 2024", null, Encoding.UTF8,
            ("fb2-000001-000003.inp",
            [
                Line("Толстой,Лев,Николаевич:Tolstoy,Leo,:", "sf_fantasy:det_classic:", "Война и мир", "Epics", "2", "1", "3", "101", "0", "fb2", "2009-01-01", "ru", "4",
                    "war, peace, war"),
                Line("Pushkin,Alexander,:", "poetry:", "Poems", "", "", "2", "3", "102", "", "fb2", "2009-01-01", "rus", "", ""),
                Line("Gone,Author,:", "prose:", "Deleted", "", "", "3", "3", "103", "1", "fb2", "2009-01-01", "ru", "", ""),
                Line("Broken,Line,:", "prose:", "No file"),
                "",
            ]),
            ("fb2-000004-000004.inp", [Line("King,Stephen,:", "thriller:", "It", "", "", "4", "4", "104", "0", "fb2", "2010-01-01", "en", "5", "")]),
            ("extra.inp", [Line("Online,Only,:", "prose:", "Not local", "", "", "9", "1", "999", "0", "fb2", "2010-01-01", "en", "", "")]));

        var result = await ImportAsync(library, inpx);
        Assert.AreEqual(3, result.Added, string.Join("; ", result.Problems));
        Assert.AreEqual(2, result.Skipped, "the deleted record and the broken line");
        Assert.IsTrue(result.Problems.Any(p => p.Contains("fb2-000001-000003.inp:4")), string.Join("; ", result.Problems));

        await using (var db = await library.DbAsync(Cancel))
        {
            var war = await db.Books.Include(b => b.Identifiers).Include(b => b.Files).SingleAsync(b => b.Title == "Война и мир", Cancel);
            Assert.AreEqual(MetadataSource.Inpx, war.MetadataSource);
            Assert.AreEqual((byte)8, war.Rating, "four stars of five");
            Assert.AreEqual("Толстой, Лев Николаевич", war.SortAuthor);
            Assert.AreEqual(2.0, war.SeriesNumber);
            Assert.AreEqual("1.fb2", war.Files.Single().Path);
            CollectionAssert.IsSubsetOf(new[] { ("libid", "101"), ("inpx", "Lib.rus.ec 2024:101") }, war.Identifiers.Select(i => (i.Scheme, i.Value)).ToList());
            var tags = await db.BookTags.Where(l => l.BookId == war.Id).Select(l => new { Field = l.Tag!.Field!.FieldKey, l.Tag.Name, l.Ordinal }).ToListAsync(Cancel);
            CollectionAssert.AreEqual(new[] { "Лев Николаевич Толстой", "Leo Tolstoy" }, tags.Where(t => t.Field == "author").OrderBy(t => t.Ordinal).Select(t => t.Name).ToList());
            Assert.IsTrue(tags.Any(t => t is { Field: "category", Name: "Fantasy" }) && tags.Any(t => t is { Field: "category", Name: "Classic detective" }), "genres by name");
            Assert.IsTrue(tags.Any(t => t is { Field: "series", Name: "Epics" }));
            Assert.IsTrue(tags.Any(t => t is { Field: "language", Name: "ru" }));
            CollectionAssert.AreEquivalent(new[] { "war", "peace" }, tags.Where(t => t.Field == null).Select(t => t.Name).ToList(), "keywords as plain tags, once each");

            Assert.IsFalse(await db.Books.AnyAsync(b => b.Title == "Not local", Cancel), "extra.inp is an online collection's, not read");
            var archives = await db.Archives.OrderBy(a => a.Path).ToListAsync(Cancel);
            Assert.IsTrue(archives.All(a => a.ScanState == ArchiveScanState.Done), "the zips are written as done");
            var zipFolders = await db.Folders.Where(f => f.ArchiveId != null).ToDictionaryAsync(f => f.ArchiveId!.Value, f => f.Id, Cancel);
            foreach (var file in await db.BookFiles.ToListAsync(Cancel))
                Assert.AreEqual(zipFolders[file.ArchiveId!.Value], file.FolderId, "an imported book is in its zip's folder");
            Assert.AreEqual(3, (await db.Folders.SingleAsync(f => f.ParentId == null, Cancel)).TotalFileCount, "imported files are counted");
        }

        var scan = await library.ScanAsync(Cancel);
        Assert.AreEqual((2, 0, 0), (scan.ArchivesSkipped, scan.Added, scan.Missing), "a later scan skips the imported zips; the deleted entry stays out");

        var again = await ImportAsync(library, inpx);
        Assert.AreEqual((0, 0, 3), (again.Added, again.Changed, again.Unchanged), "an unchanged re-import writes nothing");
    }

    [TestMethod]
    public async Task StructureInfoAndAFolderFieldDecideTheLayout()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        library.Zip("collection/archives/part one.zip", ("books/a.epub", "epub"));
        var inpx = Inpx(Path.Combine(library.Root, "collection", "catalog.inpx"), null, "TITLE;FILE;EXT;FOLDER;AUTHOR", Encoding.UTF8,
            ("any name.inp", [Line("Folder book", "books/a", "epub", "archives/part one.zip", "Writer,Some,:")]));

        var result = await ImportAsync(library, inpx);
        Assert.AreEqual(1, result.Added, string.Join("; ", result.Problems));
        await using var db = await library.DbAsync(Cancel);
        var book = await db.Books.Include(b => b.Files).Include(b => b.Identifiers).SingleAsync(Cancel);
        Assert.AreEqual(("Folder book", "books/a.epub"), (book.Title, book.Files.Single().Path));
        Assert.IsTrue(book.Identifiers.Any(i => i.Scheme == "inpx" && i.Value == "catalog:archives/part one.zip/books/a.epub"),
            "without a LIBID or collection.info: the file's name and the entry");
    }

    [TestMethod]
    public async Task AnOlderWindows1251CatalogIsRead()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        library.Zip("old/old.zip", ("7.fb2", "seven"));
        var inpx = Inpx(Path.Combine(library.Root, "old", "old.inpx"), "Старая", null, Encoding.GetEncoding(1251),
            ("old.inp", [Line("Гоголь,Николай,:", "prose:", "Мёртвые души", "", "", "7", "5", "7", "0", "fb2", "2005-01-01", "ru", "", "")]));

        var result = await ImportAsync(library, inpx);
        Assert.AreEqual(1, result.Added, string.Join("; ", result.Problems));
        await using var db = await library.DbAsync(Cancel);
        Assert.AreEqual("Мёртвые души", (await db.Books.SingleAsync(Cancel)).Title);
        Assert.IsTrue(await db.BookIdentifiers.AnyAsync(i => i.Scheme == "inpx" && i.Value == "Старая:7", Cancel));
    }

    [TestMethod]
    public void OnlyAZipOfInpFilesIsAnInpx()
    {
        var folder = Path.Combine(Path.GetTempPath(), "librarion-inpx-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var format = new InpxFormat();
            Assert.IsNull(format.Check(Inpx(Path.Combine(folder, "ok.inpx"), null, null, Encoding.UTF8, ("a.inp", [Line("x")]))));
            StringAssert.Contains(format.Check(TestZips.Zip(folder, "books.inpx", ("book.fb2", "text"))), ".inp");
            var text = Path.Combine(folder, "text.inpx");
            File.WriteAllText(text, "not a zip");
            Assert.IsNotNull(format.Check(text));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>An INPX written by the test: its <c>.inp</c> files in <paramref name="encoding"/>, <c>collection.info</c> and <c>structure.info</c> when given.</summary>
    private static string Inpx(string path, string? collection, string? structure, Encoding encoding, params (string Name, string[] Lines)[] inps)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void Text(string name, string text, Encoding inEncoding)
        {
            using var stream = zip.CreateEntry(name).Open();
            var bytes = inEncoding.GetBytes(text);
            stream.Write(bytes);
        }

        if (collection is not null)
            Text("collection.info", $"{collection}\nlibrusec\n65536\nA collection\n", Encoding.UTF8);
        if (structure is not null)
            Text("structure.info", structure, Encoding.UTF8);
        foreach (var (name, lines) in inps)
            Text(name, string.Join("\r\n", lines), encoding);
        Text("version.info", "20240101", Encoding.UTF8);
        return path;
    }

    private static async Task<ScanResult> ImportAsync(ScannedLibrary library, string inpx)
    {
        var folder = Path.GetDirectoryName(inpx)!;
        var (_, root, _) = await library.Service<LibraryRoots>().RootForAsync(library.TenantId, library.LibraryId, folder, true, CancellationToken.None);
        return await library.Service<CatalogImporter>().ImportAsync(library.TenantId, library.LibraryId, root!.Id, () => new InpxSource(inpx, folder), null, CancellationToken.None);
    }
}
