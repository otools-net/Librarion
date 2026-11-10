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
/// Plain tags on many books over HTTP, on both providers (agents/docs/features/tags.md, stage 6 chunk 7): a selection's
/// tags with how many have each, tagging a selection, a scope counted then tagged (inline and as a run of the library),
/// taken-off tags kept off, the rights.
/// </summary>
[TestClass]
public sealed class BookTaggingTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string author, string? keywords = null) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info><author><first-name>{author.Split(' ')[0]}</first-name><last-name>{author.Split(' ')[^1]}</last-name></author><book-title>{title}</book-title>{(keywords is null ? "" : $"<keywords>{keywords}</keywords>")}<lang>en</lang></title-info></description><body><p>{title}</p></body></FictionBook>
        """;

    [TestMethod]
    public async Task ASelectionShowsWhichTagsAllOrSomeHaveAndIsTagged()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", "Ann Writer", "Horror, Shared"));
        shelf.Write("two.fb2", Fb2("Two", "Ann Writer", "Shared"));
        shelf.Write("three.fb2", Fb2("Three", "Bob Other"));
        await shelf.ScanAsync(Cancel);
        var (one, two, three) = (await BookIdAsync(shelf, "One"), await BookIdAsync(shelf, "Two"), await BookIdAsync(shelf, "Three"));
        var (horror, shared) = (await TagIdAsync(shelf, "Horror"), await TagIdAsync(shelf, "Shared"));

        var state = await StateAsync(shelf, [one, two]);
        Assert.AreEqual(2, state.Books);
        CollectionAssert.AreEqual(new[] { "Horror:1", "Shared:2" }, state.Tags.Select(t => $"{t.Name}:{t.Books}").ToList(), "authors are no plain tags");

        // Horror to all three (one has it), Shared off the two (imported: kept off), a new tag by name.
        using var tagged = await shelf.Admin.PostAsJsonAsync("/api/books/tags",
            new BookEndpoints.SelectionInput([one, two, three], [new BookTagEdit(horror), new BookTagEdit(Name: "To read")], [shared]), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, tagged.StatusCode, await tagged.Content.ReadAsStringAsync(Cancel));
        Assert.AreEqual(2 + 3 + 2, (await tagged.Content.ReadFromJsonAsync<TagWork>(ApiClients.Json, Cancel))!.Links, "two Horror, three To read, two Shared off");

        state = await StateAsync(shelf, [one, two, three]);
        CollectionAssert.AreEqual(new[] { "Horror:3", "To read:3" }, state.Tags.Select(t => $"{t.Name}:{t.Books}").ToList());
        Assert.AreEqual((3, 3), await CountsAsync(shelf, horror));
        Assert.AreEqual((0, 0), await CountsAsync(shelf, shared));

        // Read again (files changed): Shared stays off, the given tags stay.
        shelf.Write("one.fb2", Fb2("One", "Ann Writer", "Horror, Shared, New"));
        File.SetLastWriteTimeUtc(Path.Combine(shelf.Root, "one.fb2"), DateTime.UtcNow.AddMinutes(1));
        await shelf.ScanAsync(Cancel);
        state = await StateAsync(shelf, [one, two, three]);
        CollectionAssert.AreEqual(new[] { "Horror:3", "New:1", "To read:3" }, state.Tags.Select(t => $"{t.Name}:{t.Books}").ToList());
    }

    [TestMethod]
    public async Task AScopePastTheCapIsCountedAsAtLeastAndTaggedInTheBackground()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("a.fb2", Fb2("Dark tower", "Ann Writer", "Old"));
        shelf.Write("b.fb2", Fb2("Dark night", "Ann Writer", "Old"));
        shelf.Write("c.fb2", Fb2("Dark day", "Bob Other", "Old"));
        await shelf.ScanAsync(Cancel);
        shelf.Factory.Services.GetRequiredService<BookTagger>().Cap = 2;

        // The library seen whole: its stored count, exact.
        var library = await shelf.Admin.GetAsync<BookEndpoints.ScopeCount>($"/api/books/scope?library={shelf.LibraryId}", Cancel);
        Assert.AreEqual((3L, false), (library.Books, library.Capped));

        // Otherwise at most the cap read (a tag's books with a limit, a search sampled from its narrowest term): "at least 2".
        foreach (var query in new[] { $"&tag={await TagIdAsync(shelf, "Old")}", "&q=dark" })
        {
            var count = await shelf.Admin.GetAsync<BookEndpoints.ScopeCount>($"/api/books/scope?library={shelf.LibraryId}{query}", Cancel);
            Assert.AreEqual(2, count.Books, query);
            Assert.IsTrue(count.Capped, query);
        }

        // Past the cap the change runs in the background, even with one tag on few books, and reaches every book.
        using var started = await shelf.Admin.PostAsJsonAsync("/api/books/scope/tags",
            new BookEndpoints.ScopeInput(shelf.LibraryId, Q: "dark", Add: [new BookTagEdit(Name: "Shelved")]), Cancel);
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode);
        var run = await WaitAsync(shelf.Admin, (await started.Content.ReadFromJsonAsync<TagWork>(ApiClients.Json, Cancel))!.RunId!.Value);
        Assert.AreEqual(TaskOutcome.Succeeded, run.Outcome);
        Assert.AreEqual((3, 3), await CountsAsync(shelf, await TagIdAsync(shelf, "Shelved")));
    }

    [TestMethod]
    public async Task AScopeIsCountedThenTaggedInlineOrInTheBackground()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("a.fb2", Fb2("Dark tower", "Ann Writer", "Old"));
        shelf.Write("b.fb2", Fb2("Dark night", "Ann Writer", "Old"));
        shelf.Write("c.fb2", Fb2("Light day", "Bob Other", "Old"));
        await shelf.ScanAsync(Cancel);
        var old = await TagIdAsync(shelf, "Old");
        var author = await TagIdAsync(shelf, "Ann Writer");

        Assert.AreEqual(2, (await shelf.Admin.GetAsync<BookEndpoints.ScopeCount>($"/api/books/scope?library={shelf.LibraryId}&q=dark", Cancel)).Books);
        Assert.AreEqual(2, (await shelf.Admin.GetAsync<BookEndpoints.ScopeCount>($"/api/books/scope?library={shelf.LibraryId}&tag={author}", Cancel)).Books);
        Assert.AreEqual(3, (await shelf.Admin.GetAsync<BookEndpoints.ScopeCount>($"/api/books/scope?library={shelf.LibraryId}", Cancel)).Books);
        Assert.IsFalse((await shelf.Admin.GetAsync<BookEndpoints.ScopeCount>($"/api/books/scope?library={shelf.LibraryId}", Cancel)).Capped);

        // A search's books, inline.
        using var inline = await shelf.Admin.PostAsJsonAsync("/api/books/scope/tags",
            new BookEndpoints.ScopeInput(shelf.LibraryId, Q: "dark", Add: [new BookTagEdit(Name: "Gloomy")]), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, inline.StatusCode, await inline.Content.ReadAsStringAsync(Cancel));
        Assert.AreEqual((2, 2), await CountsAsync(shelf, await TagIdAsync(shelf, "Gloomy")));

        // Every book with Old loses it, as a run of the library (the threshold lowered): the walk by id reaches every
        // book although the scope is the tag being taken off.
        shelf.Factory.Services.GetRequiredService<TagService>().Threshold = 0;
        using var started = await shelf.Admin.PostAsJsonAsync("/api/books/scope/tags", new BookEndpoints.ScopeInput(shelf.LibraryId, Tag: old, Remove: [old]), Cancel);
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode, await started.Content.ReadAsStringAsync(Cancel));
        var run = await WaitAsync(shelf.Admin, (await started.Content.ReadFromJsonAsync<TagWork>(ApiClients.Json, Cancel))!.RunId!.Value);
        Assert.AreEqual(TaskOutcome.Succeeded, run.Outcome, run.Summary);
        Assert.AreEqual(BackgroundTaskKind.ScopeTags, run.Task);
        Assert.AreEqual(shelf.LibraryId, run.LibraryId);
        Assert.AreEqual(3, run.Counts!.Changed);
        Assert.AreEqual((0, 0), await CountsAsync(shelf, old));
        await using var db = await shelf.DbAsync(Cancel);
        Assert.AreEqual(3, await db.BookTagSuppressions.CountAsync(s => s.TagId == old, Cancel));
    }

    [TestMethod]
    public async Task TaggingManyNeedsTheRightAndChecksItsInput()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", "Ann Writer"));
        await shelf.ScanAsync(Cancel);
        var one = await BookIdAsync(shelf, "One");
        var author = await TagIdAsync(shelf, "Ann Writer");
        var groups = await shelf.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("curator", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Curators).Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(shelf.Factory, "reader", ApiClients.Password, Cancel);
        using var curator = await ApiClients.SignInAsync(shelf.Factory, "curator", ApiClients.Password, Cancel);

        using var refused = await reader.PostAsJsonAsync("/api/books/tags", new BookEndpoints.SelectionInput([one], [new BookTagEdit(Name: "X")]), Cancel);
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        using var notCounted = await reader.GetAsync($"/api/books/scope?library={shelf.LibraryId}", Cancel);
        Assert.AreEqual(HttpStatusCode.Forbidden, notCounted.StatusCode);

        using var field = await curator.PostAsJsonAsync("/api/books/tags", new BookEndpoints.SelectionInput([one], [new BookTagEdit(author)]), Cancel);
        Assert.AreEqual(HttpStatusCode.BadRequest, field.StatusCode, "a field's value is no plain tag");
        StringAssert.Contains(await field.Content.ReadAsStringAsync(Cancel), "tag.unknown");
        using var many = await curator.PostAsJsonAsync("/api/books/tags", new BookEndpoints.SelectionInput(Enumerable.Range(1, 1001).ToList(), [new BookTagEdit(Name: "X")]), Cancel);
        Assert.AreEqual(HttpStatusCode.BadRequest, many.StatusCode);
        StringAssert.Contains(await many.Content.ReadAsStringAsync(Cancel), "books.tooMany");
        using var none = await curator.PostAsJsonAsync("/api/books/tags", new BookEndpoints.SelectionInput([99999], [new BookTagEdit(Name: "X")]), Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, none.StatusCode);
        using var done = await curator.PostAsJsonAsync("/api/books/tags", new BookEndpoints.SelectionInput([one], [new BookTagEdit(Name: "Mine")]), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, done.StatusCode);

        // A curator follows the library's runs (tagging a scope starts them) though it may not scan.
        using var runs = await curator.GetAsync("/api/tasks/running", Cancel);
        Assert.AreEqual(HttpStatusCode.OK, runs.StatusCode);
    }

    // ---- Helpers ----

    private async Task<SelectionTags> StateAsync(TestLibrary shelf, int[] books)
    {
        using var response = await shelf.Admin.PostAsJsonAsync("/api/books/tags/state", new BookEndpoints.SelectionInput(books), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
        return (await response.Content.ReadFromJsonAsync<SelectionTags>(ApiClients.Json, Cancel))!;
    }

    private async Task<int> BookIdAsync(TestLibrary shelf, string title)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Books.Where(b => b.Title == title).Select(b => b.Id).SingleAsync(Cancel);
    }

    private async Task<int> TagIdAsync(TestLibrary shelf, string name)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Tags.Where(t => t.Name == name).Select(t => t.Id).SingleAsync(Cancel);
    }

    private async Task<(int, int)> CountsAsync(TestLibrary shelf, int tagId)
    {
        await using var db = await shelf.DbAsync(Cancel);
        var row = await db.TagCounts.SingleOrDefaultAsync(c => c.TagId == tagId && c.LibraryId == shelf.LibraryId, Cancel);
        return row is null ? (0, 0) : (row.BookCount, row.TotalCount);
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
