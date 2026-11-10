using System.Net;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Tests;

/// <summary>
/// A book edited on its page over HTTP, on both providers (agents/docs/features/book-page.md, stage 6 chunk 6, D-6.3):
/// columns, a field's tags in order, plain tags; what is set survives the file being read again; counts, sort columns and
/// the work key follow; the rights per part.
/// </summary>
[TestClass]
public sealed class BookEditTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string[] authors, string? keywords = null, string? series = null, string? annotation = null) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info>{string.Concat(authors.Select(a => $"<author><first-name>{a.Split(' ')[0]}</first-name><last-name>{a.Split(' ')[^1]}</last-name></author>"))}<book-title>{title}</book-title>{(annotation is null ? "" : $"<annotation><p>{annotation}</p></annotation>")}{(keywords is null ? "" : $"<keywords>{keywords}</keywords>")}<lang>en</lang>{(series is null ? "" : $"<sequence name=\"{series}\" number=\"1\"/>")}</title-info></description><body><p>{title}</p></body></FictionBook>
        """;

    [TestMethod]
    public async Task EditsStayWhenTheFileIsReadAgain()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        var file = shelf.Write("one.fb2", Fb2("Teh Shining", ["Stephen King", "Peter Straub"], "Horror, Typo tag", "Dark", "Imported text"));
        await shelf.ScanAsync(Cancel);
        var id = await BookIdAsync(shelf);
        var (king, straub) = (await TagIdAsync(shelf, "Stephen King"), await TagIdAsync(shelf, "Peter Straub"));
        var version = (await shelf.Admin.GetAsync<BookDetail>($"/api/books/{id}", Cancel)).CoverVersion;

        var book = await EditAsync(shelf, id, new BookEdit(
            new BookColumnsEdit("The Shining", "My own words", 8),
            [
                new BookFieldEdit("author", [new BookTagEdit(straub), new BookTagEdit(king)]),
                new BookFieldEdit("series", [new BookTagEdit(Name: "Dark", Number: 2.5)]),
                new BookFieldEdit(null, [new BookTagEdit(Name: "Horror"), new BookTagEdit(Name: "Favourites")]),
            ]));

        Assert.AreEqual("The Shining", book.Title);
        Assert.AreEqual((byte)8, book.Rating);
        Assert.IsNotNull(version);
        Assert.AreNotEqual(version, book.CoverVersion, "the cover's address changes: a cached generated cover showed the old title");
        CollectionAssert.AreEqual(new[] { "Peter Straub", "Stephen King" }, book.Tags.Where(t => t.Field == "author").Select(t => t.Name).ToList());
        CollectionAssert.AreEquivalent(new[] { "Horror", "Favourites" }, book.Tags.Where(t => t.Field == null).Select(t => t.Name).ToList());
        Assert.AreEqual(2.5, book.Tags.Single(t => t.Field == "series").Number);
        await AssertBookAsync(shelf, id, "Straub, Peter", "The Shining");
        Assert.AreEqual((0, 0), await CountsAsync(shelf, await TagIdAsync(shelf, "Typo tag")), "taken off: counted no more");
        Assert.AreEqual((1, 1), await CountsAsync(shelf, await TagIdAsync(shelf, "Favourites")), "made by name and counted");
        Assert.AreEqual(1, await CountAsync(shelf, "\"The Shining\""), "the search text follows the title");

        // The file changes (another description, a new keyword): read again, the edits stay, the new keyword comes in.
        File.WriteAllText(file, Fb2("Teh Shining 2nd", ["Stephen King", "Peter Straub"], "Horror, Typo tag, Classic", "Dark", "Changed text"));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        await shelf.ScanAsync(Cancel);

        book = await shelf.Admin.GetAsync<BookDetail>($"/api/books/{id}", Cancel);
        Assert.AreEqual("The Shining", book.Title);
        Assert.AreEqual("My own words", book.Description);
        Assert.AreEqual((byte)8, book.Rating);
        CollectionAssert.AreEqual(new[] { "Peter Straub", "Stephen King" }, book.Tags.Where(t => t.Field == "author").Select(t => t.Name).ToList());
        CollectionAssert.AreEquivalent(new[] { "Horror", "Favourites", "Classic" }, book.Tags.Where(t => t.Field == null).Select(t => t.Name).ToList());
        Assert.AreEqual(2.5, book.Tags.Single(t => t.Field == "series").Number);
        await AssertBookAsync(shelf, id, "Straub, Peter", "The Shining");
        Assert.AreEqual((1, 1), await CountsAsync(shelf, king));
        Assert.AreEqual((0, 0), await CountsAsync(shelf, await TagIdAsync(shelf, "Typo tag")));
        await using (var db = await shelf.DbAsync(Cancel))
        {
            var field = await db.Tags.Where(t => t.FieldKey == "author").Select(t => t.Id).SingleAsync(Cancel);
            Assert.AreEqual(1, await db.TagCounts.Where(c => c.TagId == field).Select(c => c.TotalCount).SingleAsync(Cancel), "the field counts the book once");
        }

        // Given back, the tag is no longer kept off.
        await EditAsync(shelf, id, new BookEdit(Fields: [new BookFieldEdit(null, [new BookTagEdit(Name: "Typo tag")])]));
        await using (var db = await shelf.DbAsync(Cancel))
        {
            var typo = await db.Tags.Where(t => t.Name == "Typo tag").Select(t => t.Id).SingleAsync(Cancel);
            Assert.IsFalse(await db.BookTagSuppressions.AnyAsync(s => s.BookId == id && s.TagId == typo, Cancel));
        }
    }

    [TestMethod]
    public async Task EditsAreChecked()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", ["Ann Writer"]));
        await shelf.ScanAsync(Cancel);
        var id = await BookIdAsync(shelf);
        var author = await TagIdAsync(shelf, "Ann Writer");

        await AssertInvalidAsync(shelf, id, new BookEdit(new BookColumnsEdit("One", null, 11)), "rating.invalid");
        await AssertInvalidAsync(shelf, id, new BookEdit(Fields: [new BookFieldEdit("nope", [])]), "field.unknown");
        await AssertInvalidAsync(shelf, id, new BookEdit(Fields: [new BookFieldEdit(null, [new BookTagEdit(author)])]), "tag.unknown");
        await AssertInvalidAsync(shelf, id, new BookEdit(Fields: [new BookFieldEdit("series", [new BookTagEdit(Name: "S", Number: -1)])]), "number.invalid");
        await AssertInvalidAsync(shelf, id, new BookEdit(Fields: [new BookFieldEdit(null, [new BookTagEdit(Name: " ")])]), "name.required");
        using var missing = await shelf.Admin.PutAsJsonAsync("/api/books/99999", new BookEdit(), Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

        // Clearing: no title shows the file name, rating 0 is none, no authors leaves the sort author empty.
        var book = await EditAsync(shelf, id, new BookEdit(new BookColumnsEdit("", "", 0), [new BookFieldEdit("author", [])]));
        Assert.IsNull(book.Title);
        Assert.IsNull(book.Rating);
        await AssertBookAsync(shelf, id, "", "one");
        Assert.AreEqual((0, 0), await CountsAsync(shelf, author));
    }

    [TestMethod]
    public async Task TaggingAndEditingNeedTheirRightsInTheLibrary()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", ["Ann Writer"]));
        await shelf.ScanAsync(Cancel);
        var id = await BookIdAsync(shelf);
        var groups = await shelf.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("curator", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Curators).Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(shelf.Factory, "reader", ApiClients.Password, Cancel);
        using var curator = await ApiClients.SignInAsync(shelf.Factory, "curator", ApiClients.Password, Cancel);

        var seen = await reader.GetAsync<BookDetail>($"/api/books/{id}", Cancel);
        Assert.IsFalse(seen.CanTag || seen.CanEdit);
        using var refused = await reader.PutAsJsonAsync($"/api/books/{id}", new BookEdit(Fields: [new BookFieldEdit(null, [new BookTagEdit(Name: "Mine")])]), Cancel);
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);

        var curated = await curator.GetAsync<BookDetail>($"/api/books/{id}", Cancel);
        Assert.IsTrue(curated.CanTag && curated.CanEdit);
        using var tagged = await curator.PutAsJsonAsync($"/api/books/{id}", new BookEdit(new BookColumnsEdit("Uno", null, null), [new BookFieldEdit(null, [new BookTagEdit(Name: "Mine")])]), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, tagged.StatusCode, await tagged.Content.ReadAsStringAsync(Cancel));
    }

    // ---- Helpers ----

    private async Task<BookDetail> EditAsync(Shelf shelf, int id, BookEdit edit)
    {
        using var response = await shelf.Admin.PutAsJsonAsync($"/api/books/{id}", edit, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
        return (await response.Content.ReadFromJsonAsync<BookDetail>(ApiClients.Json, Cancel))!;
    }

    private async Task AssertInvalidAsync(Shelf shelf, int id, BookEdit edit, string code)
    {
        using var response = await shelf.Admin.PutAsJsonAsync($"/api/books/{id}", edit, Cancel);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(Cancel), code);
    }

    private async Task AssertBookAsync(Shelf shelf, int id, string sortAuthor, string title)
    {
        await using var db = await shelf.DbAsync(Cancel);
        var book = await db.Books.SingleAsync(b => b.Id == id, Cancel);
        Assert.AreEqual(sortAuthor, book.SortAuthor);
        Assert.AreEqual(BookTitles.SortKey(title), book.SortTitle);
        var author = await db.BookTags.Where(l => l.BookId == id && l.Tag!.Field!.FieldKey == "author").OrderBy(l => l.Ordinal).Select(l => l.Tag!.Name).FirstOrDefaultAsync(Cancel);
        Assert.AreEqual(PathKeys.Of(TagNames.Normalize(book.Title ?? "one"), author is null ? "" : TagNames.Normalize(author)), book.WorkKey);
    }

    private async Task<int> BookIdAsync(Shelf shelf)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Books.Select(b => b.Id).SingleAsync(Cancel);
    }

    private async Task<int> TagIdAsync(Shelf shelf, string name)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Tags.Where(t => t.Name == name).Select(t => t.Id).SingleAsync(Cancel);
    }

    private async Task<(int, int)> CountsAsync(Shelf shelf, int tagId)
    {
        await using var db = await shelf.DbAsync(Cancel);
        var row = await db.TagCounts.SingleOrDefaultAsync(c => c.TagId == tagId && c.LibraryId == shelf.LibraryId, Cancel);
        return row is null ? (0, 0) : (row.BookCount, row.TotalCount);
    }

    private async Task<int> CountAsync(Shelf shelf, string q) =>
        (await shelf.Admin.GetAsync<Page<BookListItem>>($"/api/books?library={shelf.LibraryId}&limit=50&q={Uri.EscapeDataString(q)}", Cancel)).Items.Count;
}
