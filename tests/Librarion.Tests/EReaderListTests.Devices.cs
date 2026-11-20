using System.Net;
using System.Net.Http.Json;
using Librarion.Api.EReader;
using Librarion.Tests.Infrastructure;

namespace Librarion.Tests;

/// <summary>The e-reader mode's device formats (stage 8 chunk 7, D-8.8, Q5).</summary>
public sealed partial class EReaderListTests
{
    internal const string KindleAgent = "Mozilla/5.0 (X11; U; Linux armv7l like Android; en-us) AppleWebKit/531.2+ (KHTML, like Gecko) Version/5.0 Safari/531.2+ Kindle/3.0+";

    /// <summary>The device each browser's User-Agent names; phones, desktops, Fire tablets and unknown devices name none.</summary>
    [TestMethod]
    [DataRow(KindleAgent, "kindle")]
    [DataRow("Mozilla/5.0 (Linux; U; en-US) AppleWebKit/528.5+ (KHTML, like Gecko, Safari/528.5+) Version/4.0 Kindle/3.0 (screen 600x800; rotate)", "kindle")]
    [DataRow("Mozilla/5.0 (Linux; U; Android 2.0; en-us;) AppleWebKit/538.1 (KHTML, like Gecko) Version/4.0 Mobile Safari/538.1 (Kobo Touch 0373/4.38.21908)", "kobo")]
    [DataRow("Mozilla/5.0 (Linux; Ink) AppleWebKit/537.36 (KHTML, like Gecko) PocketBook/740 Safari/537.36", "pocketbook")]
    [DataRow("Mozilla/5.0 (Linux; U; Android 4.4.2; de-de; tolino vision 4 HD Build/KOT49H) AppleWebKit/534.30 Version/4.0 Mobile Safari/534.30", "tolino")]
    [DataRow("Mozilla/5.0 (Linux; Android 11; BOOX Note Air2 Plus) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/96.0 Safari/537.36", "boox")]
    [DataRow("Mozilla/5.0 (Linux; Android 10; Onyx Nova3) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/88.0 Safari/537.36", "boox")]
    [DataRow("Mozilla/5.0 (Linux; Android 9; KFTRWI) AppleWebKit/537.36 (KHTML, like Gecko) Silk/120.3.1 like Chrome/120.0 Safari/537.36", null)]
    [DataRow("Mozilla/5.0 (Linux; U; Android 2.3.4; en-us; Kindle Fire Build/GINGERBREAD) AppleWebKit/533.1 (KHTML, like Gecko) Version/4.0 Mobile Safari/533.1 Silk/1.0", null)]
    [DataRow("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1", null)]
    [DataRow("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Edg/130.0", null)]
    [DataRow("", null)]
    public void TheDeviceIsGuessedFromTheUserAgent(string userAgent, string? expected) =>
        Assert.AreEqual(expected, EReaderDevices.Guess(userAgent)?.Key, userAgent);

    /// <summary>Its formats first in its order, the others after in their own order; no device, as they are.</summary>
    [TestMethod]
    public void FormatsAreOrderedForTheDevice()
    {
        string[] formats = ["fb2", "epub", "txt", "azw3", "djvu"];
        CollectionAssert.AreEqual(new[] { "azw3", "txt", "fb2", "epub", "djvu" }, EReaderDevices.Order(EReaderDevices.Find("kindle"), formats, f => f));
        CollectionAssert.AreEqual(new[] { "epub", "txt", "fb2", "azw3", "djvu" }, EReaderDevices.Order(EReaderDevices.Find("kobo"), formats, f => f));
        CollectionAssert.AreEqual(new[] { "fb2", "epub", "djvu", "azw3", "txt" }, EReaderDevices.Order(EReaderDevices.Find("pocketbook"), formats, f => f));
        CollectionAssert.AreEqual(formats, EReaderDevices.Order(null, formats, f => f));
        foreach (var device in EReaderDevices.All)
            foreach (var format in device.Formats)
                Assert.IsNotNull(Librarion.Core.Formats.BookFormats.Find(format), $"{device.Key}: {format} is a format the library knows");
    }

    /// <summary>A book with an FB2, a TXT and an AZW3 file (three books merged into one).</summary>
    private async Task<(TestLibrary Library, HttpClient Reader, int Book)> ThreeFormatsAsync()
    {
        var (library, reader) = await StartAsync(("Dune.fb2", OpdsFeedTests.Fb2("Dune", "Frank Herbert")), ("Dune.txt", "Dune, by Frank Herbert."),
            ("Dune.azw3", "not really a Kindle book"));
        var (fb2, _) = await FileOfAsync(library, "Dune.fb2");
        var (txt, _) = await FileOfAsync(library, "Dune.txt");
        var (azw3, _) = await FileOfAsync(library, "Dune.azw3");
        using var response = await library.Admin.PostAsJsonAsync("/api/books/merge", new Librarion.Core.Merging.BookMergeInput(fb2, [txt, azw3]), Cancel);
        Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
        return (library, reader, fb2);
    }

    /// <summary>The formats of a book page's Download buttons, in order, and those after the "Other formats" note.</summary>
    private static (List<string> All, List<string> Others) Downloads(string html)
    {
        static List<string> Of(string part) => DownloadButton().Matches(part).Select(m => m.Groups[1].Value).ToList();
        var others = html.IndexOf("<ul class=\"plain files dim\">", StringComparison.Ordinal);
        return (Of(html), others < 0 ? [] : Of(html[others..]));
    }

    /// <summary>
    /// On a Kindle the book page lists AZW3 and TXT first, then a note and FB2 dimmed (still downloadable); on a Kobo
    /// only TXT first; on a phone (no device) all three in their own order, nothing dimmed. The list's row orders the
    /// formats the same way, the others dimmed.
    /// </summary>
    [TestMethod]
    public async Task TheDevicesFormatsComeFirstAndTheOthersDimmed()
    {
        var (library, reader, book) = await ThreeFormatsAsync();
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;
        var address = $"/e/l/{id}/books/{book}";

        var phone = await PageAsync(reader, address);
        Assert.IsEmpty(Downloads(phone).Others, "no device: nothing dimmed");
        Assert.HasCount(3, Downloads(phone).All);
        StringAssert.Contains(phone, "Formats not ordered for a device.");
        StringAssert.Contains(phone, $"href=\"/e/device?return={Uri.EscapeDataString(address)}\">Change</a>");

        reader.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", KindleAgent);
        var kindle = await PageAsync(reader, address);
        CollectionAssert.AreEqual(new[] { "AZW3", "TXT", "FB2" }, Downloads(kindle).All);
        CollectionAssert.AreEqual(new[] { "FB2" }, Downloads(kindle).Others, "dimmed, still downloadable");
        StringAssert.Contains(kindle, "Formats ordered for Kindle.");
        StringAssert.Contains(kindle, "Other formats: Kindle may not open them.");

        var list = await PageAsync(reader, $"/e/l/{id}/titles");
        StringAssert.Contains(list, "<b>AZW3</b>");
        Assert.IsLessThan(list.IndexOf("<b>TXT</b>", StringComparison.Ordinal), list.IndexOf("<b>AZW3</b>", StringComparison.Ordinal));
        StringAssert.Contains(list, "<span class=\"dim\">FB2</span>");

        reader.DefaultRequestHeaders.UserAgent.Clear();
        reader.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Linux; U; Android 2.0; en-us;) AppleWebKit/538.1 (Kobo Touch 0373/4.38.21908)");
        var kobo = await PageAsync(reader, address);
        Assert.AreEqual("TXT", Downloads(kobo).All[0]);
        CollectionAssert.AreEquivalent(new[] { "FB2", "AZW3" }, Downloads(kobo).Others, "the others in the book's own order");
    }

    /// <summary>
    /// The chooser: Automatic names the guess; a device chosen is kept in a cookie for /e and wins over the guess;
    /// "another device" orders nothing; Automatic again removes it; a device not known is refused, so is a post
    /// without its form token.
    /// </summary>
    [TestMethod]
    public async Task TheDeviceChosenIsKeptInThisBrowser()
    {
        var (library, reader, book) = await ThreeFormatsAsync();
        await using var _l = library;
        using var _r = reader;
        reader.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", KindleAgent);
        var address = $"/e/l/{library.LibraryId}/books/{book}";

        var form = await PageAsync(reader, "/e/device?return=" + Uri.EscapeDataString(address));
        StringAssert.Contains(form, "<form class=\"panel\" method=\"post\" action=\"/e/device\">");
        StringAssert.Contains(form, $"name=\"return\" value=\"{address}\"");
        StringAssert.Matches(form, new System.Text.RegularExpressions.Regex(@"value=""auto"" checked[^>]*>\s*Automatic: Kindle"));
        StringAssert.Contains(form, "AZW3 · AZW · MOBI · PDF · TXT");

        async Task<HttpResponseMessage> ChooseAsync(string device, bool withToken = true)
        {
            var fields = new Dictionary<string, string> { ["device"] = device, ["return"] = address };
            if (withToken)
                fields["__RequestVerificationToken"] = await EReaderTests.FormTokenAsync(reader, "/e/device", Cancel);
            return await reader.PostAsync("/e/device", new FormUrlEncodedContent(fields), Cancel);
        }

        using (var chosen = await ChooseAsync("kobo"))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, chosen.StatusCode);
            Assert.AreEqual(address, chosen.Headers.Location?.OriginalString);
            var cookie = chosen.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(EReaderDevices.CookieName + "=", StringComparison.Ordinal));
            StringAssert.Contains(cookie, "path=/e");
            StringAssert.Contains(cookie, "httponly");
            StringAssert.Contains(cookie, "max-age=31536000");
        }
        CollectionAssert.AreEquivalent(new[] { "FB2", "AZW3" }, Downloads(await PageAsync(reader, address)).Others, "chosen wins over the guess");
        StringAssert.Matches(await PageAsync(reader, "/e/device"), new System.Text.RegularExpressions.Regex(@"value=""kobo"" checked"));

        using (var none = await ChooseAsync(EReaderDevices.NoDevice))
            Assert.AreEqual(HttpStatusCode.Redirect, none.StatusCode);
        var unordered = await PageAsync(reader, address);
        Assert.IsEmpty(Downloads(unordered).Others);
        StringAssert.Contains(unordered, "Formats not ordered for a device.");

        using (var automatic = await ChooseAsync(EReaderDevices.Automatic))
            Assert.AreEqual(HttpStatusCode.Redirect, automatic.StatusCode);
        CollectionAssert.AreEqual(new[] { "AZW3", "TXT", "FB2" }, Downloads(await PageAsync(reader, address)).All, "the guess again");

        using (var unknown = await ChooseAsync("nook"))
            Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
        using (var noToken = await ChooseAsync("kobo", withToken: false))
            Assert.AreEqual(HttpStatusCode.BadRequest, noToken.StatusCode);
        CollectionAssert.AreEqual(new[] { "AZW3", "TXT", "FB2" }, Downloads(await PageAsync(reader, address)).All, "nothing kept");

        var fields = new Dictionary<string, string>
        {
            ["device"] = "kobo", ["return"] = "https://elsewhere.example/",
            ["__RequestVerificationToken"] = await EReaderTests.FormTokenAsync(reader, "/e/device", Cancel),
        };
        using var outside = await reader.PostAsync("/e/device", new FormUrlEncodedContent(fields), Cancel);
        Assert.AreEqual("/e/", outside.Headers.Location?.OriginalString, "never back to another site");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"class=""button"" href=""[^""]*/files/\d+"">Download ([A-Z0-9]+)</a>")]
    private static partial System.Text.RegularExpressions.Regex DownloadButton();
}
