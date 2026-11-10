using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Covers;
using Librarion.Core.Data;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Librarion.Tests;

/// <summary>
/// A book's cover chosen on its page, on both providers (stage 6 chunk 10, agents/docs/features/book-display.md): an
/// upload checked, made again and served, a generated style over the book's own, back to its own, all kept through a
/// re-read; refusals; one file for one picture; the rights.
/// </summary>
[TestClass]
public sealed class CoverEditTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string body = "") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info><author><first-name>Ann</first-name><last-name>Writer</last-name></author><book-title>{title}</book-title><lang>en</lang></title-info></description><body><p>{title}{body}</p></body></FictionBook>
        """;

    private static byte[] Picture(int width, int height, SKColor color, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 90).ToArray();
    }

    /// <summary>A 24-bit BMP (Skia reads BMP but doesn't write it): a picture in a format uploads don't take.</summary>
    private static byte[] Bmp(int width, int height)
    {
        var row = (width * 3 + 3) / 4 * 4;
        var bytes = new byte[54 + row * height];
        "BM"u8.CopyTo(bytes);
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(width).CopyTo(bytes, 18);
        BitConverter.GetBytes(height).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        return bytes;
    }

    [TestMethod]
    public async Task AnUploadIsServedKeptThroughAReReadAndLetGo()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One"));
        await shelf.ScanAsync(Cancel);
        var id = await BookIdAsync(shelf, "One");

        var book = await UploadAsync(shelf.Admin, id, Picture(1800, 2700, SKColors.Teal, SKEncodedImageFormat.Jpeg), "image/jpeg");
        Assert.AreEqual((CoverSource.Uploaded, true), (book.CoverSource, book.Cover!.Chosen));
        var stored = await CoverFileAsync(shelf, id);
        var uploads = shelf.Factory.Services.GetRequiredService<CoverUploads>();
        using (var kept = SKBitmap.Decode(uploads.FullPath(stored!)))
            Assert.AreEqual(CoverUploads.MaxWidth, kept.Width, "made again at most 1,200 px wide");
        using (var served = SKBitmap.Decode(await shelf.Admin.GetByteArrayAsync($"/api/books/{id}/cover/l?v={book.CoverVersion}", Cancel)))
        {
            Assert.AreEqual(600, served.Width);
            var pixel = served.GetPixel(300, 450);
            Assert.IsTrue(pixel.Green > 100 && pixel.Red < 60, $"the uploaded picture is served ({pixel})");
        }

        shelf.Write("one.fb2", Fb2("One", " read again"));
        await shelf.ScanAsync(Cancel);
        Assert.AreEqual((CoverSource.Uploaded, stored), (await SourceAsync(shelf, id), await CoverFileAsync(shelf, id)), "a re-read leaves a chosen cover");

        using var removed = await shelf.Admin.DeleteAsync($"/api/books/{id}/cover", Cancel);
        var own = (await removed.Content.ReadFromJsonAsync<BookDetail>(ApiClients.Json, Cancel))!;
        Assert.AreEqual((CoverSource.Book, false), (own.CoverSource, own.Cover!.Chosen));
        Assert.IsNull(await CoverFileAsync(shelf, id));
        Assert.IsFalse(File.Exists(uploads.FullPath(stored!)), "a picture no book uses is deleted");
    }

    [TestMethod]
    public async Task OnePictureIsOneFile()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One"));
        shelf.Write("two.fb2", Fb2("Two"));
        await shelf.ScanAsync(Cancel);
        var (one, two) = (await BookIdAsync(shelf, "One"), await BookIdAsync(shelf, "Two"));
        var picture = Picture(200, 300, SKColors.Orange);

        await UploadAsync(shelf.Admin, one, picture, "image/png");
        await UploadAsync(shelf.Admin, two, picture, "image/png");
        Assert.AreEqual(await CoverFileAsync(shelf, one), await CoverFileAsync(shelf, two));
        using var removed = await shelf.Admin.DeleteAsync($"/api/books/{one}/cover", Cancel);

        Assert.IsTrue(File.Exists(shelf.Factory.Services.GetRequiredService<CoverUploads>().FullPath((await CoverFileAsync(shelf, two))!)), "still the other book's");
    }

    [TestMethod]
    public async Task WrongPicturesAreRefused()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One"));
        await shelf.ScanAsync(Cancel);
        var id = await BookIdAsync(shelf, "One");

        foreach (var (bytes, type, code) in new (byte[], string, string)[]
        {
            ("not a picture"u8.ToArray(), "image/png", "cover.invalid"),
            (Picture(500, 100, SKColors.Red), "image/png", "cover.invalid"),
            (Bmp(20, 30), "image/bmp", "cover.invalid"),
            (new byte[CoverUploads.MaxBytes + 1], "image/png", "cover.tooBig"),
        })
        {
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(type);
            using var response = await shelf.Admin.PutAsync($"/api/books/{id}/cover", content, Cancel);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, code);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(Cancel), code);
        }

        using var style = await shelf.Admin.PutAsJsonAsync($"/api/books/{id}/cover/style", new CoverEndpoints.StyleInput(99, 0), Cancel);
        Assert.AreEqual(HttpStatusCode.BadRequest, style.StatusCode);
        Assert.AreEqual(CoverSource.Book, await SourceAsync(shelf, id));
    }

    [TestMethod]
    public async Task AStyleOverridesTheBooksOwnCoverThroughAReRead()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One"));
        await shelf.ScanAsync(Cancel);
        var id = await BookIdAsync(shelf, "One");
        var styles = await shelf.Admin.GetAsync<CoverEndpoints.CoverStyles>("/api/covers/styles", Cancel);
        CollectionAssert.AreEqual(new[] { "classic", "typographic", "geometric", "band", "minimal" }, styles.Templates.ToList());
        Assert.AreEqual(8, styles.Palettes);

        using var set = await shelf.Admin.PutAsJsonAsync($"/api/books/{id}/cover/style", new CoverEndpoints.StyleInput(2, 3), Cancel);
        var book = (await set.Content.ReadFromJsonAsync<BookDetail>(ApiClients.Json, Cancel))!;
        Assert.AreEqual((CoverSource.Generated, (short)2, (short)3, true), (book.CoverSource, book.Cover!.Template, book.Cover.Variant, book.Cover.Chosen));
        shelf.Write("one.fb2", Fb2("One", " read again"));
        await shelf.ScanAsync(Cancel);
        Assert.AreEqual(CoverSource.Generated, await SourceAsync(shelf, id), "a re-read leaves a chosen style");

        using (var preview = SKBitmap.Decode(await shelf.Admin.GetByteArrayAsync($"/api/books/{id}/cover/preview/4/7", Cancel)))
            Assert.AreEqual(80, preview.Width);
        using var none = await shelf.Admin.GetAsync($"/api/books/{id}/cover/preview/9/0", Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, none.StatusCode);
    }

    [TestMethod]
    public async Task OnlyThoseWhoEditBooksChooseCovers()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One"));
        await shelf.ScanAsync(Cancel);
        var id = await BookIdAsync(shelf, "One");
        var groups = await shelf.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("curator", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Curators).Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(shelf.Factory, "reader", ApiClients.Password, Cancel);
        using var curator = await ApiClients.SignInAsync(shelf.Factory, "curator", ApiClients.Password, Cancel);

        using var refused = await reader.PutAsJsonAsync($"/api/books/{id}/cover/style", new CoverEndpoints.StyleInput(1, 1), Cancel);
        using var allowed = await curator.PutAsJsonAsync($"/api/books/{id}/cover/style", new CoverEndpoints.StyleInput(1, 1), Cancel);
        using var elsewhere = await curator.PutAsJsonAsync($"/api/books/{id + 100}/cover/style", new CoverEndpoints.StyleInput(1, 1), Cancel);

        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, elsewhere.StatusCode);
    }

    // ---- Helpers ----

    private async Task<BookDetail> UploadAsync(HttpClient client, int id, byte[] picture, string type)
    {
        using var content = new ByteArrayContent(picture);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        using var response = await client.PutAsync($"/api/books/{id}/cover", content, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
        return (await response.Content.ReadFromJsonAsync<BookDetail>(ApiClients.Json, Cancel))!;
    }

    private async Task<int> BookIdAsync(Shelf shelf, string title)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Books.Where(b => b.Title == title).Select(b => b.Id).SingleAsync(Cancel);
    }

    private async Task<string?> CoverFileAsync(Shelf shelf, int id)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Books.Where(b => b.Id == id).Select(b => b.CoverFile).SingleAsync(Cancel);
    }

    private async Task<CoverSource> SourceAsync(Shelf shelf, int id)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Books.Where(b => b.Id == id).Select(b => b.CoverSource).SingleAsync(Cancel);
    }
}
