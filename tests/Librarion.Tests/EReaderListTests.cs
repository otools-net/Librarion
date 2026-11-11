using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Librarion.Api.EReader;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>The e-reader mode's home, a library's page and its lists of books (stage 8 chunk 3).</summary>
[TestClass]
public sealed partial class EReaderListTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    /// <summary>A library with <paramref name="books"/> scanned, an account "reader" in Users, and its browser signed in on /e.</summary>
    private async Task<(TestLibrary Library, HttpClient Reader)> StartAsync(params (string Path, string Text)[] books)
    {
        var library = await TestLibrary.CreateAsync(Cancel);
        var groups = await library.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(library.Admin, new NewUser("reader", Password: ApiClients.Password,
            GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        foreach (var (path, text) in books)
            library.Write(path, text);
        await library.ScanAsync(Cancel);
        return (library, await EReaderTests.SignedInBrowserAsync(library.Factory, "reader", Cancel));
    }

    private async Task<string> PageAsync(HttpClient client, string address)
    {
        using var response = await client.GetAsync(address, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, address);
        return await EReaderTests.PlainPageAsync(response, Cancel);
    }

    /// <summary>The ids of the books a page lists, in order (each row links to its book's page).</summary>
    private static List<int> BookIds(string html, int library) =>
        BookLink().Matches(html).Where(m => m.Groups[1].Value == library.ToString()).Select(m => int.Parse(m.Groups[2].Value)).ToList();

    private static string? NextOf(string html) => NextLink().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    /// <summary>
    /// A library's page: the search box, the five books added last with a link to all of them, and what it can be
    /// browsed by (all by title, the fields with values by the app's names, plain tags).
    /// </summary>
    [TestMethod]
    public async Task ALibrarysPageHasSearchRecentAndBrowse()
    {
        var books = Enumerable.Range(0, 7).Select(i => ($"Book {i}.fb2", OpdsFeedTests.Fb2($"Book {i}", series: i == 0 ? "The Saga" : null))).ToArray();
        var (library, reader) = await StartAsync(books);
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;

        var html = await PageAsync(reader, $"/e/l/{id}/");

        StringAssert.Contains(html, "<h1>Library</h1>");
        StringAssert.Contains(html, $"<form class=\"panel search\" method=\"get\" action=\"/e/l/{id}/search\" role=\"search\">");
        StringAssert.Contains(html, "name=\"q\" type=\"search\"");
        Assert.HasCount(5, BookIds(html, id), "the books added last");
        StringAssert.Contains(html, $"href=\"/e/l/{id}/recent\">All recently added</a>");
        StringAssert.Contains(html, $"href=\"/e/l/{id}/titles\">All books by title</a>");
        StringAssert.Contains(html, $"href=\"/e/l/{id}/fields/author\">Authors</a>");
        StringAssert.Contains(html, $"href=\"/e/l/{id}/fields/series\">Series</a>");
        StringAssert.Contains(html, $"href=\"/e/l/{id}/tags\">Tags</a>");
        Assert.AreEqual(Token().Replace(html, ""), Token().Replace(await PageAsync(reader, "/e/"), ""), "the only library: the home page is its page");
    }

    /// <summary>With several libraries, the home page lists them, each linking to its page.</summary>
    [TestMethod]
    public async Task WithSeveralLibrariesTheHomeListsThem()
    {
        var (library, reader) = await StartAsync();
        await using var _l = library;
        using var _r = reader;
        using (var made = await library.Admin.PostAsJsonAsync("/api/libraries", new LibraryInput("Comics"), ApiClients.Json, Cancel))
            Assert.IsTrue(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync(Cancel));
        var comics = (await library.Factory.Services.GetRequiredService<AccessService>().ListLibrariesAsync(library.TenantId, Cancel)).Single(l => l.Name == "Comics");

        var html = await PageAsync(reader, "/e/");

        StringAssert.Contains(html, "<h1>Libraries</h1>");
        StringAssert.Contains(html, $"<a href=\"/e/l/{library.LibraryId}/\">Library</a>");
        StringAssert.Contains(html, $"<a href=\"/e/l/{comics.Id}/\">Comics</a>");
    }

    /// <summary>
    /// A row: the small cover (its size set, from /e), the title as a link to the book (the file's name without one),
    /// authors, series and number. The cover answers.
    /// </summary>
    [TestMethod]
    public async Task ARowShowsTheBook()
    {
        var (library, reader) = await StartAsync(
            ("Dune.fb2", OpdsFeedTests.Fb2("Dune", author: "Frank Herbert", series: "Dune Chronicles", number: 2)),
            ("no title here.fb2", OpdsFeedTests.Fb2("", author: null)));
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;

        var html = await PageAsync(reader, $"/e/l/{id}/titles");

        StringAssert.Contains(html, "<h1>All books by title</h1>");
        StringAssert.Contains(html, $"<p class=\"crumb\"><a href=\"/e/l/{id}/\">Library</a></p>");
        StringAssert.Contains(html, "<span class=\"title\">Dune</span>");
        StringAssert.Contains(html, "<span class=\"authors\">Frank Herbert</span>");
        StringAssert.Contains(html, "<span class=\"series\">Dune Chronicles, 2</span>");
        StringAssert.Contains(html, "<span class=\"title\">no title here</span>", "the file's name for a book without a title");
        var cover = CoverLink().Match(html);
        Assert.IsTrue(cover.Success, "a cover with its size");
        StringAssert.StartsWith(cover.Groups[1].Value, $"/e/l/{id}/books/");
        using var image = await reader.GetAsync(WebUtility.HtmlDecode(cover.Groups[1].Value), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, image.StatusCode);
        Assert.AreEqual("image/jpeg", image.Content.Headers.ContentType?.MediaType);
        Assert.IsFalse(html.Contains("class=\"pager\"", StringComparison.Ordinal), "one page: no First, no Next");
    }

    /// <summary>
    /// 20 books a page (Q7), the next by key: newest first or A to Z, nothing twice or left out; First from the second
    /// page on, no Next on the last.
    /// </summary>
    [TestMethod]
    public async Task BooksComePageByPageByKey()
    {
        var books = Enumerable.Range(0, 45).Select(i => ($"Book {i:00}.fb2", OpdsFeedTests.Fb2($"Book {i:00}"))).ToArray();
        var (library, reader) = await StartAsync(books);
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;
        await using var db = await library.DbAsync(Cancel);
        var all = await db.Books.Select(b => new { b.Id, b.Title }).ToListAsync(Cancel);

        foreach (var order in new[] { "titles", "recent" })
        {
            var seen = new List<int>();
            var pages = new List<int>();
            string? address = $"/e/l/{id}/{order}";
            while (address is not null)
            {
                var html = await PageAsync(reader, address);
                var ids = BookIds(html, id);
                seen.AddRange(ids);
                pages.Add(ids.Count);
                Assert.AreEqual(pages.Count > 1, html.Contains($"href=\"/e/l/{id}/{order}\">First page</a>", StringComparison.Ordinal), $"{order} page {pages.Count}: First");
                address = NextOf(html);
                if (address is not null)
                    StringAssert.StartsWith(address, $"/e/l/{id}/{order}?after=");
            }

            CollectionAssert.AreEqual(new[] { 20, 20, 5 }, pages, order);
            CollectionAssert.AreEquivalent(all.Select(b => b.Id).ToList(), seen, $"{order}: every book once");
            if (order == "titles")
                CollectionAssert.AreEqual(all.OrderBy(b => b.Title, StringComparer.Ordinal).Select(b => b.Id).ToList(), seen, "A to Z");
        }

        // A cursor that isn't one: the first page, not an error.
        Assert.HasCount(20, BookIds(await PageAsync(reader, $"/e/l/{id}/titles?after=not-a-cursor"), id));
    }

    /// <summary>
    /// A book a content filter hides is in no list and no "recently added", nor its cover; a library one can't browse
    /// is not found.
    /// </summary>
    [TestMethod]
    public async Task HiddenBooksAndOtherLibrariesAreAbsent()
    {
        var (library, reader) = await StartAsync(
            ("Seen.fb2", OpdsFeedTests.Fb2("Seen", author: "Ann Writer")),
            ("Hidden.fb2", OpdsFeedTests.Fb2("Hidden", author: "Secret Writer")));
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;
        int hidden;
        await using (var db = await library.DbAsync(Cancel))
        {
            hidden = await db.Books.Where(b => b.Title == "Hidden").Select(b => b.Id).SingleAsync(Cancel);
            var users = await db.Groups.SingleAsync(g => g.BuiltIn == BuiltInGroup.Users, Cancel);
            var tag = await db.BookTags.Where(l => l.BookId == hidden && l.Tag!.Name.Contains("Secret")).Select(l => l.TagId).SingleAsync(Cancel);
            db.GroupTagFilters.Add(new GroupTagFilter { GroupId = users.Id, TagId = tag, Deny = true });
            await db.SaveChangesAsync(Cancel);
        }
        library.Factory.Services.GetRequiredService<AccessChanges>().Changed();

        foreach (var address in new[] { $"/e/l/{id}/", $"/e/l/{id}/titles", $"/e/l/{id}/recent" })
        {
            var ids = BookIds(await PageAsync(reader, address), id);
            Assert.HasCount(1, ids, address);
            Assert.DoesNotContain(hidden, ids, address);
        }
        using (var cover = await reader.GetAsync($"/e/l/{id}/books/{hidden}/cover/s", Cancel))
            Assert.AreEqual(HttpStatusCode.NotFound, cover.StatusCode, "the hidden book's cover");

        foreach (var address in new[] { $"/e/l/{id + 1000}/", $"/e/l/{id + 1000}/titles", $"/e/l/{id}/books/1/cover/huge" })
        {
            using var response = await reader.GetAsync(address, Cancel);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, address);
            StringAssert.Contains(await EReaderTests.PlainPageAsync(response, Cancel), "Not found", address);
        }

        using var admin = await EReaderTests.SignedInBrowserAsync(library.Factory, ApiClients.AdminName, Cancel);
        Assert.HasCount(2, BookIds(await PageAsync(admin, $"/e/l/{id}/titles"), id), "the filter is the Users group's only");
    }

    /// <summary>A form token's value: a new one on every page.</summary>
    [GeneratedRegex(@"value=""CfDJ8[^""]*""")]
    private static partial Regex Token();

    [GeneratedRegex(@"href=""/e/l/(\d+)/books/(\d+)""")]
    private static partial Regex BookLink();

    [GeneratedRegex(@"<a class=""button next"" href=""([^""]+)"" rel=""next"">")]
    private static partial Regex NextLink();

    [GeneratedRegex(@"<img class=""cover"" src=""([^""]+)"" width=""48"" height=""72"" alt[ />]")]
    private static partial Regex CoverLink();
}
