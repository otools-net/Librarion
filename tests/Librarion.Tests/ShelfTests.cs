using System.Net;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Merging;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>Personal shelves (agents/docs/features/shelves.md): an account's own lists of books, in their own order, under visibility; merges keep them right.</summary>
[TestClass]
public sealed class ShelfTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string author = "Ann Writer", string text = "x") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0"><description><title-info>
        <author><first-name>{author.Split(' ')[0]}</first-name><last-name>{author.Split(' ')[1]}</last-name></author><book-title>{title}</book-title>
        </title-info></description><body><section><p>{text}</p></section></body></FictionBook>
        """;

    private async Task<TestLibrary> LibraryAsync(params string[] titles)
    {
        var library = await TestLibrary.CreateAsync(Cancel);
        foreach (var title in titles)
            library.Write($"{title}.fb2", Fb2(title, text: title));
        await library.ScanAsync(Cancel);
        return library;
    }

    private async Task<Dictionary<string, int>> BooksAsync(TestLibrary library)
    {
        await using var db = await library.DbAsync(Cancel);
        return await db.Books.Where(b => b.MergedIntoId == null).ToDictionaryAsync(b => b.Title!, b => b.Id, Cancel);
    }

    private async Task<ShelfInfo> CreateAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/shelves", new ShelfEndpoints.ShelfInput(name), ApiClients.Json, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
        return (await response.Content.ReadFromJsonAsync<ShelfInfo>(ApiClients.Json, Cancel))!;
    }

    private async Task<ShelfAdded> AddAsync(HttpClient client, int shelf, params int[] books)
    {
        using var response = await client.PostAsJsonAsync($"/api/shelves/{shelf}/books", new ShelfEndpoints.ShelfBooksInput(books), ApiClients.Json, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
        return (await response.Content.ReadFromJsonAsync<ShelfAdded>(ApiClients.Json, Cancel))!;
    }

    /// <summary>Every book of the shelf in an order, page after page (two a page: the keys are walked).</summary>
    private async Task<List<string>> TitlesAsync(HttpClient client, int shelf, string sort = "shelf")
    {
        var titles = new List<string>();
        string? after = null;
        do
        {
            var page = await client.GetAsync<Page<BookListItem>>($"/api/shelves/{shelf}/books?sort={sort}&limit=2" + (after is null ? "" : "&after=" + after), Cancel);
            titles.AddRange(page.Items.Select(b => b.Title!));
            after = page.Next;
        } while (after is not null);
        return titles;
    }

    private async Task MoveAsync(HttpClient client, int shelf, int book, int? before)
    {
        using var response = await client.PutAsJsonAsync($"/api/shelves/{shelf}/books/{book}/position", new ShelfEndpoints.ShelfMoveInput(before), ApiClients.Json, Cancel);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
    }

    /// <summary>How many shelves an account may make is the tenant's setting (else the server's, 100 by default).</summary>
    [TestMethod]
    public async Task TheSettingCapsTheShelvesOfAnAccount()
    {
        await using var library = await LibraryAsync("Dune");
        var settings = library.Factory.Services.GetRequiredService<Librarion.Core.Settings.SettingsService>();
        async Task SetAsync(Librarion.Core.Settings.SettingOwner owner, int? scope, string? value) => Assert.AreEqual(AccessStatus.Ok, (await settings.SaveAsync(owner, scope,
            new Librarion.Core.Settings.SettingChanges(new() { [Librarion.Core.Settings.SettingCatalog.MaxShelves] = value is null ? null : System.Text.Json.JsonDocument.Parse(value).RootElement }), Cancel)).Status);
        var shelves = library.Factory.Services.GetRequiredService<Shelves>();
        Assert.AreEqual(100, await shelves.MaxShelvesAsync(library.TenantId, Cancel));

        await SetAsync(new(SettingLevel.Server, 0), null, "3");
        await SetAsync(new(SettingLevel.Tenant, library.TenantId), library.TenantId, "2");
        await CreateAsync(library.Admin, "One");
        await CreateAsync(library.Admin, "Two");
        using (var refused = await library.Admin.PostAsJsonAsync("/api/shelves", new ShelfEndpoints.ShelfInput("Three"), ApiClients.Json, Cancel))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
            StringAssert.Contains(await refused.Content.ReadAsStringAsync(Cancel), "shelf.tooMany");
        }

        await SetAsync(new(SettingLevel.Tenant, library.TenantId), library.TenantId, null);
        await CreateAsync(library.Admin, "Three");
        Assert.AreEqual(3, await shelves.MaxShelvesAsync(library.TenantId, Cancel), "the server's when the tenant sets none");
    }

    [TestMethod]
    public async Task ShelvesAreMadeNamedRenamedAndDeleted()
    {
        await using var library = await LibraryAsync("Dune");
        var admin = library.Admin;

        var reading = await CreateAsync(admin, " To read ");
        await CreateAsync(admin, "Favourites");
        Assert.AreEqual("To read", reading.Name);
        using (var taken = await admin.PostAsJsonAsync("/api/shelves", new ShelfEndpoints.ShelfInput("to READ"), ApiClients.Json, Cancel))
            StringAssert.Contains(await taken.Content.ReadAsStringAsync(Cancel), "shelf.nameTaken");
        using (var empty = await admin.PostAsJsonAsync("/api/shelves", new ShelfEndpoints.ShelfInput("  "), ApiClients.Json, Cancel))
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(Cancel), "shelf.nameInvalid");

        using (var renamed = await admin.PutAsJsonAsync($"/api/shelves/{reading.Id}", new ShelfEndpoints.ShelfInput("Next"), ApiClients.Json, Cancel))
            Assert.AreEqual(HttpStatusCode.OK, renamed.StatusCode);
        var shelves = await admin.GetAsync<List<ShelfInfo>>("/api/shelves", Cancel);
        CollectionAssert.AreEqual(new[] { "Favourites", "Next" }, shelves.Select(s => s.Name).ToArray(), "by name");

        await AddAsync(admin, reading.Id, (await BooksAsync(library))["Dune"]);
        using (var deleted = await admin.DeleteAsync($"/api/shelves/{reading.Id}", Cancel))
            Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.HasCount(1, await admin.GetAsync<List<ShelfInfo>>("/api/shelves", Cancel));
        Assert.HasCount(1, await BooksAsync(library), "the book stays");
        await using var db = await library.DbAsync(Cancel);
        Assert.AreEqual(0, await db.ShelfBooks.CountAsync(Cancel), "its rows went with it");
    }

    /// <summary>Books go on after the last, a selection in the order given, those there already where they are; moves write one row, renumbering when neighbours touch.</summary>
    [TestMethod]
    public async Task BooksKeepTheShelfsOwnOrder()
    {
        await using var library = await LibraryAsync("Alpha", "Bravo", "Charlie", "Delta", "Echo");
        var books = await BooksAsync(library);
        var admin = library.Admin;
        var shelf = await CreateAsync(admin, "Mine");

        var first = await AddAsync(admin, shelf.Id, books["Charlie"]);
        var more = await AddAsync(admin, shelf.Id, books["Alpha"], books["Charlie"], books["Echo"], books["Bravo"]);
        Assert.AreEqual((1, 0), (first.Added, first.Already));
        Assert.AreEqual((3, 1, 4), (more.Added, more.Already, more.Shelf.BookCount));
        CollectionAssert.AreEqual(new[] { "Charlie", "Alpha", "Echo", "Bravo" }, await TitlesAsync(admin, shelf.Id));

        await MoveAsync(admin, shelf.Id, books["Bravo"], books["Alpha"]);
        await MoveAsync(admin, shelf.Id, books["Charlie"], null);
        CollectionAssert.AreEqual(new[] { "Bravo", "Alpha", "Echo", "Charlie" }, await TitlesAsync(admin, shelf.Id));

        // Each move halves the room just after Bravo until two neighbours touch: the shelf is spread out again (Bravo,
        // never moved since, is then first at one gap), the order kept.
        await MoveAsync(admin, shelf.Id, books["Charlie"], books["Alpha"]);
        for (var i = 0; i < 12; i++)
        {
            await MoveAsync(admin, shelf.Id, books["Echo"], books["Charlie"]);
            await MoveAsync(admin, shelf.Id, books["Charlie"], books["Echo"]);
        }
        await MoveAsync(admin, shelf.Id, books["Echo"], books["Charlie"]);
        CollectionAssert.AreEqual(new[] { "Bravo", "Echo", "Charlie", "Alpha" }, await TitlesAsync(admin, shelf.Id));
        await using (var db = await library.DbAsync(Cancel))
            Assert.AreEqual(Shelves.Gap, await db.ShelfBooks.Where(s => s.BookId == books["Bravo"]).Select(s => s.Position).SingleAsync(Cancel), "renumbered");

        CollectionAssert.AreEqual(new[] { "Alpha", "Bravo", "Charlie", "Echo" }, await TitlesAsync(admin, shelf.Id, "title"));
        using (var removed = await admin.PostAsJsonAsync($"/api/shelves/{shelf.Id}/books/remove", new ShelfEndpoints.ShelfBooksInput([books["Echo"], books["Delta"]]), ApiClients.Json, Cancel))
            Assert.AreEqual(3, (await removed.Content.ReadFromJsonAsync<ShelfInfo>(ApiClients.Json, Cancel))!.BookCount);
        CollectionAssert.AreEqual(new[] { "Bravo", "Charlie", "Alpha" }, await TitlesAsync(admin, shelf.Id));
        CollectionAssert.AreEqual(new[] { shelf.Id }, await admin.GetAsync<List<int>>($"/api/books/{books["Alpha"]}/shelves", Cancel));
        Assert.IsEmpty(await admin.GetAsync<List<int>>($"/api/books/{books["Echo"]}/shelves", Cancel));
    }

    [TestMethod]
    public async Task AShelfSortsByAuthorAndDateAddedToo()
    {
        await using var library = await TestLibrary.CreateAsync(Cancel);
        library.Write("a.fb2", Fb2("One", "Zed Zulu"));
        library.Write("b.fb2", Fb2("Two", "Amy Able"));
        library.Write("c.fb2", Fb2("Three", "Max Mike"));
        await library.ScanAsync(Cancel);
        var books = await BooksAsync(library);
        var shelf = await CreateAsync(library.Admin, "Mine");
        await AddAsync(library.Admin, shelf.Id, books["One"]);
        await AddAsync(library.Admin, shelf.Id, books["Two"]);
        await AddAsync(library.Admin, shelf.Id, books["Three"]);

        CollectionAssert.AreEqual(new[] { "Two", "Three", "One" }, await TitlesAsync(library.Admin, shelf.Id, "author"));
        CollectionAssert.AreEqual(new[] { "Three", "Two", "One" }, await TitlesAsync(library.Admin, shelf.Id, "added"));
    }

    /// <summary>
    /// A shelf is its owner's only (another account's shelf is not found), and shows only the books its owner may see
    /// now: a book of a library the owner doesn't open is neither put on nor listed.
    /// </summary>
    [TestMethod]
    public async Task AShelfIsItsOwnersAndShowsWhatTheOwnerMaySee()
    {
        await using var library = await LibraryAsync("Dune", "Emma");
        var books = await BooksAsync(library);
        var accounts = library.Factory.Services.GetRequiredService<AccessService>();
        var comics = (await accounts.CreateLibraryAsync(library.TenantId, "Comics", Cancel)).Value!;
        int hidden;
        await using (var db = await library.DbAsync(Cancel))
        {
            var now = DateTime.UtcNow;
            var book = new Book { TenantId = library.TenantId, LibraryId = comics.Id, Title = "Asterix", SortTitle = "asterix", SearchText = "asterix", SortAuthor = "", Added = now, Updated = now };
            db.Books.Add(book);
            await db.SaveChangesAsync(Cancel);
            hidden = book.Id;
        }
        var group = (await accounts.CreateGroupAsync(library.TenantId, new GroupInput("Fiction", Rights: [Rights.Browse, Rights.Shelves], LibraryIds: [library.LibraryId]), Cancel)).Value!;
        await ApiClients.CreateUserAsync(library.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [group.Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(library.Factory, "reader", ApiClients.Password, Cancel);

        var own = await CreateAsync(reader, "Mine");
        var added = await AddAsync(reader, own.Id, books["Dune"], hidden);
        Assert.AreEqual(1, added.Added, "the book it can't see is skipped");
        using (var none = await reader.PostAsJsonAsync($"/api/shelves/{own.Id}/books", new ShelfEndpoints.ShelfBooksInput([hidden]), ApiClients.Json, Cancel))
            Assert.AreEqual(HttpStatusCode.NotFound, none.StatusCode);

        // The administrator's shelf holds both; the reader can't reach it at all.
        var theirs = await CreateAsync(library.Admin, "Admin's");
        await AddAsync(library.Admin, theirs.Id, books["Emma"], hidden);
        CollectionAssert.AreEquivalent(new[] { "Emma", "Asterix" }, await TitlesAsync(library.Admin, theirs.Id));
        foreach (var address in new[] { $"/api/shelves/{theirs.Id}", $"/api/shelves/{theirs.Id}/books" })
        {
            using var response = await reader.GetAsync(address, Cancel);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, address);
        }
        using (var foreign = await reader.PostAsJsonAsync($"/api/shelves/{theirs.Id}/books", new ShelfEndpoints.ShelfBooksInput([books["Dune"]]), ApiClients.Json, Cancel))
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.HasCount(1, await reader.GetAsync<List<ShelfInfo>>("/api/shelves", Cancel));

        // The library deleted: its books leave the shelves, their counts follow.
        Assert.IsTrue(await accounts.DeleteLibraryAsync(library.TenantId, comics.Id, Cancel));
        Assert.AreEqual(1, (await library.Admin.GetAsync<ShelfInfo>($"/api/shelves/{theirs.Id}", Cancel)).BookCount);
    }

    /// <summary>
    /// A merge puts the kept book in the merged one's places (or drops a place where the shelf had both); its undo puts
    /// each back; a split leaves the book on its shelves and the new book off them.
    /// </summary>
    [TestMethod]
    public async Task MergesUndoAndSplitsKeepShelvesRight()
    {
        await using var library = await LibraryAsync("Dune", "Dune copy", "Emma");
        var books = await BooksAsync(library);
        var (kept, merged) = (books["Dune"], books["Dune copy"]);
        var admin = library.Admin;
        var only = await CreateAsync(admin, "Only the copy");
        var both = await CreateAsync(admin, "Both");
        await AddAsync(admin, only.Id, books["Emma"], merged);
        await AddAsync(admin, both.Id, merged, books["Emma"], kept);

        using (var response = await admin.PostAsJsonAsync("/api/books/merge", new BookMergeInput(kept, [merged]), Cancel))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));

        CollectionAssert.AreEqual(new[] { "Emma", "Dune" }, await TitlesAsync(admin, only.Id), "the kept book in the copy's place");
        CollectionAssert.AreEqual(new[] { "Emma", "Dune" }, await TitlesAsync(admin, both.Id), "one place dropped");
        Assert.AreEqual((2, 2), ((await admin.GetAsync<ShelfInfo>($"/api/shelves/{only.Id}", Cancel)).BookCount, (await admin.GetAsync<ShelfInfo>($"/api/shelves/{both.Id}", Cancel)).BookCount));

        long log;
        await using (var db = await library.DbAsync(Cancel))
            log = await db.MergeLogs.Where(l => l.Kind == MergeKind.Book).Select(l => l.Id).SingleAsync(Cancel);
        var merger = library.Factory.Services.GetRequiredService<BookMerger>();
        Assert.AreEqual(AccessStatus.Ok, (await merger.UndoAsync(library.TenantId, log, Cancel)).Status);

        CollectionAssert.AreEqual(new[] { "Emma", "Dune copy" }, await TitlesAsync(admin, only.Id));
        CollectionAssert.AreEqual(new[] { "Dune copy", "Emma", "Dune" }, await TitlesAsync(admin, both.Id));
        Assert.AreEqual((2, 3), ((await admin.GetAsync<ShelfInfo>($"/api/shelves/{only.Id}", Cancel)).BookCount, (await admin.GetAsync<ShelfInfo>($"/api/shelves/{both.Id}", Cancel)).BookCount));

        // Merged again, then one file split out: the book stays on its shelves, the new book is on none.
        using (var response = await admin.PostAsJsonAsync("/api/books/merge", new BookMergeInput(kept, [merged]), Cancel))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        int movedFile;
        await using (var db = await library.DbAsync(Cancel))
            movedFile = await db.BookFiles.Where(f => f.BookId == kept).OrderByDescending(f => f.Id).Select(f => f.Id).FirstAsync(Cancel);
        var split = await library.Factory.Services.GetRequiredService<BookSplitter>().SplitAsync(library.TenantId, kept, [movedFile], Cancel);
        Assert.AreEqual(AccessStatus.Ok, split.Status);
        CollectionAssert.AreEqual(new[] { only.Id, both.Id }.Order().ToArray(), (await admin.GetAsync<List<int>>($"/api/books/{kept}/shelves", Cancel)).ToArray());
        Assert.IsEmpty(await admin.GetAsync<List<int>>($"/api/books/{split.Value}/shelves", Cancel));
    }

    [TestMethod]
    public async Task WithoutTheRightThereAreNoShelves()
    {
        await using var library = await LibraryAsync("Dune");
        var accounts = library.Factory.Services.GetRequiredService<AccessService>();
        await accounts.UpdateTenantAsync(library.TenantId, new TenantInput("Home", DisabledRights: [Rights.Shelves]), Cancel);
        await ApiClients.CreateUserAsync(library.Admin, new NewUser("reader", Password: ApiClients.Password,
            GroupIds: [(await library.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel)).Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(library.Factory, "reader", ApiClients.Password, Cancel);

        using var response = await reader.GetAsync("/api/shelves", Cancel);
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
