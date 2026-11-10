using System.Net;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Merging;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>
/// Books merged, on both providers (stage 6 chunk 12, agents/docs/features/merging.md#merging-books-built-stage-6-chunk-12):
/// files, tags and identifiers moved, chosen values kept through a re-read, the merged book's addresses leading to the
/// kept one, counts, the log kept out of tag merges; refusals and rights; the Duplicates page by key and visibility;
/// Not a duplicate.
/// </summary>
[TestClass]
public sealed class BookMergeTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string author, string? isbn = null, string text = "", string? keywords = null) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info><author><first-name>{author.Split(' ')[0]}</first-name><last-name>{author.Split(' ')[^1]}</last-name></author><book-title>{title}</book-title>{(keywords is null ? "" : $"<keywords>{keywords}</keywords>")}</title-info>{(isbn is null ? "" : $"<publish-info><isbn>{isbn}</isbn></publish-info>")}</description><body><p>{text}</p></body></FictionBook>
        """;

    [TestMethod]
    public async Task AMergeMovesEverythingAndTheOldAddressesLeadToTheKeptBook()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("Dune.fb2", Fb2("Dune", "Frank Herbert", text: "one"));
        shelf.Write("dune (scan).fb2", Fb2("DUNE: the complete edition", "Frank Herbert", isbn: "9780441172719", text: "two", keywords: "classic"));
        await shelf.ScanAsync(Cancel);
        var (kept, merged) = (await BookOfAsync(shelf, "Dune.fb2"), await BookOfAsync(shelf, "dune (scan).fb2"));
        var oldFile = await shelf.UrlOfAsync("dune (scan).fb2", Cancel);
        var countsBefore = await LibraryCountAsync(shelf);

        using var response = await shelf.Admin.PostAsJsonAsync("/api/books/merge", new BookMergeInput(kept, [merged], TitleFrom: merged), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
        var book = (await response.Content.ReadFromJsonAsync<BookDetail>(ApiClients.Json, Cancel))!;

        Assert.AreEqual((kept, "DUNE: the complete edition", 2), (book.Id, book.Title, book.Files.Count));
        Assert.IsTrue(book.Tags.Any(t => t.Name == "classic"), "the plain tag moved");
        Assert.IsTrue(book.Identifiers.Any(i => i.Key == "isbn"), "the identifier moved");
        Assert.AreEqual(countsBefore - 1, await LibraryCountAsync(shelf));
        await using (var db = await shelf.DbAsync(Cancel))
        {
            var classic = await db.Tags.Where(t => t.Name == "classic").Select(t => t.Id).SingleAsync(Cancel);
            Assert.AreEqual(1, await db.TagCounts.Where(c => c.TagId == classic).Select(c => c.BookCount).SingleAsync(Cancel), "still one book with it");
            Assert.AreEqual(0, await db.DuplicateGroups.CountAsync(g => !g.Noise, Cancel), "one book now: no group");
            Assert.AreEqual(MergeKind.Book, await db.MergeLogs.Select(l => l.Kind).SingleAsync(Cancel));
        }

        // The merged book's addresses: its page says where it went, its file and cover still answer.
        Assert.AreEqual(kept, (await shelf.Admin.GetAsync<BookDetail>($"/api/books/{merged}", Cancel)).MergedIntoId);
        using (var file = await shelf.Admin.GetAsync(oldFile, Cancel))
            Assert.AreEqual(HttpStatusCode.OK, file.StatusCode);
        using (var cover = await shelf.Admin.GetAsync($"/api/books/{merged}/cover/s", Cancel))
            Assert.AreEqual(HttpStatusCode.OK, cover.StatusCode);
        Assert.AreEqual(0, (await shelf.Admin.GetAsync<List<MergeLogInfo>>("/api/merges", Cancel)).Count, "a book merge isn't a tag merge");

        // A scan reads the files again: one book, the chosen title kept.
        shelf.Write("dune (scan).fb2", Fb2("DUNE: the complete edition", "Frank Herbert", isbn: "9780441172719", text: "two, read again"));
        await shelf.ScanAsync(Cancel);
        await using (var db = await shelf.DbAsync(Cancel))
        {
            Assert.AreEqual(1, await db.Books.CountAsync(b => b.MergedIntoId == null, Cancel));
            Assert.AreEqual("DUNE: the complete edition", await db.Books.Where(b => b.Id == kept).Select(b => b.Title).SingleAsync(Cancel));
        }
    }

    [TestMethod]
    public async Task WrongMergesAreRefused()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("a.fb2", Fb2("A", "Ann Writer", text: "a"));
        shelf.Write("b.fb2", Fb2("B", "Ann Writer", text: "b"));
        await shelf.ScanAsync(Cancel);
        var (a, b) = (await BookOfAsync(shelf, "a.fb2"), await BookOfAsync(shelf, "b.fb2"));
        var other = await OtherLibraryBookAsync(shelf);

        foreach (var (input, code) in new (BookMergeInput, string)[]
        {
            (new BookMergeInput(a, []), "merge.books"),
            (new BookMergeInput(a, [a, b]), "merge.books"),
            (new BookMergeInput(a, [b], TitleFrom: other), "merge.choice"),
            (new BookMergeInput(a, [other]), "merge.library"),
        })
        {
            using var response = await shelf.Admin.PostAsJsonAsync("/api/books/merge", input, Cancel);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, code);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(Cancel), code);
        }

        using (var merged = await shelf.Admin.PostAsJsonAsync("/api/books/merge", new BookMergeInput(a, [b]), Cancel))
            Assert.AreEqual(HttpStatusCode.OK, merged.StatusCode);
        using var again = await shelf.Admin.PostAsJsonAsync("/api/books/merge", new BookMergeInput(b, [a]), Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, again.StatusCode, "a merged book can't be merged again");
    }

    [TestMethod]
    public async Task OnlyThoseWhoMergeBooksMergeThem()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("a.fb2", Fb2("Twin", "Ann Writer", text: "a"));
        shelf.Write("b.fb2", Fb2("Twin", "Ann Writer", text: "b"));
        await shelf.ScanAsync(Cancel);
        var (a, b) = (await BookOfAsync(shelf, "a.fb2"), await BookOfAsync(shelf, "b.fb2"));
        var groups = await shelf.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("curator", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Curators).Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(shelf.Factory, "reader", ApiClients.Password, Cancel);
        using var curator = await ApiClients.SignInAsync(shelf.Factory, "curator", ApiClients.Password, Cancel);

        Assert.IsFalse((await reader.GetAsync<BookDetail>($"/api/books/{a}", Cancel)).CanMerge);
        using (var list = await reader.GetAsync($"/api/duplicates?library={shelf.LibraryId}", Cancel))
            Assert.AreEqual(HttpStatusCode.Forbidden, list.StatusCode);
        using (var refused = await reader.PostAsJsonAsync("/api/books/merge", new BookMergeInput(a, [b]), Cancel))
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);

        Assert.IsTrue((await curator.GetAsync<BookDetail>($"/api/books/{a}", Cancel)).CanMerge);
        using var allowed = await curator.PostAsJsonAsync("/api/books/merge", new BookMergeInput(a, [b]), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
    }

    [TestMethod]
    public async Task TheDuplicatesPageListsGroupsByKeyAndNotDuplicatesLeaveIt()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        foreach (var (name, copies) in new[] { ("Alpha", 3), ("Beta", 2), ("Gamma", 2) })
            for (var i = 0; i < copies; i++)
                shelf.Write($"{name}-{i}.fb2", Fb2(name, "Ann Writer", text: $"{name}{i}"));
        shelf.Write("Lone.fb2", Fb2("Lone", "Ann Writer"));
        await shelf.ScanAsync(Cancel);

        var first = await shelf.Admin.GetAsync<DuplicatePage>($"/api/duplicates?library={shelf.LibraryId}&limit=2", Cancel);
        Assert.IsTrue(first.Built);
        Assert.AreEqual(2, first.Items.Count);
        Assert.AreEqual(3, first.Items[0].Books.Count, "the biggest first");
        Assert.AreEqual(DuplicateReason.Work, first.Items[0].Level);
        Assert.IsNotNull(first.Next);
        var second = await shelf.Admin.GetAsync<DuplicatePage>($"/api/duplicates?library={shelf.LibraryId}&limit=2&after={first.Next}", Cancel);
        Assert.AreEqual(1, second.Items.Count);
        Assert.IsNull(second.Next);
        CollectionAssert.AreEquivalent(new[] { "Alpha", "Beta", "Gamma" }, first.Items.Concat(second.Items).Select(g => g.Books[0].Title).ToList());
        Assert.AreEqual(0, (await shelf.Admin.GetAsync<DuplicatePage>($"/api/duplicates?library={shelf.LibraryId}&level=file", Cancel)).Items.Count);

        var beta = first.Items.Concat(second.Items).Single(g => g.Books[0].Title == "Beta").Books.Select(b => b.Id).ToList();
        using (var marked = await shelf.Admin.PostAsJsonAsync("/api/duplicates/not", new DuplicateEndpoints.NotDuplicatesInput(beta), Cancel))
            Assert.AreEqual(HttpStatusCode.NoContent, marked.StatusCode);
        var after = await shelf.Admin.GetAsync<DuplicatePage>($"/api/duplicates?library={shelf.LibraryId}", Cancel);
        CollectionAssert.AreEquivalent(new[] { "Alpha", "Gamma" }, after.Items.Select(g => g.Books[0].Title).ToList());
        Assert.AreEqual(0, (await shelf.Admin.GetAsync<List<BookDuplicate>>($"/api/books/{beta[0]}/duplicates", Cancel)).Count);
    }

    // ---- Helpers ----

    private async Task<int> BookOfAsync(TestLibrary shelf, string path)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.BookFiles.Where(f => f.Path == path).Select(f => f.BookId).SingleAsync(Cancel);
    }

    private async Task<int> LibraryCountAsync(TestLibrary shelf)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Libraries.Where(l => l.Id == shelf.LibraryId).Select(l => l.BookCount).SingleAsync(Cancel);
    }

    /// <summary>A book in a second library of the tenant.</summary>
    private async Task<int> OtherLibraryBookAsync(TestLibrary shelf)
    {
        using var created = await shelf.Admin.PostAsJsonAsync("/api/libraries", new LibraryInput("Second"), ApiClients.Json, Cancel);
        var library = (await created.Content.ReadFromJsonAsync<LibraryInfo>(ApiClients.Json, Cancel))!;
        var root = Path.Combine(Path.GetDirectoryName(shelf.Root)!, "second");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "c.fb2"), Fb2("C", "Ann Writer"));
        await using (var db = await shelf.DbAsync(Cancel))
        {
            db.LibraryRoots.Add(new LibraryRoot { TenantId = shelf.TenantId, LibraryId = library.Id, Path = root, Created = DateTime.UtcNow });
            await db.SaveChangesAsync(Cancel);
        }

        await shelf.Factory.Services.GetRequiredService<LibraryScanner>().ScanAsync(shelf.TenantId, library.Id, null, Cancel);
        await using var read = await shelf.DbAsync(Cancel);
        return await read.Books.Where(b => b.LibraryId == library.Id).Select(b => b.Id).SingleAsync(Cancel);
    }
}
