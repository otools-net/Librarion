using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using Librarion.Api.Endpoints;
using Librarion.Core.Covers;
using Librarion.Core.Covers.Generated;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Librarion.Tests;

/// <summary>
/// Covers (agents/docs/features/book-display.md): the one inside each format, hostile pictures refused, every
/// generated template drawing any text, and the route serving them cached.
/// </summary>
[TestClass]
public sealed class CoverTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    /// <summary>
    /// Every cover is a baseline JPEG (every reader app decodes those; older ones no progressive JPEG and no WebP), and a
    /// picture's transparent parts come out white, not black (JPEG has no transparency).
    /// </summary>
    [TestMethod]
    public void CoversAreBaselineJpegsWithTransparencyOnWhite()
    {
        using var source = new SKBitmap(new SKImageInfo(300, 450, SKColorType.Rgba8888, SKAlphaType.Premul));
        source.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(source))
        using (var paint = new SKPaint { Color = SKColors.Navy })
            canvas.DrawRect(0, 0, 300, 100, paint);
        var png = SKImage.FromBitmap(source).Encode(SKEncodedImageFormat.Png, 100).ToArray();

        var thumbnails = CoverImages.Thumbnails(png)!;
        var uploaded = CoverImages.Remake(png, CoverUploads.MaxWidth, CoverUploads.MaxBytes)!;
        var drawn = GeneratedCovers.Render(0, 0, new CoverText("Title", "Author", null), CoverSize.S);
        // An upload stored as WebP before stage 7 still gives JPEG thumbnails.
        var fromWebp = CoverImages.Thumbnails(SKImage.FromBitmap(source).Encode(SKEncodedImageFormat.Webp, 80).ToArray())!;

        foreach (var jpeg in thumbnails.Values.Append(uploaded).Append(drawn).Concat(fromWebp.Values))
        {
            using var codec = SKCodec.Create(SKData.CreateCopy(jpeg));
            Assert.AreEqual(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
            Assert.IsTrue(HasMarker(jpeg, 0xC0), "baseline (SOF0)");
            Assert.IsFalse(HasMarker(jpeg, 0xC2), "not progressive (SOF2)");
        }
        using var large = SKBitmap.Decode(thumbnails[CoverSize.L]);
        var below = large.GetPixel(150, 300);
        Assert.IsTrue(below.Red > 240 && below.Green > 240 && below.Blue > 240, $"transparent became white: {below}");
        var above = large.GetPixel(150, 40);
        Assert.IsTrue(above.Blue > 90 && above.Red < 40, $"the picture kept: {above}");
        Assert.AreEqual(".jpg", Path.GetExtension(new CoverCache("x").PathOf("0123456789abcdef", CoverSize.M)));
        Assert.AreEqual("\"0123456789abcdef-M.jpg\"", CoverCache.ETag("0123456789abcdef", CoverSize.M));
    }

    /// <summary>A JPEG marker (0xFF, <paramref name="code"/>): in the coded data a 0xFF is followed by 0 or a restart, never these.</summary>
    private static bool HasMarker(byte[] jpeg, byte code)
    {
        for (var i = 0; i + 1 < jpeg.Length; i++)
            if (jpeg[i] == 0xFF && jpeg[i + 1] == code)
                return true;
        return false;
    }

    [TestMethod]
    public void AnFb2sCoverIsItsCoverpageBinary()
    {
        var png = Picture(300, 450, SKColors.Red);
        var fb2 = Fb2("Title", png);

        var cover = CoverExtractor.Extract("fb2", () => new MemoryStream(fb2), fb2.Length);

        CollectionAssert.AreEqual(png, cover);
        var none = Encoding.UTF8.GetBytes("<FictionBook><description><title-info><book-title>T</book-title></title-info></description><body><p>x</p></body></FictionBook>");
        Assert.IsNull(CoverExtractor.Extract("fb2", () => new MemoryStream(none), none.Length));
    }

    [TestMethod]
    [DataRow("epub3")]
    [DataRow("epub2")]
    public void AnEpubsCoverComesFromItsManifest(string kind)
    {
        var png = Picture(200, 300, SKColors.Blue);
        var item = kind == "epub3"
            ? """<item id="c" href="../images/front%20cover.png" media-type="image/png" properties="cover-image"/>"""
            : """<item id="cover-img" href="../images/front%20cover.png" media-type="image/png"/>""";
        var meta = kind == "epub2" ? """<meta name="cover" content="cover-img"/>""" : "";
        var epub = Zip(
            ("META-INF/container.xml", Encoding.UTF8.GetBytes("""<container><rootfiles><rootfile full-path="OPS/text/content.opf"/></rootfiles></container>""")),
            ("OPS/text/content.opf", Encoding.UTF8.GetBytes($"""<package><metadata>{meta}</metadata><manifest><item id="other" href="a.png" media-type="image/png"/>{item}</manifest></package>""")),
            ("OPS/text/a.png", Picture(10, 10, SKColors.Green)),
            ("OPS/images/front cover.png", png));

        CollectionAssert.AreEqual(png, CoverExtractor.Extract("epub", () => new MemoryStream(epub), epub.Length));
    }

    [TestMethod]
    public void AComicsCoverIsItsFirstPageInNaturalOrder()
    {
        var first = Picture(100, 150, SKColors.Yellow);
        var cbz = Zip(("page10.jpg", Picture(100, 150, SKColors.Black)), ("ComicInfo.xml", "<x/>"u8.ToArray()), ("page2.jpg", first));

        CollectionAssert.AreEqual(first, CoverExtractor.Extract("cbz", () => new MemoryStream(cbz), cbz.Length));
    }

    [TestMethod]
    public void AMobisCoverIsItsExth201Record()
    {
        var png = Picture(120, 180, SKColors.Purple);
        var mobi = Mobi(png);

        CollectionAssert.AreEqual(png, CoverExtractor.Extract("azw3", () => new MemoryStream(mobi), mobi.Length));
        // Read forward too, as from a compressed zip entry.
        CollectionAssert.AreEqual(png, CoverExtractor.Extract("mobi", () => new ForwardOnly(mobi), mobi.Length));
    }

    [TestMethod]
    public void ACoverIsMadeAtEverySizeAndHostilePicturesAreRefused()
    {
        var thumbnails = CoverImages.Thumbnails(Picture(1200, 1800, SKColors.Teal))!;
        foreach (var size in CoverImages.Sizes)
        {
            using var bitmap = SKBitmap.Decode(thumbnails[size]);
            Assert.AreEqual(CoverImages.Width(size), bitmap.Width);
            Assert.AreEqual(CoverImages.Width(size) * 3 / 2, bitmap.Height);
        }

        using (var small = SKBitmap.Decode(CoverImages.Thumbnails(Picture(50, 75, SKColors.Teal))![CoverSize.L]))
            Assert.AreEqual(50, small.Width, "never enlarged");
        Assert.IsNull(CoverImages.Thumbnails(PngHeader(30_000, 30_000)), "a bomb's header: refused before decoding");
        Assert.IsNull(CoverImages.Thumbnails(PngHeader(100_000, 10)));
        Assert.IsNull(CoverImages.Thumbnails(Picture(2000, 100, SKColors.Teal)), "a banner, not a cover");
        Assert.IsNull(CoverImages.Thumbnails("not a picture at all"u8.ToArray()));
        Assert.IsNull(CoverImages.Thumbnails([]));
    }

    public static IEnumerable<object[]> Texts =>
    [
        ["Short", "Ann Author", null!],
        [string.Join(' ', Enumerable.Repeat("A very long title that goes on and on", 12)), "First Author, Second Author, Third Author", "A Series With A Long Name #12.5"],
        ["Supercalifragilisticexpialidociousandmoreandmoreandmore", null!, null!],
        ["Мастер и Маргарита", "Михаил Булгаков", "Классика #3"],
        ["Ἰλιάς", "Ὅμηρος", null!],
        ["ألف ليلة وليلة", "مجهول", null!],
        ["מאה שנים של בדידות", "גבריאל גרסיה מרקס", null!],
        ["三体", "刘慈欣", null!],
        ["ノルウェイの森", "村上春樹", "長編小説 #5"],
        ["채식주의자", "한강", null!],
        ["गोदान", "प्रेमचंद", null!],
        ["গীতাঞ্জলি", "রবীন্দ্রনাথ ঠাকুর", null!],
        ["பொன்னியின் செல்வன்", "கல்கி", null!],
        ["สี่แผ่นดิน", "คึกฤทธิ์ ปราโมช", null!],
        [string.Concat(Enumerable.Repeat("红楼梦", 40)), "曹雪芹", null!],
        ["", null!, null!],
    ];

    [TestMethod]
    [DataRow("三体")]
    [DataRow("ノルウェイの森")]
    [DataRow("채식주의자")]
    [DataRow("गोदान")]
    [DataRow("গীতাঞ্জলি")]
    [DataRow("பொன்னியின் செல்வன்")]
    [DataRow("สี่แผ่นดิน")]
    [DataRow("ألف ليلة وليلة")]
    [DataRow("מאה שנים של בדידות")]
    public void ATitleInAnAsianOrRightToLeftScriptIsDrawn(string title)
    {
        for (var template = 0; template < GeneratedCovers.Templates.Count; template++)
        {
            using var with = SKBitmap.Decode(GeneratedCovers.Render(template, 0, new CoverText(title, null, null), CoverSize.M));
            using var without = SKBitmap.Decode(GeneratedCovers.Render(template, 0, new CoverText("", null, null), CoverSize.M));
            var changed = 0;
            for (var y = 0; y < with.Height; y += 2)
                for (var x = 0; x < with.Width; x += 2)
                    if (with.GetPixel(x, y) != without.GetPixel(x, y))
                        changed++;
            Assert.IsGreaterThan(100, changed, $"{GeneratedCovers.Templates[template].Name}: the title shows");
        }
    }

    [TestMethod]
    [DynamicData(nameof(Texts))]
    public void EveryTemplateDrawsAnyTextWithEveryPalette(string title, string? authors, string? series)
    {
        var samples = Environment.GetEnvironmentVariable("LIBRARION_COVER_SAMPLES");
        for (var template = 0; template < GeneratedCovers.Templates.Count; template++)
            for (var variant = 0; variant < GeneratedCovers.Palettes.Count; variant++)
            {
                var jpeg = GeneratedCovers.Render(template, variant, new CoverText(title, authors, series), CoverSize.M);
                using var bitmap = SKBitmap.Decode(jpeg);
                Assert.AreEqual(240, bitmap.Width);
                Assert.AreEqual(360, bitmap.Height);
                if (samples is not null && variant == template % GeneratedCovers.Palettes.Count)
                {
                    Directory.CreateDirectory(samples);
                    var name = $"{GeneratedCovers.Templates[template].Name}-{XxName(title)}.png";
                    File.WriteAllBytes(Path.Combine(samples, name), SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100).ToArray());
                }
            }
    }

    [TestMethod]
    public void TheAssignmentIsStableAndSpreadsEvenly()
    {
        Assert.AreEqual(GeneratedCovers.Assign(12345), GeneratedCovers.Assign(12345));
        var counts = new int[GeneratedCovers.Templates.Count, GeneratedCovers.Palettes.Count];
        for (long i = 0; i < 40_000; i++)
        {
            var (template, variant) = GeneratedCovers.Assign(i);
            counts[template, variant]++;
        }

        var expected = 40_000.0 / counts.Length;
        foreach (var count in counts)
            Assert.IsTrue(Math.Abs(count - expected) < expected * 0.2, $"{count} against {expected}");
    }

    [TestMethod]
    public async Task TheRouteServesABooksOwnCoverOrItsGeneratedOneCached()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        library.WriteBytes("with-cover.fb2", Fb2("With a cover", Picture(600, 900, SKColors.Orange)));
        library.WriteBytes("broken-cover.fb2", Fb2("Broken cover", "not a picture"u8.ToArray()));
        library.Write("Some Report.pdf", "%PDF-1.4");
        await library.ScanAsync(Cancel, new Librarion.Core.Import.Metadata.MetadataReaders());
        var (admin, _) = await library.AdminAsync(Cancel);
        int withCover, broken, pdf;
        await using (var db = await library.DbAsync(Cancel))
        {
            var books = await db.Books.Include(b => b.Files).ToListAsync(Cancel);
            Assert.IsTrue(books.All(b => b.GeneratedTemplate is not null && b.GeneratedVariant is not null), "every book has its generated design");
            withCover = books.Single(b => b.Title == "With a cover").Id;
            broken = books.Single(b => b.Title == "Broken cover").Id;
            var report = books.Single(b => b.Files[0].Format == "pdf");
            Assert.AreEqual(CoverSource.Generated, report.CoverSource, "a PDF has no cover of its own on the server");
            pdf = report.Id;
        }

        foreach (var (id, color) in new[] { (withCover, (SKColor?)SKColors.Orange), (broken, null), (pdf, null) })
        {
            using var response = await admin.GetAsync($"/api/books/{id}/cover/l", Cancel);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("image/jpeg", response.Content.Headers.ContentType?.MediaType);
            StringAssert.EndsWith(response.Headers.ETag!.Tag, ".jpg\"", "a browser's cached WebP of the same key isn't taken for it");
            using var bitmap = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync(Cancel));
            Assert.AreEqual(600, bitmap.Width);
            if (color is { } expected)
            {
                // JPEG is lossy: the colour comes back close, not exact.
                var pixel = bitmap.GetPixel(300, 450);
                Assert.IsTrue(Math.Abs(pixel.Red - expected.Red) + Math.Abs(pixel.Green - expected.Green) + Math.Abs(pixel.Blue - expected.Blue) < 30,
                    $"the book's own picture: {pixel} against {expected}");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/books/{id}/cover/l");
            request.Headers.IfNoneMatch.Add(response.Headers.ETag!);
            using var again = await admin.SendAsync(request, Cancel);
            Assert.AreEqual(HttpStatusCode.NotModified, again.StatusCode);
        }

        await using (var db = await library.DbAsync(Cancel))
        {
            Assert.AreEqual(CoverState.Present, (await db.Books.SingleAsync(b => b.Id == withCover, Cancel)).CoverState);
            Assert.AreEqual(CoverState.Failed, (await db.Books.SingleAsync(b => b.Id == broken, Cancel)).CoverState, "looked at once, not again");
        }

        var cached = Directory.GetFiles(library.CacheDirectory, "*.jpg", SearchOption.AllDirectories);
        Assert.AreEqual(3 + 1 + 1, cached.Length, "the book's own cover at every size, the generated ones at the size asked");
        Assert.IsTrue(cached.All(f => f.StartsWith(Path.Combine(library.CacheDirectory, "covers"), StringComparison.Ordinal)), "under the configured cache folder");

        using (var response = await admin.GetAsync($"/api/books/{withCover}/cover/xl", Cancel))
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        using (var response = await admin.GetAsync("/api/books/999999/cover/s", Cancel))
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string XxName(string text) => text.Length == 0 ? "empty" : System.IO.Hashing.XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(text)).ToString("x8");

    internal static byte[] Picture(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    internal static byte[] Fb2(string title, byte[] cover) => Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink">
        <description><title-info><book-title>{title}</book-title><coverpage><image l:href="#cover.png"/></coverpage></title-info></description>
        <body><section><p>Text.</p></section></body>
        <body name="notes"><section><p>A note.</p></section></body>
        <binary id="other.png" content-type="image/png">{Convert.ToBase64String(Picture(4, 4, SKColors.White))}</binary>
        <binary id="cover.png" content-type="image/png">{Convert.ToBase64String(cover)}</binary>
        </FictionBook>
        """);

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
                using (var stream = zip.CreateEntry(name).Open())
                    stream.Write(data);
        return memory.ToArray();
    }

    /// <summary>A PNG's signature and header claiming a size, and nothing else.</summary>
    private static byte[] PngHeader(int width, int height)
    {
        var chunk = new byte[17];
        "IHDR"u8.CopyTo(chunk);
        BinaryPrimitives.WriteInt32BigEndian(chunk.AsSpan(4), width);
        BinaryPrimitives.WriteInt32BigEndian(chunk.AsSpan(8), height);
        chunk[12] = 8;
        chunk[13] = 6;
        var png = new List<byte>([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13]);
        png.AddRange(chunk);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, System.IO.Hashing.Crc32.HashToUInt32(chunk));
        png.AddRange(crc);
        png.AddRange(new byte[64]);
        return png.ToArray();
    }

    /// <summary>A PalmDB with record 0 (MOBI header, EXTH 201 = 0, first image = record 1), the image, and an end record.</summary>
    private static byte[] Mobi(byte[] image)
    {
        const int headerLength = 0xE8;
        var exth = new byte[12 + 12];
        "EXTH"u8.CopyTo(exth);
        BinaryPrimitives.WriteUInt32BigEndian(exth.AsSpan(4), (uint)exth.Length);
        BinaryPrimitives.WriteUInt32BigEndian(exth.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(exth.AsSpan(12), 201);
        BinaryPrimitives.WriteUInt32BigEndian(exth.AsSpan(16), 12);
        BinaryPrimitives.WriteUInt32BigEndian(exth.AsSpan(20), 0);
        var record0 = new byte[16 + headerLength + exth.Length];
        var mobi = record0.AsSpan(16);
        "MOBI"u8.CopyTo(mobi);
        BinaryPrimitives.WriteUInt32BigEndian(mobi[4..], headerLength);
        BinaryPrimitives.WriteUInt32BigEndian(mobi[12..], 65001);
        BinaryPrimitives.WriteUInt32BigEndian(mobi[0x6C..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(mobi[0x70..], 0x40);
        exth.CopyTo(mobi[headerLength..]);

        const int records = 3;
        var start = 78 + records * 8 + 2;
        var file = new byte[start + record0.Length + image.Length + 4];
        "BOOKMOBI"u8.CopyTo(file.AsSpan(60));
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(76), records);
        int[] offsets = [start, start + record0.Length, start + record0.Length + image.Length];
        for (var i = 0; i < records; i++)
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(78 + i * 8), (uint)offsets[i]);
        record0.CopyTo(file, offsets[0]);
        image.CopyTo(file, offsets[1]);
        return file;
    }

    /// <summary>A stream that only reads forward, a few bytes at a time (a compressed entry).</summary>
    private sealed class ForwardOnly(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1000));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1000)]);
    }
}
