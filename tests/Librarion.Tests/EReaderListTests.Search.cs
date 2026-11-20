using System.Net;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>The e-reader mode's search (stage 8 chunk 4): the search language, pages by key, blank and cut searches.</summary>
public sealed partial class EReaderListTests
{
    /// <summary>
    /// The library's search speaks the search language (words, a field's value, a minus), lists by title, keeps the
    /// words in its box (encoded) and links each book under /e.
    /// </summary>
    [TestMethod]
    public async Task ASearchSpeaksTheSearchLanguage()
    {
        var (library, reader) = await StartAsync(
            ("Dune.fb2", OpdsFeedTests.Fb2("Dune", author: "Frank Herbert")),
            ("Dune Messiah.fb2", OpdsFeedTests.Fb2("Dune Messiah", author: "Frank Herbert")),
            ("Children.fb2", OpdsFeedTests.Fb2("Children of Dune", author: "Ann Writer")),
            ("Other.fb2", OpdsFeedTests.Fb2("Other", author: "Ann Writer")));
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;
        await using var db = await library.DbAsync(Cancel);
        var titles = await db.Books.ToDictionaryAsync(b => b.Id, b => b.Title ?? "", Cancel);
        List<string> Found(string html) => BookIds(html, id).Select(b => titles[b]).ToList();

        var html = await PageAsync(reader, $"/e/l/{id}/search?q=dune");
        StringAssert.Contains(html, "<h1>Search</h1>");
        StringAssert.Contains(html, $"<p class=\"crumb\"><a href=\"/e/l/{id}/\">Library</a></p>");
        StringAssert.Contains(html, "name=\"q\" type=\"search\" value=\"dune\"");
        StringAssert.Contains(html, "<h2>Books matching dune</h2>");
        CollectionAssert.AreEqual(new[] { "Children of Dune", "Dune", "Dune Messiah" }, Found(html), "by title");
        Assert.IsFalse(html.Contains("class=\"pager\"", StringComparison.Ordinal), "one page");

        CollectionAssert.AreEqual(new[] { "Dune", "Dune Messiah" }, Found(await PageAsync(reader, $"/e/l/{id}/search?q=" + Uri.EscapeDataString("author:\"Frank Herbert\""))));
        CollectionAssert.AreEqual(new[] { "Children of Dune" }, Found(await PageAsync(reader, $"/e/l/{id}/search?q=" + Uri.EscapeDataString("dune -author:\"Frank Herbert\""))));
        CollectionAssert.AreEqual(new[] { "Dune Messiah" }, Found(await PageAsync(reader, $"/e/l/{id}/search?q=" + Uri.EscapeDataString("\"dune messiah\""))));

        var quoted = await PageAsync(reader, $"/e/l/{id}/search?q=" + Uri.EscapeDataString("\"<b>\" & x"));
        StringAssert.Contains(quoted, "value=\"&quot;&lt;b&gt;&quot; &amp; x\"", "the words encoded in the box");
        Assert.IsFalse(quoted.Contains("<b>", StringComparison.Ordinal));
    }

    /// <summary>Results come 20 a page by key, the Next link keeping the words; nothing twice or left out; First from page 2.</summary>
    [TestMethod]
    public async Task SearchResultsComePageByPage()
    {
        var books = Enumerable.Range(0, 45).Select(i => ($"Saga {i:00}.fb2", OpdsFeedTests.Fb2($"Saga {i:00}")))
            .Append(("Dune.fb2", OpdsFeedTests.Fb2("Dune"))).ToArray();
        var (library, reader) = await StartAsync(books);
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;
        await using var db = await library.DbAsync(Cancel);
        var sagas = await db.Books.Where(b => b.Title!.StartsWith("Saga")).OrderBy(b => b.Title).Select(b => b.Id).ToListAsync(Cancel);

        var seen = new List<int>();
        var pages = new List<int>();
        string? address = $"/e/l/{id}/search?q=saga";
        while (address is not null)
        {
            var html = await PageAsync(reader, address);
            var ids = BookIds(html, id);
            seen.AddRange(ids);
            pages.Add(ids.Count);
            Assert.AreEqual(pages.Count > 1, html.Contains($"href=\"/e/l/{id}/search?q=saga\">First page</a>", StringComparison.Ordinal), $"page {pages.Count}: First");
            StringAssert.Contains(html, "value=\"saga\"", "the words kept on every page");
            address = NextOf(html);
            if (address is not null)
                StringAssert.StartsWith(address, $"/e/l/{id}/search?q=saga&after=");
        }

        CollectionAssert.AreEqual(new[] { 20, 20, 5 }, pages);
        CollectionAssert.AreEqual(sagas, seen, "every match once, A to Z");
    }

    /// <summary>
    /// A blank search, or one of signs only, lists nothing and says how to search; one finding nothing says so with
    /// the help; one too long says only its start was searched.
    /// </summary>
    [TestMethod]
    public async Task BlankEmptyAndCutSearchesAreSaidPlainly()
    {
        var (library, reader) = await StartAsync(("Dune.fb2", OpdsFeedTests.Fb2("Dune")));
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;

        foreach (var q in new[] { "", "q=", "q=%20%20", "q=-", "q=%22%22", "q=tag%3A" })
        {
            var html = await PageAsync(reader, $"/e/l/{id}/search?{q}");
            Assert.IsEmpty(BookIds(html, id), q);
            StringAssert.Contains(html, "Type words of titles, authors, series or tags in the box above.", q);
            StringAssert.Contains(html, "<h2>How to search</h2>", q);
            StringAssert.Contains(html, "<dt><code>author:&quot;Frank Herbert&quot;</code></dt>", q);
            Assert.IsFalse(html.Contains("No books match", StringComparison.Ordinal), q);
        }

        var none = await PageAsync(reader, $"/e/l/{id}/search?q=nothing-like-it");
        Assert.IsEmpty(BookIds(none, id));
        StringAssert.Contains(none, "<p class=\"notice\">No books match this search.</p>");
        StringAssert.Contains(none, "<h2>How to search</h2>");

        var many = string.Join(' ', Enumerable.Range(0, SearchQuery.MaxTerms + 5).Select(i => i == 0 ? "dune" : "-zzz" + i));
        var cut = await PageAsync(reader, $"/e/l/{id}/search?q=" + Uri.EscapeDataString(many));
        StringAssert.Contains(cut, $"only its start was searched (at most {SearchQuery.MaxTerms} terms, {SearchQuery.MaxLength} characters)");
        Assert.HasCount(1, BookIds(cut, id), "its first terms still searched");
        Assert.IsFalse((await PageAsync(reader, $"/e/l/{id}/search?q=dune")).Contains("class=\"notice\"", StringComparison.Ordinal), "a short one: no notice");
    }

    /// <summary>A book a content filter hides is never found; a library one can't browse is not found; a visitor signs in first.</summary>
    [TestMethod]
    public async Task SearchLeavesHiddenBooksAndOtherLibrariesOut()
    {
        var (library, reader) = await StartAsync(
            ("Seen.fb2", OpdsFeedTests.Fb2("Seen Saga", author: "Ann Writer")),
            ("Hidden.fb2", OpdsFeedTests.Fb2("Hidden Saga", author: "Secret Writer")));
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;
        int hidden;
        await using (var db = await library.DbAsync(Cancel))
        {
            hidden = await db.Books.Where(b => b.Title == "Hidden Saga").Select(b => b.Id).SingleAsync(Cancel);
            var users = await db.Groups.SingleAsync(g => g.BuiltIn == BuiltInGroup.Users, Cancel);
            var tag = await db.BookTags.Where(l => l.BookId == hidden && l.Tag!.Name.Contains("Secret")).Select(l => l.TagId).SingleAsync(Cancel);
            db.GroupTagFilters.Add(new GroupTagFilter { GroupId = users.Id, TagId = tag, Deny = true });
            await db.SaveChangesAsync(Cancel);
        }
        library.Factory.Services.GetRequiredService<AccessChanges>().Changed();

        foreach (var q in new[] { "saga", "hidden", "author:\"Secret Writer\"" })
            Assert.DoesNotContain(hidden, BookIds(await PageAsync(reader, $"/e/l/{id}/search?q=" + Uri.EscapeDataString(q)), id), q);
        Assert.HasCount(1, BookIds(await PageAsync(reader, $"/e/l/{id}/search?q=saga"), id));

        using (var other = await reader.GetAsync($"/e/l/{id + 1000}/search?q=saga", Cancel))
            Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);

        using var visitor = library.Factory.CreateClient(new() { AllowAutoRedirect = false });
        using var signIn = await visitor.GetAsync($"/e/l/{id}/search?q=saga", Cancel);
        Assert.AreEqual(HttpStatusCode.Redirect, signIn.StatusCode);
        StringAssert.StartsWith(signIn.Headers.Location?.OriginalString, "/e/signin?return=");
    }
}
