using System.Net;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Librarion.Core.Tasks;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>
/// Default tags of a library and its folders over HTTP, on both providers (stage 6 chunk 8c; SFE's folder default tags,
/// agents/docs/features/default-tags.md): given as rule links to the books that come in under them (sub-folders and
/// ZIPs too) and to a moved book, to the books already there when asked (inline and as a run), taken off them when
/// asked; a tag taken off a book stays off; a folder holding one is kept; merges carry them; the rights.
/// </summary>
[TestClass]
public sealed class DefaultTagTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info><author><first-name>Ann</first-name><last-name>Writer</last-name></author><book-title>{title}</book-title></title-info></description><body><p>{title}</p></body></FictionBook>
        """;

    [TestMethod]
    public async Task BooksThatComeInGetTheLibrarysAndTheirFoldersDefaults()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("Fantasy/Old.fb2", Fb2("Old"));
        await shelf.ScanAsync(Cancel);
        var fantasy = await FolderAsync(shelf, "Fantasy");

        Assert.AreEqual(0, (await AddAsync(shelf, null, "Everything")).Links, "new books only: nothing changes now");
        await AddAsync(shelf, fantasy, "Magic");
        shelf.Write("Fantasy/Deep/New.fb2", Fb2("New"));
        shelf.Write("Other/Plain.fb2", Fb2("Plain"));
        shelf.Zip("Fantasy/pack.zip", System.IO.Compression.CompressionLevel.Optimal, ("Zipped.fb2", Fb2("Zipped")));
        await shelf.ScanAsync(Cancel);

        CollectionAssert.AreEquivalent(new[] { "Everything", "Magic" }, await RuleTagsAsync(shelf, "New"), "the library's and the folder's, in a sub-folder too");
        CollectionAssert.AreEquivalent(new[] { "Everything", "Magic" }, await RuleTagsAsync(shelf, "Zipped"), "a ZIP is a folder under it");
        CollectionAssert.AreEquivalent(new[] { "Everything" }, await RuleTagsAsync(shelf, "Plain"));
        Assert.IsEmpty(await RuleTagsAsync(shelf, "Old"), "a book already there gets nothing");
        Assert.AreEqual(2, await BooksWithAsync(shelf, "Magic"), "counted");

        var list = await shelf.Admin.GetAsync<DefaultTagList>($"/api/libraries/{shelf.LibraryId}/default-tags?folder={await FolderAsync(shelf, "Fantasy/Deep")}", Cancel);
        Assert.IsEmpty(list.Own);
        CollectionAssert.AreEqual(new[] { ("Magic", "Fantasy"), ("Everything", (string?)null) }, list.Inherited.Select(t => (t.Name, t.From)).ToList(), "nearest first");
        var folders = await shelf.Admin.GetAsync<Page<FolderItem>>($"/api/folders/{await FolderAsync(shelf, "")}/folders", Cancel);
        Assert.AreEqual(1, folders.Items.Single(f => f.Name == "Fantasy").DefaultTags, "the badge's count");
    }

    [TestMethod]
    public async Task TheBooksThereGetItWhenAskedAndLoseOnlyItsRuleLinks()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("Shelf/One.fb2", Fb2("One"));
        shelf.Write("Shelf/Sub/Two.fb2", Fb2("Two"));
        shelf.Write("Elsewhere/Three.fb2", Fb2("Three"));
        await shelf.ScanAsync(Cancel);
        var folder = await FolderAsync(shelf, "Shelf");

        // One has Read by hand, Two had it taken off: neither gets a rule link.
        var read = (await shelf.Admin.PostAsJsonAsync("/api/books/tags", new BookEndpoints.SelectionInput([await BookIdAsync(shelf, "One"), await BookIdAsync(shelf, "Two")],
            Add: [new BookTagEdit(Name: "Read")]), Cancel));
        read.EnsureSuccessStatusCode();
        (await shelf.Admin.PostAsJsonAsync("/api/books/tags", new BookEndpoints.SelectionInput([await BookIdAsync(shelf, "Two")], Remove: [await TagIdAsync(shelf, "Read")]), Cancel))
            .EnsureSuccessStatusCode();
        Assert.AreEqual(0, (await AddAsync(shelf, folder, "Read", existing: true)).Links);
        Assert.AreEqual(1, await BooksWithAsync(shelf, "Read"));

        Assert.AreEqual(2, (await AddAsync(shelf, folder, "Shelved", existing: true)).Links, "the folder's books and those under it");
        CollectionAssert.AreEquivalent(new[] { "One", "Two" }, await TitlesWithAsync(shelf, "Shelved"));

        // Taken away: with the books, only the rule links go (One keeps Read, given by hand).
        Assert.AreEqual(2, (await RemoveAsync(shelf, folder, await TagIdAsync(shelf, "Shelved"), books: true)).Links);
        Assert.AreEqual(0, await BooksWithAsync(shelf, "Shelved"));
        await RemoveAsync(shelf, folder, await TagIdAsync(shelf, "Read"), books: true);
        CollectionAssert.AreEquivalent(new[] { "One" }, await TitlesWithAsync(shelf, "Read"));

        // The library gives it too: taking it off the folder leaves the books alone.
        await AddAsync(shelf, null, "Kept", existing: true);
        await AddAsync(shelf, folder, "Kept");
        Assert.AreEqual(0, (await RemoveAsync(shelf, folder, await TagIdAsync(shelf, "Kept"), books: true)).Links);
        Assert.AreEqual(3, await BooksWithAsync(shelf, "Kept"));

        // Past the threshold: a run of the library.
        shelf.Factory.Services.GetRequiredService<TagService>().Threshold = 0;
        using var started = await shelf.Admin.PostAsJsonAsync($"/api/libraries/{shelf.LibraryId}/default-tags",
            new FolderEndpoints.DefaultTagInput(null, new BookTagEdit(Name: "Everyone"), Existing: true), Cancel);
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode, await started.Content.ReadAsStringAsync(Cancel));
        var run = await WaitAsync(shelf.Admin, (await started.Content.ReadFromJsonAsync<TagWork>(ApiClients.Json, Cancel))!.RunId!.Value);
        Assert.AreEqual((TaskOutcome.Succeeded, BackgroundTaskKind.ScopeTags, 3), (run.Outcome, run.Task, run.Counts!.Changed), run.Summary);
        Assert.AreEqual(3, await BooksWithAsync(shelf, "Everyone"));
    }

    [TestMethod]
    public async Task AMovedBookGetsItsNewFoldersDefaultsAndAFolderWithDefaultsIsKept()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        var path = shelf.Write("Inbox/Book.fb2", Fb2("Book"));
        shelf.Write("Done/Placeholder.fb2", Fb2("Placeholder"));
        await shelf.ScanAsync(Cancel);
        await AddAsync(shelf, await FolderAsync(shelf, "Inbox"), "Unsorted");
        await AddAsync(shelf, await FolderAsync(shelf, "Done"), "Sorted");

        File.Move(path, Path.Combine(shelf.Root, "Done", "Book.fb2"));
        Assert.AreEqual(1, (await shelf.ScanAsync(Cancel)).Returned);
        CollectionAssert.AreEquivalent(new[] { "Sorted" }, await RuleTagsAsync(shelf, "Book"), "the new place's, not the old one's");
        await using var db = await shelf.DbAsync(Cancel);
        Assert.IsTrue(await db.Folders.AnyAsync(f => f.Path == "Inbox", Cancel), "empty, but it has a default tag");
    }

    [TestMethod]
    public async Task AMergeCarriesTheDefaultsAndItsUndoBringsThemBack()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("Shelf/One.fb2", Fb2("One"));
        await shelf.ScanAsync(Cancel);
        var folder = await FolderAsync(shelf, "Shelf");
        await AddAsync(shelf, folder, "Scifi");
        await AddAsync(shelf, null, "Science fiction");
        var (scifi, full) = (await TagIdAsync(shelf, "Scifi"), await TagIdAsync(shelf, "Science fiction"));

        using var merged = await shelf.Admin.PostAsJsonAsync($"/api/tags/{scifi}/merge", new TagEndpoints.MergeInput(full), Cancel);
        var work = (await merged.Content.ReadFromJsonAsync<TagWork>(ApiClients.Json, Cancel))!;
        await using (var db = await shelf.DbAsync(Cancel))
            Assert.AreEqual(full, (await db.DefaultTags.SingleAsync(d => d.FolderId == folder, Cancel)).TagId);

        (await shelf.Admin.PostAsync($"/api/merges/{work.LogId}/undo", null, Cancel)).EnsureSuccessStatusCode();
        await using (var db = await shelf.DbAsync(Cancel))
        {
            var back = await db.Tags.Where(t => t.Name == "Scifi").Select(t => t.Id).SingleAsync(Cancel);
            Assert.AreEqual(back, (await db.DefaultTags.SingleAsync(d => d.FolderId == folder, Cancel)).TagId);
        }
    }

    [TestMethod]
    public async Task OnlyDefaultTagsChangesThem()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("Shelf/One.fb2", Fb2("One"));
        await shelf.ScanAsync(Cancel);
        var folder = await FolderAsync(shelf, "Shelf");
        await AddAsync(shelf, folder, "Given");
        using (var created = await shelf.Admin.PostAsJsonAsync("/api/groups", new GroupInput("Viewers", AllLibraries: true, Rights: [Rights.Browse, Rights.Folders]), ApiClients.Json, Cancel))
        {
            var group = (await created.Content.ReadFromJsonAsync<GroupInfo>(ApiClients.Json, Cancel))!;
            await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("viewer", Password: ApiClients.Password, GroupIds: [group.Id]), Cancel);
        }

        using var viewer = await ApiClients.SignInAsync(shelf.Factory, "viewer", ApiClients.Password, Cancel);
        var list = await viewer.GetAsync<DefaultTagList>($"/api/libraries/{shelf.LibraryId}/default-tags?folder={folder}", Cancel);
        Assert.AreEqual("Given", list.Own.Single().Name, "seen with the folder view");
        using var add = await viewer.PostAsJsonAsync($"/api/libraries/{shelf.LibraryId}/default-tags",
            new FolderEndpoints.DefaultTagInput(folder, new BookTagEdit(Name: "Nope")), ApiClients.Json, Cancel);
        Assert.IsTrue(add.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, add.StatusCode.ToString());
        using var remove = await viewer.DeleteAsync($"/api/libraries/{shelf.LibraryId}/default-tags/{await TagIdAsync(shelf, "Given")}?folder={folder}", Cancel);
        Assert.IsTrue(remove.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, remove.StatusCode.ToString());
        using var elsewhere = await shelf.Admin.GetAsync($"/api/libraries/{shelf.LibraryId}/default-tags?folder=999999", Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, elsewhere.StatusCode);
    }

    private async Task<TagWork> AddAsync(Shelf shelf, int? folder, string name, bool existing = false)
    {
        using var response = await shelf.Admin.PostAsJsonAsync($"/api/libraries/{shelf.LibraryId}/default-tags",
            new FolderEndpoints.DefaultTagInput(folder, new BookTagEdit(Name: name), existing), ApiClients.Json, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
        return (await response.Content.ReadFromJsonAsync<TagWork>(ApiClients.Json, Cancel))!;
    }

    private async Task<TagWork> RemoveAsync(Shelf shelf, int? folder, int tagId, bool books)
    {
        using var response = await shelf.Admin.DeleteAsync($"/api/libraries/{shelf.LibraryId}/default-tags/{tagId}?folder={folder}&books={books}", Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
        return (await response.Content.ReadFromJsonAsync<TagWork>(ApiClients.Json, Cancel))!;
    }

    private async Task<int> FolderAsync(Shelf shelf, string path)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Folders.Where(f => f.Path == path).Select(f => f.Id).SingleAsync(Cancel);
    }

    private async Task<int> BookIdAsync(Shelf shelf, string title)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Books.Where(b => b.Title == title).Select(b => b.Id).SingleAsync(Cancel);
    }

    private async Task<int> TagIdAsync(Shelf shelf, string name)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Tags.Where(t => t.Name == name && t.FieldId == null).Select(t => t.Id).SingleAsync(Cancel);
    }

    private async Task<List<string>> RuleTagsAsync(Shelf shelf, string title)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.BookTags.Where(l => l.Book!.Title == title && l.Source == TagSource.Rule).Select(l => l.Tag!.Name).ToListAsync(Cancel);
    }

    private async Task<List<string>> TitlesWithAsync(Shelf shelf, string tag)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.BookTags.Where(l => l.Tag!.Name == tag && l.Tag.FieldId == null).Select(l => l.Book!.Title!).Distinct().ToListAsync(Cancel);
    }

    /// <summary>The tag's stored count in the library (what lists show), against the links themselves.</summary>
    private async Task<int> BooksWithAsync(Shelf shelf, string tag)
    {
        await using var db = await shelf.DbAsync(Cancel);
        var id = await db.Tags.Where(t => t.Name == tag && t.FieldId == null).Select(t => t.Id).SingleAsync(Cancel);
        var linked = await db.BookTags.Where(l => l.TagId == id).Select(l => l.BookId).Distinct().CountAsync(Cancel);
        var counted = await db.TagCounts.Where(c => c.TagId == id && c.LibraryId == shelf.LibraryId).Select(c => c.BookCount).SingleOrDefaultAsync(Cancel);
        Assert.AreEqual(linked, counted, $"{tag}: stored count against the links");
        return linked;
    }

    private async Task<TaskRunInfo> WaitAsync(HttpClient client, long run)
    {
        for (var i = 0; i < 300; i++)
        {
            var info = await client.GetAsync<TaskRunInfo>($"/api/tasks/{run}", Cancel);
            if (info.Outcome != TaskOutcome.Running)
                return info;
            await Task.Delay(50, Cancel);
        }

        Assert.Fail($"run {run} didn't end");
        return null!;
    }
}
