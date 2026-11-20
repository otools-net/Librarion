using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Librarion.Api.EReader;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>The e-reader mode's book page and downloads (stage 8 chunk 6).</summary>
public sealed partial class EReaderListTests
{
    private async Task<(int Book, int File)> FileOfAsync(TestLibrary library, string path)
    {
        await using var db = await library.DbAsync(Cancel);
        var file = await db.BookFiles.SingleAsync(f => f.Path == path, Cancel);
        return (file.BookId, file.Id);
    }

    /// <summary>
    /// A book's page: the title, the cover with its size, its fields as links to their values' books (a series with
    /// its number), the files with Download (an FB2 also zipped), the description as plain text in paragraphs.
    /// </summary>
    [TestMethod]
    public async Task ABookPageShowsItsFieldsFilesAndDescription()
    {
        var (library, reader) = await StartAsync(("Dune.fb2", OpdsFeedTests.Fb2("Dune", "Frank Herbert", series: "Dune Chronicles", number: 2,
            annotation: "<p>Arrakis, the &lt;desert&gt; planet.</p><p>Spice &amp; sand.</p>")));
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;
        var (book, file) = await FileOfAsync(library, "Dune.fb2");
        int author;
        await using (var db = await library.DbAsync(Cancel))
            author = await db.BookTags.Where(l => l.BookId == book && l.Tag!.Name == "Frank Herbert").Select(l => l.TagId).SingleAsync(Cancel);

        var html = await PageAsync(reader, $"/e/l/{id}/books/{book}");

        StringAssert.Contains(html, "<h1>Dune</h1>");
        StringAssert.Contains(html, $"<p class=\"crumb\"><a href=\"/e/l/{id}/\">Library</a></p>");
        StringAssert.Matches(html, new Regex($@"<img class=""cover-m"" src=""/e/l/{id}/books/{book}/cover/m(\?v=[^""]*)?"" width=""120"" height=""180"" alt"));
        StringAssert.Contains(html, "<dt>Authors</dt>");
        StringAssert.Contains(html, $"<a href=\"/e/l/{id}/tags/{author}\">Frank Herbert</a>");
        StringAssert.Contains(html, "<dt>Series</dt>");
        StringAssert.Contains(html, ">Dune Chronicles, 2</a>");
        Assert.IsLessThan(html.IndexOf("<dt>Series</dt>", StringComparison.Ordinal), html.IndexOf("<dt>Authors</dt>", StringComparison.Ordinal), "the fields in the tenant's order");
        StringAssert.Contains(html, $"<a class=\"button\" href=\"/e/l/{id}/books/{book}/files/{file}\">Download FB2</a>");
        StringAssert.Contains(html, $"<a class=\"button\" href=\"/e/l/{id}/books/{book}/files/{file}/zip\">Download FB2, zipped</a>");
        StringAssert.Contains(html, "<p>Arrakis, the &lt;desert&gt; planet.</p>");
        StringAssert.Contains(html, "<p>Spice &amp; sand.</p>", "a paragraph each, the text encoded");

        foreach (var address in new[] { $"/e/l/{id}/books/{book}/cover/m", $"/e/l/{id}/tags/{author}" })
        {
            using var response = await reader.GetAsync(address, Cancel);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, address);
        }
    }

    /// <summary>A long description is cut at a word with "…"; a book without one has no Description heading.</summary>
    [TestMethod]
    public void ADescriptionIsPlainTextCut()
    {
        var long_ = string.Join(" ", Enumerable.Repeat("word", 1000));
        var cut = EReaderEndpoints.Paragraphs(long_ + "\nnever shown");
        Assert.HasCount(1, cut);
        Assert.IsLessThanOrEqualTo(EReaderEndpoints.MaxDescription + 1, cut[0].Length);
        StringAssert.EndsWith(cut[0], "word…");
        CollectionAssert.AreEqual(new[] { "One", "Two lines" }, EReaderEndpoints.Paragraphs("One\r\nTwo\xA0  lines\n\n").ToArray());
        CollectionAssert.AreEqual(new[] { "<script>x</script> is text" }, EReaderEndpoints.Paragraphs("<script>x</script> is text").ToArray(),
            "plain text, never markup: Razor encodes it");
        Assert.IsEmpty(EReaderEndpoints.Paragraphs("  \n \t "));
    }

    /// <summary>
    /// Downloads through /e: a loose file (with Range) and a ZIP entry, the same bytes as the app's; an FB2 zipped
    /// while sent; the name as UTF-8 and as ASCII (D-8.8).
    /// </summary>
    [TestMethod]
    public async Task FilesDownloadLooseAndFromAZip()
    {
        var text = OpdsFeedTests.Fb2("Война и мир", "Лев Толстой");
        var (library, reader) = await StartAsync(("Café Society.fb2", OpdsFeedTests.Fb2("Café Society")));
        await using var _l = library;
        using var _r = reader;
        library.Zip("Pack.zip", CompressionLevel.Optimal, ("Война и мир.fb2", text));
        await library.ScanAsync(Cancel);
        var id = library.LibraryId;

        var (loose, looseFile) = await FileOfAsync(library, "Café Society.fb2");
        using (var whole = await reader.GetAsync($"/e/l/{id}/books/{loose}/files/{looseFile}", Cancel))
        {
            Assert.AreEqual(HttpStatusCode.OK, whole.StatusCode);
            Assert.AreEqual("application/x-fictionbook+xml", whole.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("Café Society.fb2", whole.Content.Headers.ContentDisposition?.FileNameStar);
            Assert.AreEqual("Cafe Society.fb2", whole.Content.Headers.ContentDisposition?.FileName, "accents folded for old browsers");
        }
        using (var part = await reader.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/e/l/{id}/books/{loose}/files/{looseFile}")
               { Headers = { Range = new RangeHeaderValue(0, 4) } }, Cancel))
            Assert.AreEqual(HttpStatusCode.PartialContent, part.StatusCode);

        var (inside, insideFile) = await FileOfAsync(library, "Война и мир.fb2");
        var page = await PageAsync(reader, $"/e/l/{id}/books/{inside}");
        StringAssert.Contains(page, "<h1>Война и мир</h1>", "written as it is");
        StringAssert.Contains(page, "in Pack.zip");
        using (var entry = await reader.GetAsync($"/e/l/{id}/books/{inside}/files/{insideFile}", Cancel))
        {
            Assert.AreEqual(HttpStatusCode.OK, entry.StatusCode);
            Assert.AreEqual(text, await entry.Content.ReadAsStringAsync(Cancel));
            Assert.AreEqual("Война и мир.fb2", entry.Content.Headers.ContentDisposition?.FileNameStar);
            Assert.AreEqual($"book-{insideFile}.fb2", entry.Content.Headers.ContentDisposition?.FileName, "no Latin letter left: the file's id");
        }
        using (var zipped = await reader.GetAsync($"/e/l/{id}/books/{inside}/files/{insideFile}/zip", Cancel))
        {
            Assert.AreEqual(HttpStatusCode.OK, zipped.StatusCode);
            Assert.AreEqual("Война и мир.fb2.zip", zipped.Content.Headers.ContentDisposition?.FileNameStar);
            Assert.AreEqual($"book-{insideFile}.fb2.zip", zipped.Content.Headers.ContentDisposition?.FileName);
            using var zip = new ZipArchive(new MemoryStream(await zipped.Content.ReadAsByteArrayAsync(Cancel)));
            using var unzipped = new StreamReader(await zip.Entries.Single().OpenAsync(Cancel));
            Assert.AreEqual(text, await unzipped.ReadToEndAsync(Cancel));
        }

        using var notFb2 = await reader.GetAsync($"/e/l/{id}/books/{loose}/files/{looseFile + 1000}", Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, notFb2.StatusCode);
    }

    /// <summary>
    /// A merged book's address goes to the kept book; a hidden book is not found; without the download right the
    /// page says so and offers no Download, and a file's address is refused; a file gone since the scan is a plain page.
    /// </summary>
    [TestMethod]
    public async Task MergedHiddenRefusedAndGoneAreSaidPlainly()
    {
        var (library, reader) = await StartAsync(("Dune.fb2", OpdsFeedTests.Fb2("Dune", "Frank Herbert")),
            ("Dune copy.fb2", OpdsFeedTests.Fb2("Dune (copy)", "Frank Herbert")), ("Gone.fb2", OpdsFeedTests.Fb2("Gone")));
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;
        var (kept, _) = await FileOfAsync(library, "Dune.fb2");
        var (merged, _) = await FileOfAsync(library, "Dune copy.fb2");
        var (gone, goneFile) = await FileOfAsync(library, "Gone.fb2");

        File.Delete(Path.Combine(library.Root, "Gone.fb2"));
        using (var response = await reader.GetAsync($"/e/l/{id}/books/{gone}/files/{goneFile}", Cancel))
        {
            Assert.AreEqual(HttpStatusCode.Gone, response.StatusCode);
            var html = await EReaderTests.PlainPageAsync(response, Cancel);
            StringAssert.Contains(html, "t download this file</h1>");
            StringAssert.Contains(html, "This file is no longer where the last scan found it.");
        }

        using (var response = await library.Admin.PostAsJsonAsync("/api/books/merge", new Librarion.Core.Merging.BookMergeInput(kept, [merged]), Cancel))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
        using (var redirect = await reader.GetAsync($"/e/l/{id}/books/{merged}", Cancel))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, redirect.StatusCode);
            Assert.AreEqual($"/e/l/{id}/books/{kept}", redirect.Headers.Location?.OriginalString);
        }
        foreach (var missing in new[] { $"/e/l/{id}/books/{kept + 1000}", $"/e/l/{id + 1000}/books/{kept}" })
        {
            using var response = await reader.GetAsync(missing, Cancel);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, missing);
        }

        var (_, keptFile) = await FileOfAsync(library, "Dune.fb2");
        await library.Factory.Services.GetRequiredService<AccessService>().UpdateTenantAsync(library.TenantId,
            new TenantInput("Home", DisabledRights: [Rights.Download]), Cancel);
        var page = await PageAsync(reader, $"/e/l/{id}/books/{kept}");
        StringAssert.Contains(page, "You may not download books of this library.");
        Assert.IsFalse(page.Contains("/files/", StringComparison.Ordinal), "no Download");
        StringAssert.Contains(page, "Dune.fb2", "the files are still listed");
        using var refused = await reader.GetAsync($"/e/l/{id}/books/{kept}/files/{keptFile}", Cancel);
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        StringAssert.Contains(await EReaderTests.PlainPageAsync(refused, Cancel), "Not allowed");
    }

    /// <summary>The ASCII name beside the UTF-8 one (D-8.8): accents folded, other letters one <c>_</c>, never only signs.</summary>
    [TestMethod]
    public void DownloadNamesHaveAnAsciiFallback()
    {
        Assert.AreEqual("The Hobbit.fb2", DownloadNames.Ascii("The Hobbit.fb2", 7));
        Assert.AreEqual("Cafe Society.epub", DownloadNames.Ascii("Café Society.epub", 7));
        Assert.AreEqual("Tolstoy.fb2", DownloadNames.Ascii("Tolstoy – Война.fb2", 7), "no _ left at the end");
        Assert.AreEqual("War_Peace.fb2", DownloadNames.Ascii("War «и» Peace.fb2", 7));
        Assert.AreEqual("book-7.fb2.zip", DownloadNames.Ascii("Война и мир.fb2.zip", 7));
        Assert.AreEqual("book-7.fb2", DownloadNames.Ascii("\"«»\".fb2", 7));
        Assert.AreEqual("a_b.pdf", DownloadNames.Ascii("a\"b.pdf", 7), "never a quote in the header");
        var header = DownloadNames.Disposition("Война.fb2", 7);
        StringAssert.StartsWith(header, "attachment; filename=\"book-7.fb2\"; filename*=UTF-8''");
    }
}
