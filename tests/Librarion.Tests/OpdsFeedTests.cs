using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using Librarion.Api.Endpoints;
using Librarion.Api.Opds;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>The OPDS catalog's feeds (agents/docs/features/opds.md): the root, a library's, its books by key, entries, files and covers.</summary>
[TestClass]
public sealed class OpdsFeedTests
{
    private static readonly XNamespace Atom = OpdsFeed.AtomNamespace;
    private static readonly XNamespace Dc = OpdsFeed.DcNamespace;

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string? author = "Ann Writer", string? series = null, int number = 1, string? annotation = null, string lang = "en") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0"><description><title-info>
        <genre>sf_fantasy</genre>
        {(author is null ? "" : $"<author><first-name>{author.Split(' ')[0]}</first-name><last-name>{author.Split(' ')[1]}</last-name></author>")}
        <book-title>{title}</book-title>
        {(annotation is null ? "" : $"<annotation>{annotation}</annotation>")}
        <lang>{lang}</lang>
        {(series is null ? "" : $"<sequence name=\"{series}\" number=\"{number}\"/>")}
        </title-info></description><body><section><p>The text of {title}.</p></section></body></FictionBook>
        """;

    /// <summary>A shelf with an account "reader" in Users and its reader app's client.</summary>
    private async Task<(Shelf Shelf, HttpClient Reader)> StartAsync(params (string Path, string Text)[] books)
    {
        var shelf = await Shelf.CreateAsync(Cancel);
        var groups = await shelf.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        foreach (var (path, text) in books)
            shelf.Write(path, text);
        await shelf.ScanAsync(Cancel);
        return (shelf, OpdsTests.Reader(shelf.Factory, "reader", ApiClients.Password));
    }

    private async Task<XDocument> FeedAsync(HttpClient client, string url, string kind)
    {
        using var response = await client.GetAsync(url, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, url);
        Assert.AreEqual("application/atom+xml", response.Content.Headers.ContentType?.MediaType, url);
        Assert.AreEqual(kind, response.Content.Headers.ContentType?.Parameters.Single(p => p.Name == "kind").Value, url);
        return XDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));
    }

    private static string? Href(XContainer feed, string rel) =>
        feed.Elements(Atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") == rel)?.Attribute("href")?.Value;

    [TestMethod]
    public async Task TheRootOpensTheOnlyLibraryOrListsThem()
    {
        var (shelf, reader) = await StartAsync(("The Hobbit.fb2", Fb2("The Hobbit")));
        await using var _s = shelf;
        using var _r = reader;

        var one = (await FeedAsync(reader, "/opds", "navigation")).Root!;
        Assert.AreEqual($"urn:librarion:library:{shelf.LibraryId}", one.Element(Atom + "id")!.Value);
        Assert.AreEqual("/opds", Href(one, "self"));
        Assert.AreEqual("/opds", Href(one, "start"));
        var entries = one.Elements(Atom + "entry").ToList();
        CollectionAssert.AreEqual(new[] { "Recently added", "All books by title" }, entries.Take(2).Select(e => e.Element(Atom + "title")!.Value).ToArray());
        Assert.AreEqual($"/opds/l/{shelf.LibraryId}/recent", Href(entries[0], OpdsFeed.NewRel));
        Assert.AreEqual($"/opds/l/{shelf.LibraryId}/titles", Href(entries[1], "subsection"));

        using (var made = await shelf.Admin.PostAsJsonAsync("/api/libraries", new LibraryInput("Comics"), ApiClients.Json, Cancel))
            Assert.IsTrue(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync(Cancel));
        var two = (await FeedAsync(reader, "/opds", "navigation")).Root!;
        Assert.AreEqual("urn:librarion:root", two.Element(Atom + "id")!.Value);
        CollectionAssert.AreEquivalent(new[] { "Comics", "Library" }, two.Elements(Atom + "entry").Select(e => e.Element(Atom + "title")!.Value).ToArray());
    }

    /// <summary>A page holds 50 books; the next one follows by key, newest first or A to Z, nothing twice or left out.</summary>
    [TestMethod]
    public async Task BooksComePageByPageByKey()
    {
        var books = Enumerable.Range(0, 55).Select(i => ($"Book {i:00}.fb2", Fb2($"Book {i:00}"))).ToArray();
        var (shelf, reader) = await StartAsync(books);
        await using var _s = shelf;
        using var _r = reader;

        foreach (var order in new[] { "titles", "recent" })
        {
            var first = (await FeedAsync(reader, $"/opds/l/{shelf.LibraryId}/{order}", "acquisition")).Root!;
            var next = Href(first, "next");
            Assert.IsNotNull(next, order);
            StringAssert.StartsWith(next, $"/opds/l/{shelf.LibraryId}/{order}?after=");
            Assert.AreEqual($"/opds/l/{shelf.LibraryId}", Href(first, "up"));
            var second = (await FeedAsync(reader, next, "acquisition")).Root!;
            Assert.IsNull(Href(second, "next"));
            var titles = first.Elements(Atom + "entry").Concat(second.Elements(Atom + "entry")).Select(e => e.Element(Atom + "title")!.Value).ToList();
            Assert.HasCount(50, first.Elements(Atom + "entry").ToList());
            CollectionAssert.AreEquivalent(books.Select(b => b.Item1[..^4]).ToList(), titles, order);
            if (order == "titles")
                CollectionAssert.AreEqual(titles.Order(StringComparer.Ordinal).ToList(), titles);
        }
    }

    /// <summary>An entry: title, authors, language, categories, a summary (series, description as text), JPEG covers, a link per file that downloads it.</summary>
    [TestMethod]
    public async Task AnEntryCarriesTheBookAndItsLinks()
    {
        var (shelf, reader) = await StartAsync(("Fantasy/The Hobbit.fb2",
            Fb2("The Hobbit", "John Tolkien", "Middle-earth", 1, "<p>A <strong>hobbit</strong> &amp; a dragon.</p>", "en")));
        await using var _s = shelf;
        using var _r = reader;

        var entry = (await FeedAsync(reader, $"/opds/l/{shelf.LibraryId}/titles", "acquisition")).Root!.Element(Atom + "entry")!;
        int bookId;
        await using (var db = await shelf.DbAsync(Cancel))
            bookId = (await db.Books.SingleAsync(Cancel)).Id;

        Assert.AreEqual("The Hobbit", entry.Element(Atom + "title")!.Value);
        Assert.AreEqual($"urn:librarion:book:{bookId}", entry.Element(Atom + "id")!.Value);
        Assert.AreEqual("John Tolkien", entry.Element(Atom + "author")!.Element(Atom + "name")!.Value);
        Assert.AreEqual("en", entry.Element(Dc + "language")!.Value);
        Assert.IsNotEmpty(entry.Elements(Atom + "category").ToList());
        Assert.AreEqual("Middle-earth #1\nA hobbit & a dragon.", entry.Element(Atom + "summary")!.Value);
        var links = entry.Elements(Atom + "link").ToList();
        var acquisition = links.Single(l => (string?)l.Attribute("rel") == OpdsFeed.AcquisitionRel && (string?)l.Attribute("title") == "FB2");
        Assert.AreEqual("application/x-fictionbook+xml", (string?)acquisition.Attribute("type"));
        Assert.AreEqual("FB2", (string?)acquisition.Attribute("title"));

        using (var file = await reader.GetAsync(acquisition.Attribute("href")!.Value, Cancel))
        {
            Assert.AreEqual(HttpStatusCode.OK, file.StatusCode);
            Assert.AreEqual((long?)acquisition.Attribute("length"), file.Content.Headers.ContentLength);
            StringAssert.Contains(await file.Content.ReadAsStringAsync(Cancel), "The text of The Hobbit.");
        }
        foreach (var rel in new[] { OpdsFeed.ImageRel, OpdsFeed.ThumbnailRel })
        {
            var link = links.Single(l => (string?)l.Attribute("rel") == rel);
            Assert.AreEqual("image/jpeg", (string?)link.Attribute("type"));
            using var cover = await reader.GetAsync(link.Attribute("href")!.Value, Cancel);
            Assert.AreEqual(HttpStatusCode.OK, cover.StatusCode, rel);
            Assert.AreEqual("image/jpeg", cover.Content.Headers.ContentType?.MediaType);
        }
    }

    [TestMethod]
    public void ASummaryIsPlainTextCutAtAWord()
    {
        var book = new Librarion.Core.Catalog.BookListItem(1, 1, "T", null, [], "Saga", 2.5, [], CoverSource.Generated);
        var description = "<p>" + string.Join(' ', Enumerable.Repeat("word", 400)) + "</p>";

        var summary = OpdsFeed.Summary(book, description);

        StringAssert.StartsWith(summary, "Saga #2.5\nword word");
        StringAssert.EndsWith(summary, "word…");
        Assert.IsLessThanOrEqualTo(OpdsFeed.MaxSummary + 20, summary.Length);
        Assert.AreEqual("", OpdsFeed.Summary(book with { Series = null }, " <br/> "));
    }

    /// <summary>
    /// An FB2 is also offered as <c>fb2.zip</c>, zipped while sent (a loose one and one inside a ZIP): one entry, the
    /// same bytes, the name with <c>.zip</c>; other formats have no such link and their address is not found.
    /// </summary>
    [TestMethod]
    public async Task AnFb2IsAlsoOfferedZipped()
    {
        var (shelf, reader) = await StartAsync(("Loose.fb2", Fb2("Loose")), ("Other.txt", "Just text."));
        await using var _s = shelf;
        using var _r = reader;
        shelf.Zip("Pack.zip", System.IO.Compression.CompressionLevel.Optimal, ("Inside.fb2", Fb2("Inside")));
        await shelf.ScanAsync(Cancel);

        var entries = Entries(await FeedAsync(reader, $"/opds/l/{shelf.LibraryId}/titles", "acquisition"));
        foreach (var (title, name) in new[] { ("Loose", "Loose.fb2"), ("Inside", "Inside.fb2") })
        {
            var acquisitions = entries.Single(e => Title(e) == title).Elements(Atom + "link")
                .Where(l => (string?)l.Attribute("rel") == OpdsFeed.AcquisitionRel).ToList();
            var plain = acquisitions.Single(l => (string?)l.Attribute("title") == "FB2");
            var zipped = acquisitions.Single(l => (string?)l.Attribute("title") == "FB2.ZIP");
            Assert.AreEqual(OpdsFeed.Fb2ZipType, (string?)zipped.Attribute("type"));
            Assert.IsNull(zipped.Attribute("length"));
            Assert.AreEqual(plain.Attribute("href")!.Value + "/zip", zipped.Attribute("href")!.Value);

            byte[] original;
            using (var file = await reader.GetAsync(plain.Attribute("href")!.Value, Cancel))
                original = await file.Content.ReadAsByteArrayAsync(Cancel);
            using var response = await reader.GetAsync(zipped.Attribute("href")!.Value, Cancel);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, title);
            Assert.AreEqual(ZippedDownload.ContentType, response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual(name + ".zip", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(Cancel)));
            var entry = zip.Entries.Single();
            Assert.AreEqual(name, entry.FullName);
            using var unzipped = new MemoryStream();
            await using (var stream = await entry.OpenAsync(Cancel))
                await stream.CopyToAsync(unzipped, Cancel);
            CollectionAssert.AreEqual(original, unzipped.ToArray(), title);
        }

        var text = entries.Single(e => Title(e) == "Other").Elements(Atom + "link").Single(l => (string?)l.Attribute("rel") == OpdsFeed.AcquisitionRel);
        using var notZipped = await reader.GetAsync(text.Attribute("href")!.Value + "/zip", Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, notZipped.StatusCode);
    }

    /// <summary>A reader app holding a merged book's entry: its file and cover addresses still answer (they are the kept book's now).</summary>
    [TestMethod]
    public async Task AMergedBooksOldAddressesStillAnswer()
    {
        var (shelf, reader) = await StartAsync(("Dune.fb2", Fb2("Dune", "Frank Herbert")), ("Dune copy.fb2", Fb2("Dune (copy)", "Frank Herbert")));
        await using var _s = shelf;
        using var _r = reader;
        var before = Entries(await FeedAsync(reader, $"/opds/l/{shelf.LibraryId}/titles", "acquisition"));
        var (kept, merged) = (before.Single(e => Title(e) == "Dune"), before.Single(e => Title(e) == "Dune (copy)"));
        static int IdOf(XElement entry) => int.Parse(entry.Element(Atom + "id")!.Value.Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);
        var hrefs = merged.Elements(Atom + "link")
            .Where(l => (string?)l.Attribute("rel") is OpdsFeed.AcquisitionRel or OpdsFeed.ThumbnailRel)
            .Select(l => l.Attribute("href")!.Value).ToList();
        Assert.HasCount(3, hrefs);

        using (var response = await shelf.Admin.PostAsJsonAsync("/api/books/merge", new Librarion.Core.Merging.BookMergeInput(IdOf(kept), [IdOf(merged)]), Cancel))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));

        var after = Entries(await FeedAsync(reader, $"/opds/l/{shelf.LibraryId}/titles", "acquisition"));
        Assert.AreEqual("Dune", Title(after.Single()), "the merged book is no longer listed");
        foreach (var href in hrefs)
        {
            using var response = await reader.GetAsync(href, Cancel);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, href);
        }
    }

    /// <summary>Without the download right the entries carry no file links and a file's address is refused; others' libraries are not there.</summary>
    [TestMethod]
    public async Task WhatTheAccountMayNotDoIsNotOffered()
    {
        var shelf = await Shelf.CreateAsync(Cancel);
        await using var _s = shelf;
        await shelf.Factory.Services.GetRequiredService<AccessService>().UpdateTenantAsync(shelf.TenantId,
            new TenantInput("Home", DisabledRights: [Rights.Download]), Cancel);
        shelf.Write("The Hobbit.fb2", Fb2("The Hobbit"));
        await shelf.ScanAsync(Cancel);
        var url = (await shelf.UrlOfAsync("The Hobbit.fb2", Cancel)).Replace("/api/", $"/opds/l/{shelf.LibraryId}/");
        using var admin = OpdsTests.Reader(shelf.Factory, ApiClients.AdminName, ApiClients.Password);

        var entry = (await FeedAsync(admin, $"/opds/l/{shelf.LibraryId}/titles", "acquisition")).Root!.Element(Atom + "entry")!;
        using var refused = await admin.GetAsync(url, Cancel);
        using var other = await admin.GetAsync($"/opds/l/{shelf.LibraryId + 1000}/titles", Cancel);

        Assert.IsFalse(entry.Elements(Atom + "link").Any(l => (string?)l.Attribute("rel") == OpdsFeed.AcquisitionRel));
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);
    }

    /// <summary>A server administrator reaches a library of any tenant by its address: its tenant follows the library.</summary>
    [TestMethod]
    public async Task AServerAdministratorReachesAnyTenantsLibrary()
    {
        var shelf = await Shelf.CreateAsync(Cancel);
        await using var _s = shelf;
        using (var made = await shelf.Admin.PostAsJsonAsync("/api/tenants", new TenantInput("Second", LibraryName: "Second books"), ApiClients.Json, Cancel))
            Assert.IsTrue(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync(Cancel));
        int library;
        await using (var db = await shelf.Factory.Services.GetRequiredService<TenantDb>().ForServerAsync(Cancel))
            library = (await db.Libraries.SingleAsync(l => l.Name == "Second books", Cancel)).Id;
        using var admin = OpdsTests.Reader(shelf.Factory, ApiClients.AdminName, ApiClients.Password);
        using var reader = await ReaderOfAsync(shelf);

        var feed = (await FeedAsync(admin, $"/opds/l/{library}", "navigation")).Root!;
        using var refused = await reader.GetAsync($"/opds/l/{library}", Cancel);

        Assert.AreEqual("Second books", feed.Element(Atom + "title")!.Value);
        Assert.AreEqual(HttpStatusCode.NotFound, refused.StatusCode, "an account of another tenant doesn't see it");
    }

    /// <summary>An API key limited to one library sees only that one, from the root on.</summary>
    [TestMethod]
    public async Task AKeyLimitedToALibrarySeesOnlyIt()
    {
        var shelf = await Shelf.CreateAsync(Cancel);
        await using var _s = shelf;
        LibraryInfo comics;
        using (var made = await shelf.Admin.PostAsJsonAsync("/api/libraries", new LibraryInput("Comics"), ApiClients.Json, Cancel))
            comics = (await made.Content.ReadFromJsonAsync<LibraryInfo>(ApiClients.Json, Cancel))!;
        using var web = await ApiClients.SignInAsync(shelf.Factory, ApiClients.AdminName, ApiClients.Password, Cancel);
        using var created = await web.PostAsJsonAsync("/api/account/api-keys", new ApiKeyInput("Comics only", [ApiKeyScopes.Opds], [comics.Id]), ApiClients.Json, Cancel);
        var key = (await created.Content.ReadFromJsonAsync<NewApiKey>(ApiClients.Json, Cancel))!.Key;
        using var app = OpdsTests.Reader(shelf.Factory, ApiClients.AdminName, key);

        var root = (await FeedAsync(app, "/opds", "navigation")).Root!;
        using var other = await app.GetAsync($"/opds/l/{shelf.LibraryId}", Cancel);

        Assert.AreEqual($"urn:librarion:library:{comics.Id}", root.Element(Atom + "id")!.Value, "the only library it may open");
        Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);
    }

    // ---- Fields and tags (chunk 6) ----

    private static string Title(XElement entry) => entry.Element(Atom + "title")!.Value;

    private static List<XElement> Entries(XDocument feed) => feed.Root!.Elements(Atom + "entry").ToList();

    /// <summary>The library's catalog lists its fields that hold values (by their names), then Tags; a field's few values come at once, each with its books.</summary>
    [TestMethod]
    public async Task FieldsWithValuesAreListedAndTheirValuesOpenTheirBooks()
    {
        var (shelf, reader) = await StartAsync(
            ("a.fb2", Fb2("The Hobbit", "John Tolkien", "Middle-earth", 1)),
            ("b.fb2", Fb2("The Silmarillion", "John Tolkien")),
            ("c.fb2", Fb2("Dune", "Frank Herbert", lang: "fr")));
        await using var _s = shelf;
        using var _r = reader;

        var catalog = await FeedAsync(reader, "/opds", "navigation");
        var names = Entries(catalog).Select(Title).ToList();
        foreach (var expected in new[] { "Authors", "Series", "Categories", "Languages", "Tags" })
            CollectionAssert.Contains(names, expected);
        CollectionAssert.DoesNotContain(names, "Translators", "an empty field is left out");

        var authors = await FeedAsync(reader, Href(Entries(catalog).Single(e => Title(e) == "Authors"), "subsection")!, "navigation");
        var tolkien = Entries(authors).Single(e => Title(e) == "John Tolkien");
        Assert.AreEqual("2 books", tolkien.Element(Atom + "content")!.Value);
        Assert.AreEqual($"/opds/l/{shelf.LibraryId}/fields/author", Href(authors.Root!, "self"));
        Assert.AreEqual($"/opds/l/{shelf.LibraryId}", Href(authors.Root!, "up"));
        var link = tolkien.Elements(Atom + "link").Single();
        Assert.AreEqual(OpdsFeed.CatalogType, (string?)link.Attribute("type"));

        var books = await FeedAsync(reader, link.Attribute("href")!.Value, "acquisition");
        CollectionAssert.AreEqual(new[] { "The Hobbit", "The Silmarillion" }, Entries(books).Select(Title).ToArray());
        Assert.AreEqual($"/opds/l/{shelf.LibraryId}/fields/author", Href(books.Root!, "up"));
        using var missing = await reader.GetAsync($"/opds/l/{shelf.LibraryId}/fields/nothing", Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>
    /// Every feed of a library links its OpenSearch description and the template; the description gives the template;
    /// a search pages by key (the next page keeps the words), speaks the search language, and a blank one finds nothing.
    /// </summary>
    [TestMethod]
    public async Task ALibraryIsSearchedPageByPage()
    {
        var books = Enumerable.Range(0, 52).Select(i => ($"Saga {i:00}.fb2", Fb2($"Saga {i:00}", "Ann Writer")))
            .Append(("Dune.fb2", Fb2("Dune", "Frank Herbert"))).Append(("Saga Other.fb2", Fb2("Saga of the Other", "Frank Herbert"))).ToArray();
        var (shelf, reader) = await StartAsync(books);
        await using var _s = shelf;
        using var _r = reader;
        var library = $"/opds/l/{shelf.LibraryId}";
        var template = $"{library}/search?q={{searchTerms}}";

        foreach (var (url, kind) in new[] { ("/opds", "navigation"), (library, "navigation"), ($"{library}/titles", "acquisition"), ($"{library}/fields/author", "navigation") })
        {
            var searches = (await FeedAsync(reader, url, kind)).Root!.Elements(Atom + "link").Where(l => (string?)l.Attribute("rel") == "search").ToList();
            CollectionAssert.AreEqual(new[] { $"{library}/opensearch.xml", template }, searches.Select(l => l.Attribute("href")!.Value).ToArray(), url);
            CollectionAssert.AreEqual(new[] { OpdsFeed.OpenSearchType, OpdsFeed.AcquisitionType }, searches.Select(l => l.Attribute("type")!.Value).ToArray(), url);
        }

        using (var response = await reader.GetAsync($"{library}/opensearch.xml", Cancel))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(OpdsFeed.OpenSearchType, response.Content.Headers.ContentType?.MediaType);
            var description = XDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).Root!;
            XNamespace os = OpdsFeed.OpenSearchNamespace;
            Assert.AreEqual(os + "OpenSearchDescription", description.Name);
            Assert.AreEqual("Library", description.Element(os + "ShortName")!.Value);
            var urls = description.Elements(os + "Url").ToList();
            Assert.IsTrue(urls.All(u => u.Attribute("template")!.Value == template));
            CollectionAssert.Contains(urls.Select(u => u.Attribute("type")!.Value).ToList(), OpdsFeed.AcquisitionType);
        }

        var first = (await FeedAsync(reader, $"{library}/search?q=saga", "acquisition")).Root!;
        Assert.AreEqual($"{library}/search?q=saga", Href(first, "self"));
        Assert.AreEqual(library, Href(first, "up"));
        Assert.AreEqual("Search: saga", first.Element(Atom + "title")!.Value);
        var next = Href(first, "next");
        Assert.IsNotNull(next);
        StringAssert.StartsWith(next, $"{library}/search?q=saga&after=");
        var second = (await FeedAsync(reader, next, "acquisition")).Root!;
        Assert.IsNull(Href(second, "next"));
        var found = Entries(new XDocument(first)).Concat(Entries(new XDocument(second))).Select(Title).ToList();
        Assert.HasCount(53, found);
        CollectionAssert.AllItemsAreUnique(found);
        CollectionAssert.DoesNotContain(found, "Dune");

        var language = Entries(await FeedAsync(reader, $"{library}/search?q=" + Uri.EscapeDataString("author:\"Frank Herbert\" -dune"), "acquisition"))
            .Select(Title).ToArray();
        CollectionAssert.AreEqual(new[] { "Saga of the Other" }, language);

        Assert.IsEmpty(Entries(await FeedAsync(reader, $"{library}/search?q=%20", "acquisition")).ToList());
        using var other = await reader.GetAsync($"/opds/l/{shelf.LibraryId + 1000}/search?q=saga", Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);
        using var otherDescription = await reader.GetAsync($"/opds/l/{shelf.LibraryId + 1000}/opensearch.xml", Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, otherDescription.StatusCode);
    }

    [TestMethod]
    public async Task ASeriesComesByItsNumbers()
    {
        var (shelf, reader) = await StartAsync(
            ("3.fb2", Fb2("Third", series: "Saga", number: 3)),
            ("1.fb2", Fb2("First", series: "Saga", number: 1)),
            ("2.fb2", Fb2("Second", series: "Saga", number: 2)));
        await using var _s = shelf;
        using var _r = reader;

        var series = await FeedAsync(reader, $"/opds/l/{shelf.LibraryId}/fields/series", "navigation");
        var books = await FeedAsync(reader, Href(Entries(series).Single(), "subsection")!, "acquisition");

        CollectionAssert.AreEqual(new[] { "First", "Second", "Third" }, Entries(books).Select(Title).ToArray());
    }

    /// <summary>
    /// More values than a page: the field opens on the first letters they start with (any alphabet), each letter on its
    /// values from there, next pages by key.
    /// </summary>
    [TestMethod]
    public async Task ManyValuesOpenOnTheirFirstLetters()
    {
        var surnames = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".Select(c => $"{c}ster").Concat("БВГДЕЖЗИКЛМНОПРСТУФХЦЧШЭЮЯ".Select(c => $"{c}ов")).ToList();
        var (shelf, reader) = await StartAsync(surnames.Select((name, i) => ($"{i}.fb2", Fb2($"Book {i}", $"Ann {name}"))).ToArray());
        await using var _s = shelf;
        using var _r = reader;

        var letters = await FeedAsync(reader, $"/opds/l/{shelf.LibraryId}/fields/author", "navigation");
        var titles = Entries(letters).Select(Title).ToList();
        var fromB = await FeedAsync(reader, $"/opds/l/{shelf.LibraryId}/fields/author?from=%D0%91", "navigation");

        CollectionAssert.IsSubsetOf(new[] { "A", "Z", "Б", "Я" }, titles);
        Assert.AreEqual("A", titles[0]);
        Assert.AreEqual($"/opds/l/{shelf.LibraryId}/fields/author?from=A", Href(Entries(letters)[0], "subsection"));
        Assert.Contains("Бов", Title(Entries(fromB)[0]));
        Assert.AreEqual($"/opds/l/{shelf.LibraryId}/fields/author", Href(fromB.Root!, "up"));
        Assert.AreEqual("All", titles[^1]);
        var all = await FeedAsync(reader, Href(Entries(letters)[^1], "subsection")!, "navigation");
        Assert.HasCount(OpdsEndpoints.PageSize, Entries(all), "the whole list from its start, a page at a time");
        Assert.IsNotNull(Href(all.Root!, "next"));
    }

    /// <summary>A tag with tags under it is a little tree: all its books (theirs too), then each tag under it; a merged tag's old address follows it.</summary>
    [TestMethod]
    public async Task ATagWithTagsUnderItIsATree()
    {
        var (shelf, reader) = await StartAsync(("a.fb2", Fb2("The Hobbit")), ("b.fb2", Fb2("Dune")));
        await using var _s = shelf;
        using var _r = reader;
        var parent = await CreateTagAsync(shelf, new Librarion.Core.Tagging.TagInput("Speculative"));
        var fairy = await CreateTagAsync(shelf, new Librarion.Core.Tagging.TagInput("Fairy", ParentIds: [parent]));
        int hobbit;
        await using (var db = await shelf.DbAsync(Cancel))
            hobbit = (await db.Books.SingleAsync(b => b.Title == "The Hobbit", Cancel)).Id;
        using (var tagged = await shelf.Admin.PostAsJsonAsync("/api/books/tags",
                   new BookEndpoints.SelectionInput([hobbit], [new Librarion.Core.Catalog.BookTagEdit(fairy)]), ApiClients.Json, Cancel))
            Assert.IsTrue(tagged.IsSuccessStatusCode, await tagged.Content.ReadAsStringAsync(Cancel));

        var tree = await FeedAsync(reader, $"/opds/l/{shelf.LibraryId}/tags/{parent}", "navigation");
        CollectionAssert.AreEqual(new[] { "All books: Speculative", "Fairy" }, Entries(tree).Select(Title).ToArray());
        var all = await FeedAsync(reader, Href(Entries(tree)[0], "subsection")!, "acquisition");
        CollectionAssert.AreEqual(new[] { "The Hobbit" }, Entries(all).Select(Title).ToArray());
        var leaf = await FeedAsync(reader, Href(Entries(tree)[1], "subsection")!, "acquisition");
        CollectionAssert.AreEqual(new[] { "The Hobbit" }, Entries(leaf).Select(Title).ToArray());
        var plain = await FeedAsync(reader, $"/opds/l/{shelf.LibraryId}/tags", "navigation");
        CollectionAssert.IsSubsetOf(new[] { "Fairy", "Speculative" }, Entries(plain).Select(Title).ToArray());

        using (var merged = await shelf.Admin.PostAsJsonAsync($"/api/tags/{fairy}/merge", new TagEndpoints.MergeInput(parent), ApiClients.Json, Cancel))
            Assert.IsTrue(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync(Cancel));
        using var noRedirect = shelf.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        noRedirect.DefaultRequestHeaders.Authorization = reader.DefaultRequestHeaders.Authorization;
        using var moved = await noRedirect.GetAsync($"/opds/l/{shelf.LibraryId}/tags/{fairy}", Cancel);
        Assert.AreEqual(HttpStatusCode.MovedPermanently, moved.StatusCode);
        Assert.AreEqual($"/opds/l/{shelf.LibraryId}/tags/{parent}", moved.Headers.Location?.OriginalString);
    }

    private async Task<int> CreateTagAsync(Shelf shelf, Librarion.Core.Tagging.TagInput input)
    {
        using var made = await shelf.Admin.PostAsJsonAsync("/api/tags", input, ApiClients.Json, Cancel);
        Assert.IsTrue(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync(Cancel));
        return (await made.Content.ReadFromJsonAsync<Librarion.Core.Tagging.TagDefinition>(ApiClients.Json, Cancel))!.Id;
    }

    private async Task<HttpClient> ReaderOfAsync(Shelf shelf)
    {
        var groups = await shelf.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        return OpdsTests.Reader(shelf.Factory, "reader", ApiClients.Password);
    }
}
