using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>The e-reader mode's browsing (stage 8 chunk 5): fields, their first letters and values, a value's books, plain tags.</summary>
public sealed partial class EReaderListTests
{
    private sealed record ValueRow(string Href, string Name, string? Count);

    /// <summary>The values a page lists: each row's link, name and count.</summary>
    private static List<ValueRow> Values(string html) => ValueLink().Matches(html)
        .Select(m => new ValueRow(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value), m.Groups[3].Success ? m.Groups[3].Value : null))
        .ToList();

    private async Task<Dictionary<int, string>> TitlesAsync(TestLibrary library)
    {
        await using var db = await library.DbAsync(Cancel);
        return await db.Books.ToDictionaryAsync(b => b.Id, b => b.Title ?? "", Cancel);
    }

    /// <summary>
    /// A field with few values lists them at once, each with its books in the library, up to the library; a value opens
    /// its books, up to the field; a field that doesn't exist is not found.
    /// </summary>
    [TestMethod]
    public async Task AFieldsValuesOpenTheirBooks()
    {
        var (library, reader) = await StartAsync(
            ("a.fb2", OpdsFeedTests.Fb2("The Hobbit", "John Tolkien")),
            ("b.fb2", OpdsFeedTests.Fb2("The Silmarillion", "John Tolkien")),
            ("c.fb2", OpdsFeedTests.Fb2("Dune", "Frank Herbert")));
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;
        var titles = await TitlesAsync(library);

        var authors = await PageAsync(reader, $"/e/l/{id}/fields/author");
        StringAssert.Contains(authors, "<h1>Authors</h1>");
        StringAssert.Contains(authors, $"<p class=\"crumb\"><a href=\"/e/l/{id}/\">Library</a></p>");
        Assert.IsFalse(authors.Contains("class=\"letters\"", StringComparison.Ordinal), "one page: no letters");
        var values = Values(authors);
        CollectionAssert.AreEqual(new[] { "Frank Herbert", "John Tolkien" }, values.Select(v => v.Name).ToArray(), "by sort name");
        Assert.AreEqual("2 books", values[1].Count);
        StringAssert.StartsWith(values[1].Href, $"/e/l/{id}/tags/");

        var books = await PageAsync(reader, values[1].Href);
        StringAssert.Contains(books, "<h1>John Tolkien</h1>");
        StringAssert.Contains(books, $"<p class=\"crumb\"><a href=\"/e/l/{id}/fields/author\">Authors</a></p>");
        CollectionAssert.AreEqual(new[] { "The Hobbit", "The Silmarillion" }, BookIds(books, id).Select(b => titles[b]).ToArray());

        foreach (var address in new[] { $"/e/l/{id}/fields/nothing", $"/e/l/{id + 1000}/fields/author", $"/e/l/{id}/tags/999999", $"/e/l/{id}/tags/999999/books" })
        {
            using var missing = await reader.GetAsync(address, Cancel);
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode, address);
        }
    }

    /// <summary>A series' books come by their numbers.</summary>
    [TestMethod]
    public async Task ASeriesComesByItsNumbers()
    {
        var (library, reader) = await StartAsync(
            ("3.fb2", OpdsFeedTests.Fb2("Third", series: "Saga", number: 3)),
            ("1.fb2", OpdsFeedTests.Fb2("First", series: "Saga", number: 1)),
            ("2.fb2", OpdsFeedTests.Fb2("Second", series: "Saga", number: 2)));
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;
        var titles = await TitlesAsync(library);

        var series = Values(await PageAsync(reader, $"/e/l/{id}/fields/series")).Single();
        var html = await PageAsync(reader, series.Href);

        CollectionAssert.AreEqual(new[] { "First", "Second", "Third" }, BookIds(html, id).Select(b => titles[b]).ToArray());
        StringAssert.Contains(html, "<span class=\"series\">Saga, 2</span>");
    }

    /// <summary>
    /// More values than a page: the field opens on the first letters they start with (any alphabet) and All; a letter
    /// on its values from there, up to the letters, Next by key keeping the letter, First back to the letter.
    /// </summary>
    [TestMethod]
    public async Task ManyValuesOpenOnTheirFirstLetters()
    {
        var surnames = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".Select(c => $"{c}ster").Concat("БВГДЕЖЗИКЛМНОПРСТУФХЦЧШЭЮЯ".Select(c => $"{c}ов")).ToList();
        var (library, reader) = await StartAsync(surnames.Select((name, i) => ($"{i}.fb2", OpdsFeedTests.Fb2($"Book {i}", $"Ann {name}"))).ToArray());
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;

        var letters = await PageAsync(reader, $"/e/l/{id}/fields/author");
        StringAssert.Contains(letters, "<nav class=\"letters\" aria-label=\"First letters\">");
        StringAssert.Contains(letters, ">Б</a>", "other scripts written as they are, not as entities");
        var buttons = LetterButton().Matches(letters).Select(m => (Href: WebUtility.HtmlDecode(m.Groups[1].Value), Text: m.Groups[2].Value)).ToList();
        CollectionAssert.IsSubsetOf(new[] { "A", "Z", "Б", "Я" }, buttons.Select(b => b.Text).ToArray());
        Assert.AreEqual(("/e/l/" + id + "/fields/author?from=A", "A"), buttons[0]);
        StringAssert.Contains(letters, $"<a href=\"/e/l/{id}/fields/author?from=\">All, from the start</a>");

        var fromB = await PageAsync(reader, $"/e/l/{id}/fields/author?from=" + Uri.EscapeDataString("Б"));
        StringAssert.Contains(fromB, "<h1>Authors: Б</h1>");
        StringAssert.Contains(fromB, $"<p class=\"crumb\"><a href=\"/e/l/{id}/fields/author\">Authors</a></p>");
        Assert.Contains("Бов", Values(fromB)[0].Name);

        var seen = new List<string>();
        string? address = $"/e/l/{id}/fields/author?from=";
        var pages = 0;
        while (address is not null)
        {
            var html = await PageAsync(reader, address);
            pages++;
            seen.AddRange(Values(html).Select(v => v.Name));
            Assert.AreEqual(pages > 1, html.Contains($"href=\"/e/l/{id}/fields/author?from=\">First page</a>", StringComparison.Ordinal), $"page {pages}: First");
            address = NextOf(html);
            if (address is not null)
                StringAssert.StartsWith(address, $"/e/l/{id}/fields/author?from=&after=");
        }

        Assert.HasCount(surnames.Count, seen, "every value once, page by page");
        CollectionAssert.AllItemsAreUnique(seen);
        Assert.AreEqual(3, pages);
    }

    /// <summary>
    /// Plain tags are listed; a tag with tags under it lists All its books and those tags; a merged tag's old address
    /// goes on to the tag it became; a tag the content filters deny is neither listed nor there.
    /// </summary>
    [TestMethod]
    public async Task PlainTagsAndTagsUnderTags()
    {
        var (library, reader) = await StartAsync(("a.fb2", OpdsFeedTests.Fb2("The Hobbit")), ("b.fb2", OpdsFeedTests.Fb2("Dune")));
        await using var _l = library;
        using var _r = reader;
        var id = library.LibraryId;
        var titles = await TitlesAsync(library);
        var parent = await CreateTagAsync(library, new Librarion.Core.Tagging.TagInput("Speculative"));
        var fairy = await CreateTagAsync(library, new Librarion.Core.Tagging.TagInput("Fairy", ParentIds: [parent]));
        var secret = await CreateTagAsync(library, new Librarion.Core.Tagging.TagInput("Secret"));
        var hobbit = titles.Single(t => t.Value == "The Hobbit").Key;
        var dune = titles.Single(t => t.Value == "Dune").Key;
        await TagAsync(library, hobbit, fairy);
        await TagAsync(library, dune, secret);

        var plain = await PageAsync(reader, $"/e/l/{id}/tags");
        StringAssert.Contains(plain, "<h1>Tags</h1>");
        CollectionAssert.IsSubsetOf(new[] { "Fairy", "Secret", "Speculative" }, Values(plain).Select(v => v.Name).ToArray());

        var tree = await PageAsync(reader, $"/e/l/{id}/tags/{parent}");
        StringAssert.Contains(tree, "<h1>Speculative</h1>");
        StringAssert.Contains(tree, $"<p class=\"crumb\"><a href=\"/e/l/{id}/tags\">Tags</a></p>");
        StringAssert.Contains(tree, $"<a href=\"/e/l/{id}/tags/{parent}/books\">All its books</a>");
        CollectionAssert.AreEqual(new[] { new ValueRow($"/e/l/{id}/tags/{fairy}", "Fairy", null) }, Values(tree));
        var all = await PageAsync(reader, $"/e/l/{id}/tags/{parent}/books");
        CollectionAssert.AreEqual(new[] { hobbit }, BookIds(all, id), "the books of the tags under it too");
        StringAssert.Contains(all, $"<p class=\"crumb\"><a href=\"/e/l/{id}/tags/{parent}\">Speculative</a></p>");
        CollectionAssert.AreEqual(new[] { hobbit }, BookIds(await PageAsync(reader, $"/e/l/{id}/tags/{fairy}"), id), "a leaf: its books");

        await using (var db = await library.DbAsync(Cancel))
        {
            var users = await db.Groups.SingleAsync(g => g.BuiltIn == BuiltInGroup.Users, Cancel);
            db.GroupTagFilters.Add(new GroupTagFilter { GroupId = users.Id, TagId = secret, Deny = true });
            await db.SaveChangesAsync(Cancel);
        }
        library.Factory.Services.GetRequiredService<AccessChanges>().Changed();
        CollectionAssert.DoesNotContain(Values(await PageAsync(reader, $"/e/l/{id}/tags")).Select(v => v.Name).ToList(), "Secret");
        foreach (var hidden in new[] { $"/e/l/{id}/tags/{secret}", $"/e/l/{id}/tags/{secret}/books" })
        {
            using var response = await reader.GetAsync(hidden, Cancel);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, hidden);
        }

        using (var merged = await library.Admin.PostAsJsonAsync($"/api/tags/{fairy}/merge", new TagEndpoints.MergeInput(parent), ApiClients.Json, Cancel))
            Assert.IsTrue(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync(Cancel));
        foreach (var (old, now) in new[] { ($"/e/l/{id}/tags/{fairy}", $"/e/l/{id}/tags/{parent}"), ($"/e/l/{id}/tags/{fairy}/books", $"/e/l/{id}/tags/{parent}/books") })
        {
            using var moved = await reader.GetAsync(old, Cancel);
            Assert.AreEqual(HttpStatusCode.MovedPermanently, moved.StatusCode, old);
            Assert.AreEqual(now, moved.Headers.Location?.OriginalString, old);
        }
    }

    private async Task<int> CreateTagAsync(TestLibrary library, Librarion.Core.Tagging.TagInput input)
    {
        using var made = await library.Admin.PostAsJsonAsync("/api/tags", input, ApiClients.Json, Cancel);
        Assert.IsTrue(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync(Cancel));
        return (await made.Content.ReadFromJsonAsync<Librarion.Core.Tagging.TagDefinition>(ApiClients.Json, Cancel))!.Id;
    }

    private async Task TagAsync(TestLibrary library, int book, int tag)
    {
        using var tagged = await library.Admin.PostAsJsonAsync("/api/books/tags",
            new BookEndpoints.SelectionInput([book], [new Librarion.Core.Catalog.BookTagEdit(tag)]), ApiClients.Json, Cancel);
        Assert.IsTrue(tagged.IsSuccessStatusCode, await tagged.Content.ReadAsStringAsync(Cancel));
    }

    [GeneratedRegex(@"<a href=""(/e/l/\d+/tags/\d+)"">\s*<span class=""name"">([^<]*)</span>(?:\s*<span class=""count"">([^<]*)</span>)?")]
    private static partial Regex ValueLink();

    [GeneratedRegex(@"<a class=""button"" href=""([^""]+)"">([^<]+)</a>")]
    private static partial Regex LetterButton();
}
