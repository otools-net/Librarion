using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>
/// Downloading a book's file (<c>GET api/books/{id}/files/{fileId}</c>, agents/docs/features/archives.md): a loose
/// file or a ZIP entry streamed in place, with its length, ETag and Range where the bytes can be sought; a file gone
/// or changed since the scan answered as such; only for those who may see the book and download in its library.
/// </summary>
[TestClass]
public sealed class BookDownloadTests
{
    private static readonly string Long = string.Concat(Enumerable.Range(0, 2000).Select(i => $"<p>Line {i} of the book.</p>\n"));

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    [TestMethod]
    public async Task ALooseFileComesWholeOrByRangeWithItsNameAndTag()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("Fantasy/The Hobbit.fb2", Long);
        await shelf.ScanAsync(Cancel);
        var url = await shelf.UrlOfAsync("Fantasy/The Hobbit.fb2", Cancel);

        using var whole = await shelf.Admin.GetAsync(url, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, whole.StatusCode);
        Assert.AreEqual(Long, await whole.Content.ReadAsStringAsync(Cancel));
        Assert.AreEqual(Long.Length, whole.Content.Headers.ContentLength);
        Assert.AreEqual("application/x-fictionbook+xml", whole.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("The Hobbit.fb2", whole.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.IsNotNull(whole.Headers.ETag);
        Assert.AreEqual("bytes", whole.Headers.AcceptRanges.SingleOrDefault());

        using var part = await shelf.GetAsync(url, request => request.Headers.Range = new RangeHeaderValue(3, 12), Cancel);
        Assert.AreEqual(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.AreEqual(Long[3..13], await part.Content.ReadAsStringAsync(Cancel));

        using var cached = await shelf.GetAsync(url, request => request.Headers.IfNoneMatch.Add(whole.Headers.ETag!), Cancel);
        Assert.AreEqual(HttpStatusCode.NotModified, cached.StatusCode);
    }

    [TestMethod]
    public async Task AZipEntryIsStreamedFromTheArchiveWithItsLengthAndCrcAsTag()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Zip("lib/fb2-000.zip", CompressionLevel.Optimal, ("first.fb2", "first"), ("sub/second.epub", Long));
        await shelf.ScanAsync(Cancel);
        var url = await shelf.UrlOfAsync("sub/second.epub", Cancel);

        using var whole = await shelf.Admin.GetAsync(url, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, whole.StatusCode);
        Assert.AreEqual(Long, await whole.Content.ReadAsStringAsync(Cancel));
        Assert.AreEqual(Long.Length, whole.Content.Headers.ContentLength, "the entry's size, though the stream is inflated as it goes");
        Assert.AreEqual("application/epub+zip", whole.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("second.epub", whole.Content.Headers.ContentDisposition?.FileNameStar);
        var crc = System.IO.Hashing.Crc32.HashToUInt32(System.Text.Encoding.UTF8.GetBytes(Long));
        Assert.AreEqual($"\"{crc:x8}-{Long.Length:x}\"", whole.Headers.ETag?.Tag);

        // A compressed entry can't be sought: a range is answered with the whole file.
        using var part = await shelf.GetAsync(url, request => request.Headers.Range = new RangeHeaderValue(3, 12), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, part.StatusCode);
        Assert.AreEqual(Long, await part.Content.ReadAsStringAsync(Cancel));
    }

    [TestMethod]
    public async Task AStoredEntryIsServedByRange()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Zip("stored.zip", CompressionLevel.NoCompression, ("a.pdf", "%PDF-1.4 first"), ("b.pdf", Long));
        await shelf.ScanAsync(Cancel);
        var url = await shelf.UrlOfAsync("b.pdf", Cancel);

        using var part = await shelf.GetAsync(url, request => request.Headers.Range = new RangeHeaderValue(100, 199), Cancel);
        Assert.AreEqual(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.AreEqual(Long[100..200], await part.Content.ReadAsStringAsync(Cancel));
        Assert.AreEqual(Long.Length, part.Content.Headers.ContentRange?.Length);
        using var whole = await shelf.Admin.GetAsync(url, Cancel);
        Assert.AreEqual(Long, await whole.Content.ReadAsStringAsync(Cancel));
    }

    [TestMethod]
    public async Task AFileGoneOrChangedSinceTheScanIsSaidSo()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        var loose = shelf.Write("gone.fb2", "soon gone");
        shelf.Write("edited.fb2", "short");
        shelf.Zip("rebuilt.zip", CompressionLevel.Optimal, ("one.fb2", "one"), ("two.fb2", "two"));
        await shelf.ScanAsync(Cancel);
        var gone = await shelf.UrlOfAsync("gone.fb2", Cancel);
        var edited = await shelf.UrlOfAsync("edited.fb2", Cancel);
        var entry = await shelf.UrlOfAsync("two.fb2", Cancel);

        File.Delete(loose);
        shelf.Write("edited.fb2", "much longer than before");
        shelf.Zip("rebuilt.zip", CompressionLevel.Optimal, ("zero.fb2", Long), ("one.fb2", "one"), ("two.fb2", "two"));

        await AssertProblemAsync(shelf.Admin, gone, HttpStatusCode.Gone, BookEndpoints.FileGoneType);
        await AssertProblemAsync(shelf.Admin, edited, HttpStatusCode.Conflict, BookEndpoints.FileChangedType);
        await AssertProblemAsync(shelf.Admin, entry, HttpStatusCode.Conflict, BookEndpoints.FileChangedType);

        await shelf.ScanAsync(Cancel);
        using var again = await shelf.Admin.GetAsync(await shelf.UrlOfAsync("two.fb2", Cancel), Cancel);
        Assert.AreEqual("two", await again.Content.ReadAsStringAsync(Cancel), "the next scan follows the entry where it moved");
    }

    [TestMethod]
    public async Task APathLeavingItsRootIsNeverOpened()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("book.fb2", "inside");
        await File.WriteAllTextAsync(Path.Combine(shelf.Root, "..", "secret.fb2"), "inside", Cancel);
        await shelf.ScanAsync(Cancel);
        var url = await shelf.UrlOfAsync("book.fb2", Cancel);
        await using (var db = await shelf.DbAsync(Cancel))
            await db.BookFiles.ExecuteUpdateAsync(s => s.SetProperty(f => f.Path, "../secret.fb2"), Cancel);

        await AssertProblemAsync(shelf.Admin, url, HttpStatusCode.Gone, BookEndpoints.FileGoneType);
    }

    [TestMethod]
    public async Task OnlyThoseWhoSeeTheBookAndMayDownloadInItsLibraryGetIt()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("a.fb2", "a");
        shelf.Write("b.fb2", "b");
        await shelf.ScanAsync(Cancel);
        var url = await shelf.UrlOfAsync("a.fb2", Cancel);
        var other = await shelf.UrlOfAsync("b.fb2", Cancel);

        var browse = await Created<GroupInfo>(await shelf.Admin.PostAsJsonAsync("/api/groups",
            new GroupInput("Browsers", AllLibraries: true, Rights: [Rights.Browse]), ApiClients.Json, Cancel));
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("browser", Password: ApiClients.Password, GroupIds: [browse.Id]), Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("nobody", Password: ApiClients.Password), Cancel);
        using var browser = await ApiClients.SignInAsync(shelf.Factory, "browser", ApiClients.Password, Cancel);
        using var nobody = await ApiClients.SignInAsync(shelf.Factory, "nobody", ApiClients.Password, Cancel);
        using var visitor = ApiClients.Create(shelf.Factory);

        using (var response = await browser.GetAsync(url, Cancel))
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, "sees the book, may not download");
        using (var response = await nobody.GetAsync(url, Cancel))
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, "no library right at all");
        using (var response = await visitor.GetAsync(url, Cancel))
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        // Another book's file, or one that doesn't exist: not found.
        var (bookA, _) = Parts(url);
        var (_, fileB) = Parts(other);
        using (var response = await shelf.Admin.GetAsync($"/api/books/{bookA}/files/{fileB}", Cancel))
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        using (var response = await shelf.Admin.GetAsync($"/api/books/{bookA}/files/999999", Cancel))
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static (string Book, string File) Parts(string url)
    {
        var parts = url.Split('/');
        return (parts[3], parts[5]);
    }

    private async Task AssertProblemAsync(HttpClient client, string url, HttpStatusCode status, string type)
    {
        using var response = await client.GetAsync(url, Cancel);
        Assert.AreEqual(status, response.StatusCode, url);
        Assert.AreEqual(type, (await response.Content.ReadFromJsonAsync<ProblemDetails>(ApiClients.Json, Cancel))?.Type);
    }

    private async Task<T> Created<T>(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
            return (await response.Content.ReadFromJsonAsync<T>(ApiClients.Json, Cancel))!;
        }
    }
}
