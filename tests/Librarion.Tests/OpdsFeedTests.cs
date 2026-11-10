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
        CollectionAssert.AreEqual(new[] { "Recently added", "All books by title" }, entries.Select(e => e.Element(Atom + "title")!.Value).ToArray());
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
        var acquisition = links.Single(l => (string?)l.Attribute("rel") == OpdsFeed.AcquisitionRel);
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

    private async Task<HttpClient> ReaderOfAsync(Shelf shelf)
    {
        var groups = await shelf.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        return OpdsTests.Reader(shelf.Factory, "reader", ApiClients.Password);
    }
}
