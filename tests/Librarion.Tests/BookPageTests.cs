using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>
/// What the book page reads (agents/docs/features/book-page.md): a book's files with their archive and whether the
/// caller may download them (<c>GET api/books/{id}</c>), and its possible duplicates (<c>GET api/books/{id}/duplicates</c>):
/// by the same file, identifier or work, a key shared by too many books left out as noise, only books the caller sees.
/// </summary>
[TestClass]
public sealed class BookPageTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string? author = null, string? isbn = null, string text = "", string? id = null) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info>{(author is null ? "" : $"<author><first-name>{author.Split(' ')[0]}</first-name><last-name>{author.Split(' ')[^1]}</last-name></author>")}<book-title>{title}</book-title></title-info>{(id is null ? "" : $"<document-info><id>{id}</id></document-info>")}{(isbn is null ? "" : $"<publish-info><isbn>{isbn}</isbn></publish-info>")}</description><body><p>{text}</p></body></FictionBook>
        """;

    [TestMethod]
    public async Task ABookListsItsFilesWithTheirArchiveAndSaysWhetherTheCallerMayDownload()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("Dune.fb2", Fb2("Dune", "Frank Herbert"));
        shelf.Zip("lib/fb2-000.zip", CompressionLevel.Optimal, ("sub/Children of Dune.fb2", Fb2("Children of Dune", "Frank Herbert")));
        await shelf.ScanAsync(Cancel);
        var loose = await BookOfAsync(shelf, "Dune.fb2");
        var entry = await BookOfAsync(shelf, "sub/Children of Dune.fb2");

        var book = await shelf.Admin.GetAsync<BookDetail>($"/api/books/{loose}", Cancel);
        Assert.IsTrue(book!.CanDownload);
        var file = book.Files.Single();
        Assert.AreEqual("Dune.fb2", file.Name);
        Assert.IsNull(file.Archive);
        Assert.IsFalse(file.InArchive);
        Assert.IsFalse(file.Missing);
        var zipped = (await shelf.Admin.GetAsync<BookDetail>($"/api/books/{entry}", Cancel))!.Files.Single();
        Assert.AreEqual("Children of Dune.fb2", zipped.Name, "the entry's own name");
        Assert.AreEqual("fb2-000.zip", zipped.Archive, "the archive's name, not its path");

        var browse = await Created<GroupInfo>(await shelf.Admin.PostAsJsonAsync("/api/groups",
            new GroupInput("Browsers", AllLibraries: true, Rights: [Rights.Browse]), ApiClients.Json, Cancel));
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("browser", Password: ApiClients.Password, GroupIds: [browse.Id]), Cancel);
        using var browser = await ApiClients.SignInAsync(shelf.Factory, "browser", ApiClients.Password, Cancel);
        var seen = await browser.GetAsync<BookDetail>($"/api/books/{loose}", Cancel);
        Assert.IsFalse(seen!.CanDownload, "may browse, not download");
        Assert.AreEqual("Dune.fb2", seen.Files.Single().Name, "the files are listed all the same");
    }

    [TestMethod]
    public async Task PossibleDuplicatesShareAFileAnIdentifierOrTheWorkStrongestFirst()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("Dune.fb2", Fb2("Dune", "Frank Herbert", isbn: "978-0-441-17271-9", text: "first"));
        shelf.Write("dune (other scan).fb2", Fb2("DUNE", "Frank Herbert", text: "second"));
        shelf.Write("Dune Messiah.fb2", Fb2("Dune Messiah", "Frank Herbert", isbn: "9780441172719", text: "wrong ISBN"));
        shelf.Write("Other.fb2", Fb2("Solaris", "Stanislaw Lem"));
        var zip = shelf.Zip("lib/a.zip", CompressionLevel.Optimal, ("Dune.fb2", Fb2("Dune", "Frank Herbert", text: "zipped")));
        await shelf.ScanAsync(Cancel);

        // The same ZIP in another library: the same entry there (copies within one library are merged at import).
        var second = await Created<LibraryInfo>(await shelf.Admin.PostAsJsonAsync("/api/libraries", new LibraryInput("Second"), ApiClients.Json, Cancel));
        var otherRoot = Path.Combine(Path.GetDirectoryName(shelf.Root)!, "second");
        Directory.CreateDirectory(otherRoot);
        File.Copy(zip, Path.Combine(otherRoot, "a.zip"));
        await using (var db = await shelf.DbAsync(Cancel))
        {
            db.LibraryRoots.Add(new LibraryRoot { TenantId = shelf.TenantId, LibraryId = second.Id, Path = otherRoot, Created = DateTime.UtcNow });
            await db.SaveChangesAsync(Cancel);
        }
        await shelf.Factory.Services.GetRequiredService<LibraryScanner>().ScanAsync(shelf.TenantId, second.Id, null, Cancel);

        var dune = await BookOfAsync(shelf, "Dune.fb2", shelf.LibraryId, inArchive: false);
        var zipped = await BookOfAsync(shelf, "Dune.fb2", shelf.LibraryId, inArchive: true);
        var copy = await BookOfAsync(shelf, "Dune.fb2", second.Id, inArchive: true);
        var duplicates = await shelf.Admin.GetAsync<List<BookDuplicate>>($"/api/books/{zipped}/duplicates", Cancel);
        Assert.AreEqual(copy, duplicates![0].Book.Id);
        Assert.AreEqual(DuplicateReason.File, duplicates[0].Reason, "the same file wins over the same work");
        CollectionAssert.AreEquivalent(new[] { copy, dune, await BookOfAsync(shelf, "dune (other scan).fb2") }, duplicates.Select(d => d.Book.Id).ToList());

        var byIsbn = (await shelf.Admin.GetAsync<List<BookDuplicate>>($"/api/books/{dune}/duplicates", Cancel))!;
        var messiah = byIsbn.Single(d => d.Book.Title == "Dune Messiah");
        Assert.AreEqual(DuplicateReason.Identifier, messiah.Reason);
        Assert.AreEqual("isbn", messiah.Scheme);
        Assert.AreEqual(DuplicateReason.Identifier, byIsbn[0].Reason, "strongest reason first");
        Assert.IsTrue(byIsbn.Where(d => d.Book.Title != "Dune Messiah").All(d => d.Reason == DuplicateReason.Work), "same title, case aside, and author");
        Assert.IsFalse(byIsbn.Any(d => d.Book.Title == "Solaris"));
        CollectionAssert.AreEqual(new[] { "Frank Herbert" }, byIsbn[0].Book.Authors.ToList());

        // Only the libraries the caller browses: the copy in the second library is not someone else's suggestion.
        var first = await Created<GroupInfo>(await shelf.Admin.PostAsJsonAsync("/api/groups",
            new GroupInput("First only", Rights: [Rights.Browse], LibraryIds: [shelf.LibraryId]), ApiClients.Json, Cancel));
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [first.Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(shelf.Factory, "reader", ApiClients.Password, Cancel);
        var seen = await reader.GetAsync<List<BookDuplicate>>($"/api/books/{zipped}/duplicates", Cancel);
        Assert.IsFalse(seen!.Any(d => d.Book.Id == copy));
        Assert.IsTrue(seen.Any(d => d.Book.Id == dune));
        using var hidden = await reader.GetAsync($"/api/books/{copy}/duplicates", Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode, "a book the caller can't see");
    }

    [TestMethod]
    public async Task AnFb2IdCountsOnlyWithTheSameTitleOrAuthor()
    {
        // FB2 ids are copied from template to template: a shared one alone is not the same book.
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("Dune.fb2", Fb2("Dune", "Frank Herbert", id: "copied-id"));
        shelf.Write("Dune Messiah.fb2", Fb2("Dune Messiah", "Frank Herbert", id: "copied-id"));
        shelf.Write("Dune by another.fb2", Fb2("Dune", "Brian Herbert", id: "copied-id"));
        shelf.Write("Solaris.fb2", Fb2("Solaris", "Stanislaw Lem", id: "copied-id"));
        await shelf.ScanAsync(Cancel);

        var duplicates = (await shelf.Admin.GetAsync<List<BookDuplicate>>($"/api/books/{await BookOfAsync(shelf, "Dune.fb2")}/duplicates", Cancel))!;
        CollectionAssert.AreEquivalent(new[] { await BookOfAsync(shelf, "Dune Messiah.fb2"), await BookOfAsync(shelf, "Dune by another.fb2") },
            duplicates.Select(d => d.Book.Id).ToList(), "the same author, the same title; not Solaris");
        Assert.IsTrue(duplicates.All(d => d is { Reason: DuplicateReason.Identifier, Scheme: "fb2-id" }));
        var solaris = await shelf.Admin.GetAsync<List<BookDuplicate>>($"/api/books/{await BookOfAsync(shelf, "Solaris.fb2")}/duplicates", Cancel);
        Assert.IsEmpty(solaris!);
    }

    [TestMethod]
    public async Task AKeySharedByTooManyBooksIsNoise()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        for (var i = 0; i <= BookQueries.MaxGroup; i++)
            shelf.Write($"notes-{i}.fb2", Fb2("Notes", text: $"page {i}"));
        shelf.Write("diary-1.fb2", Fb2("Diary", text: "one"));
        shelf.Write("diary-2.fb2", Fb2("Diary", text: "two"));
        await shelf.ScanAsync(Cancel);

        var notes = await shelf.Admin.GetAsync<List<BookDuplicate>>($"/api/books/{await BookOfAsync(shelf, "notes-0.fb2")}/duplicates", Cancel);
        Assert.IsEmpty(notes!, $"{BookQueries.MaxGroup + 1} books called Notes by nobody are not one book");
        var diary = await shelf.Admin.GetAsync<List<BookDuplicate>>($"/api/books/{await BookOfAsync(shelf, "diary-1.fb2")}/duplicates", Cancel);
        Assert.AreEqual(await BookOfAsync(shelf, "diary-2.fb2"), diary!.Single().Book.Id);
    }

    private async Task<T> Created<T>(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
            return (await response.Content.ReadFromJsonAsync<T>(ApiClients.Json, Cancel))!;
        }
    }

    private async Task<int> BookOfAsync(Shelf shelf, string path, int? library = null, bool? inArchive = null)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.BookFiles.Where(f => f.Path == path && (library == null || f.LibraryId == library) && (inArchive == null || (f.ArchiveId != null) == inArchive))
            .Select(f => f.BookId).SingleAsync(Cancel);
    }
}
