using System.Net;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Tests;

/// <summary>
/// The folder view's routes (agents/docs/features/folders.md): a library's roots, a folder with the folders above it,
/// a folder's folders by key, a folder's books, Tag all over a folder; all with <c>library.folders</c> only.
/// </summary>
[TestClass]
public sealed class FolderRouteTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    [TestMethod]
    public async Task AFolderShowsItsPathItsFoldersByKeyAndItsBooks()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        library.Write("Authors/King/It.fb2", "it");
        library.Write("Authors/King/Carrie.fb2", "carrie");
        library.Write("Authors/Lem/Solaris.fb2", "solaris");
        library.Write("Authors/Austen/Emma.fb2", "emma");
        library.Zip("lib/a.zip", ("1.fb2", "one"));
        await library.ScanAsync(Cancel);
        var (admin, _) = await library.AdminAsync(Cancel);

        var roots = await admin.GetAsync<List<FolderItem>>($"/api/libraries/{library.LibraryId}/folders", Cancel);
        var root = roots.Single();
        Assert.AreEqual(("books", 5, 2), (root.Name, root.TotalFiles, root.Folders), "named after the root's folder on disk");

        var top = await admin.GetAsync<Page<FolderItem>>($"/api/folders/{root.Id}/folders", Cancel);
        CollectionAssert.AreEqual(new[] { "Authors", "lib" }, top.Items.Select(f => f.Name).ToList());
        var authors = top.Items[0];
        var first = await admin.GetAsync<Page<FolderItem>>($"/api/folders/{authors.Id}/folders?limit=2", Cancel);
        CollectionAssert.AreEqual(new[] { "Austen", "King" }, first.Items.Select(f => f.Name).ToList());
        var rest = await admin.GetAsync<Page<FolderItem>>($"/api/folders/{authors.Id}/folders?limit=2&after={first.Next}", Cancel);
        Assert.AreEqual("Lem", rest.Items.Single().Name);
        Assert.IsNull(rest.Next);

        var king = first.Items[1];
        Assert.AreEqual((2, 2, 0), (king.Files, king.TotalFiles, king.Folders));
        var detail = await admin.GetAsync<FolderDetail>($"/api/folders/{king.Id}", Cancel);
        CollectionAssert.AreEqual(new[] { "Authors" }, detail.Path.Select(c => c.Name).ToList(), "the library's only root is the library itself");
        var zip = (await admin.GetAsync<Page<FolderItem>>($"/api/folders/{top.Items[1].Id}/folders", Cancel)).Items.Single();
        Assert.IsTrue(zip.Zip);

        var books = await admin.GetAsync<Page<BookListItem>>($"/api/books?library={library.LibraryId}&folder={king.Id}", Cancel);
        CollectionAssert.AreEquivalent(new[] { "Carrie", "It" }, books.Items.Select(b => b.FileName).ToList());
        Assert.IsEmpty((await admin.GetAsync<Page<BookListItem>>($"/api/books?library={library.LibraryId}&folder={authors.Id}", Cancel)).Items,
            "only the books directly in it");
        Assert.AreEqual(2, (await admin.GetAsync<BookEndpoints.ScopeCount>($"/api/books/scope?library={library.LibraryId}&folder={king.Id}", Cancel)).Books);
        using (var noLibrary = await admin.GetAsync($"/api/books?folder={king.Id}", Cancel))
            Assert.AreEqual(HttpStatusCode.NotFound, noLibrary.StatusCode, "a folder's books are asked in its library");
    }

    [TestMethod]
    public async Task OnlyBrowseByFolderOpensTheFolders()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        library.Write("TestLibrary/book.fb2", "book");
        await library.ScanAsync(Cancel);
        var (admin, _) = await library.AdminAsync(Cancel);
        int folderId;
        await using (var db = await library.DbAsync(Cancel))
            folderId = await db.Folders.Where(f => f.Path == "TestLibrary").Select(f => f.Id).SingleAsync(Cancel);

        using (var created = await admin.PostAsJsonAsync("/api/groups", new GroupInput("Readers", AllLibraries: true, Rights: [Rights.Browse]), ApiClients.Json, Cancel))
        {
            Assert.IsTrue(created.IsSuccessStatusCode);
            var group = (await created.Content.ReadFromJsonAsync<GroupInfo>(ApiClients.Json, Cancel))!;
            await ApiClients.CreateUserAsync(admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [group.Id]), Cancel);
        }

        using var reader = await ApiClients.SignInAsync(library.Factory, "reader", ApiClients.Password, Cancel);
        foreach (var path in new[]
                 {
                     $"/api/libraries/{library.LibraryId}/folders", $"/api/folders/{folderId}", $"/api/folders/{folderId}/folders",
                     $"/api/books?library={library.LibraryId}&folder={folderId}",
                 })
        {
            using var response = await reader.GetAsync(path, Cancel);
            Assert.IsTrue(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, $"{path}: {response.StatusCode}");
        }

        Assert.AreEqual(1, (await reader.GetAsync<Page<BookListItem>>($"/api/books?library={library.LibraryId}", Cancel)).Items.Count, "browsing as before");
    }
}
