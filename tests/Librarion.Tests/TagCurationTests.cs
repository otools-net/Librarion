using System.Net;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Merging;
using Librarion.Core.Tagging;
using Librarion.Core.Tasks;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>
/// The tag service and merges over HTTP, on both providers (agents/docs/features/tags.md, merging.md, stage 6 chunks
/// 2-3): definitions, parents and the closure, deletes, tag and field merges with their undo, background runs.
/// </summary>
[TestClass]
public sealed class TagCurationTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string[] authors, string? keywords = null) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info>{string.Concat(authors.Select(a => $"<author><first-name>{a.Split(' ')[0]}</first-name><last-name>{a.Split(' ')[^1]}</last-name></author>"))}<book-title>{title}</book-title>{(keywords is null ? "" : $"<keywords>{keywords}</keywords>")}<lang>en</lang></title-info></description><body><p>{title}</p></body></FictionBook>
        """;

    [TestMethod]
    public async Task ParentsKeepTheClosureAndRefuseCycles()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        var speculative = await CreateAsync(shelf, new TagInput("Speculative"));
        var fantasy = await CreateAsync(shelf, new TagInput("Fantasy fiction", ParentIds: [speculative.Id]));
        var epic = await CreateAsync(shelf, new TagInput("Epic", Color: "#A0b", ParentIds: [fantasy.Id], Aliases: ["Epic fantasy", "epic"]));
        var fiction = await CreateAsync(shelf, new TagInput("Fiction"));
        shelf.Write("saga.fb2", Fb2("A Long Saga", ["Ann Writer"], "Epic"));
        await shelf.ScanAsync(Cancel);

        Assert.AreEqual("#a0b", epic.Color);
        CollectionAssert.AreEqual(new[] { "Epic fantasy" }, epic.Aliases.ToList(), "an alias equal to the name is dropped");
        Assert.IsTrue(await UnderAsync(shelf, epic.Id, speculative.Id));
        Assert.AreEqual(1, await CountAsync(shelf, "tag:Speculative"), "the scan linked the existing tag, found through its parent");

        using var cycle = await shelf.Admin.PutAsJsonAsync($"/api/tags/{speculative.Id}", new TagInput("Speculative", ParentIds: [epic.Id]), Cancel);
        Assert.AreEqual(HttpStatusCode.BadRequest, cycle.StatusCode);
        StringAssert.Contains(await cycle.Content.ReadAsStringAsync(Cancel), "parent.cycle");

        using var moved = await shelf.Admin.PutAsJsonAsync($"/api/tags/{fantasy.Id}", new TagInput("Fantasy fiction", ParentIds: [fiction.Id]), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, moved.StatusCode, await moved.Content.ReadAsStringAsync(Cancel));
        Assert.IsTrue(await UnderAsync(shelf, epic.Id, fiction.Id), "the grandchild follows its parent");
        Assert.IsFalse(await UnderAsync(shelf, epic.Id, speculative.Id));
        Assert.IsFalse(await UnderAsync(shelf, fantasy.Id, speculative.Id));
        Assert.AreEqual(1, await CountAsync(shelf, "tag:Fiction"));
        Assert.AreEqual(0, await CountAsync(shelf, "tag:Speculative"));
    }

    [TestMethod]
    public async Task DefinitionsAreChecked()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", ["Ann Writer"]));
        await shelf.ScanAsync(Cancel);
        int authorField;
        await using (var db = await shelf.DbAsync(Cancel))
            authorField = await db.Tags.Where(t => t.FieldKey == "author").Select(t => t.Id).SingleAsync(Cancel);

        foreach (var (input, code) in new (TagInput, string)[]
        {
            (new TagInput(" "), "name.required"),
            (new TagInput("Red", Color: "red"), "color.invalid"),
            (new TagInput("x", FieldKey: "Bad Key"), "fieldKey.invalid"),
            (new TagInput("x", FieldKey: "author"), "fieldKey.taken"),
            (new TagInput("Ann Writer", FieldId: authorField), "name.taken"),
            (new TagInput("x", ParentIds: [authorField]), "parent.otherField"),
            (new TagInput("x", DisambiguationParentId: authorField), "disambiguation.notParent"),
        })
        {
            using var response = await shelf.Admin.PostAsJsonAsync("/api/tags", input, Cancel);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, code);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(Cancel), code);
        }

        var person = await CreateAsync(shelf, new TagInput("Ann Writer", FieldId: authorField, DisambiguationParentId: null, ParentIds: []) with { Name = "Bea Reader" });
        var field = await CreateAsync(shelf, new TagInput("Mood", FieldKey: "mood"));
        Assert.AreEqual("Reader, Bea", person.SortName, "a person sorts by the last name");
        Assert.AreEqual("mood", field.FieldKey);
        Assert.IsTrue(field.IsCategory);
    }

    [TestMethod]
    public async Task DeletingATagRemovesItsLinksCountsAndPlace()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", ["Ann Other"], "Doomed, Kept"));
        await shelf.ScanAsync(Cancel);
        var doomed = await TagIdAsync(shelf, "Doomed");
        var child = await CreateAsync(shelf, new TagInput("Doomed child", ParentIds: [doomed]));
        var author = await TagIdAsync(shelf, "Ann Other");
        var mood = await CreateAsync(shelf, new TagInput("Mood", FieldKey: "mood"));
        await CreateAsync(shelf, new TagInput("Calm", FieldId: mood.Id));
        int archived, kept;
        await using (var db = await shelf.DbAsync(Cancel))
        {
            archived = await db.Tags.Where(t => t.BuiltIn == BuiltInTag.Archived).Select(t => t.Id).SingleAsync(Cancel);
            kept = await db.Tags.Where(t => t.Name == "Kept").Select(t => t.Id).SingleAsync(Cancel);
            var users = await db.Groups.Where(g => g.BuiltIn == BuiltInGroup.Users).Select(g => g.Id).SingleAsync(Cancel);
            db.GroupTagFilters.Add(new GroupTagFilter { GroupId = users, TagId = kept, Deny = true });
            await db.SaveChangesAsync(Cancel);
        }

        var work = await WorkAsync(await shelf.Admin.DeleteAsync($"/api/tags/{doomed}", Cancel));
        await WorkAsync(await shelf.Admin.DeleteAsync($"/api/tags/{author}", Cancel));

        Assert.AreEqual(1, work.Links);
        Assert.IsNull(work.RunId);
        await using (var db = await shelf.DbAsync(Cancel))
        {
            Assert.IsFalse(await db.Tags.AnyAsync(t => t.Id == doomed, Cancel));
            Assert.IsFalse(await db.TagCounts.AnyAsync(c => c.TagId == doomed, Cancel));
            Assert.AreEqual(1, await db.TagClosure.CountAsync(c => c.DescendantId == child.Id, Cancel), "the child is on its own now");
            Assert.AreEqual("", await db.Books.Select(b => b.SortAuthor).SingleAsync(Cancel), "the book's only author is gone");
        }

        foreach (var (id, code) in new[] { (archived, "tag.builtIn"), (kept, "tag.inFilter"), (mood.Id, "field.notEmpty") })
        {
            using var refused = await shelf.Admin.DeleteAsync($"/api/tags/{id}", Cancel);
            Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode, code);
            StringAssert.Contains(await refused.Content.ReadAsStringAsync(Cancel), code);
        }
    }

    [TestMethod]
    public async Task MergingAuthorsMovesLinksAndAnUndoPutsThemBack()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", ["Steven King"]));
        shelf.Write("two.fb2", Fb2("Two", ["Stephen King"]));
        shelf.Write("three.fb2", Fb2("Three", ["Steven King", "Stephen King"]));
        await shelf.ScanAsync(Cancel);
        var steven = await TagIdAsync(shelf, "Steven King");
        var stephen = await TagIdAsync(shelf, "Stephen King");

        var merged = await WorkAsync(await shelf.Admin.PostAsJsonAsync($"/api/tags/{steven}/merge", new TagEndpoints.MergeInput(stephen), Cancel));

        Assert.AreEqual(2, merged.Links);
        Assert.AreEqual(3, await TagCountAsync(shelf, stephen));
        Assert.AreEqual(3, await CountAsync(shelf, "author:\"Stephen King\""));
        await using (var db = await shelf.DbAsync(Cancel))
        {
            Assert.IsFalse(await db.Tags.AnyAsync(t => t.Id == steven, Cancel));
            Assert.IsTrue(await db.TagAliases.AnyAsync(a => a.TagId == stephen && a.Name == "Steven King", Cancel));
            Assert.AreEqual("King, Stephen", await db.Books.Where(b => b.Title == "One").Select(b => b.SortAuthor).SingleAsync(Cancel));
        }

        // A new book naming the merged author comes in under the target, through the alias.
        shelf.Write("four.fb2", Fb2("Four", ["Steven King"]));
        await shelf.ScanAsync(Cancel);
        Assert.AreEqual(4, await TagCountAsync(shelf, stephen));

        var undone = await WorkAsync(await shelf.Admin.PostAsync($"/api/merges/{merged.LogId}/undo", null, Cancel));
        using var again = await shelf.Admin.PostAsync($"/api/merges/{merged.LogId}/undo", null, Cancel);

        Assert.AreEqual(2, undone.Links);
        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
        StringAssert.Contains(await again.Content.ReadAsStringAsync(Cancel), "merge.undone");
        Assert.AreEqual(2, await TagCountAsync(shelf, steven), "One and Three");
        Assert.AreEqual(3, await TagCountAsync(shelf, stephen), "Two, Three and Four");
        await using (var db = await shelf.DbAsync(Cancel))
        {
            Assert.IsTrue(await db.Tags.AnyAsync(t => t.Id == steven && t.Name == "Steven King", Cancel), "made again with its id");
            Assert.IsFalse(await db.TagAliases.AnyAsync(a => a.TagId == stephen, Cancel));
            Assert.AreEqual("King, Steven", await db.Books.Where(b => b.Title == "One").Select(b => b.SortAuthor).SingleAsync(Cancel));
            Assert.AreEqual(2, await db.BookTags.CountAsync(l => l.Book!.Title == "Three" && l.Tag!.Field!.FieldKey == "author", Cancel), "Three has both authors again");
            Assert.IsTrue(await db.TagClosure.AnyAsync(c => c.DescendantId == steven && c.AncestorId == db.Tags.Single(t => t.FieldKey == "author").Id, Cancel));
        }
    }

    [TestMethod]
    public async Task AnUndoWaitsForTheLaterMergesOfItsTarget()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", ["Ann Writer"], "Ex, Why, Zed"));
        await shelf.ScanAsync(Cancel);
        var (x, y, z) = (await TagIdAsync(shelf, "Ex"), await TagIdAsync(shelf, "Why"), await TagIdAsync(shelf, "Zed"));

        var first = await WorkAsync(await shelf.Admin.PostAsJsonAsync($"/api/tags/{x}/merge", new TagEndpoints.MergeInput(y), Cancel));
        var second = await WorkAsync(await shelf.Admin.PostAsJsonAsync($"/api/tags/{z}/merge", new TagEndpoints.MergeInput(y), Cancel));
        using var blocked = await shelf.Admin.PostAsync($"/api/merges/{first.LogId}/undo", null, Cancel);
        var merges = await shelf.Admin.GetAsync<List<MergeLogInfo>>("/api/merges", Cancel);

        Assert.AreEqual(HttpStatusCode.Conflict, blocked.StatusCode);
        StringAssert.Contains(await blocked.Content.ReadAsStringAsync(Cancel), "merge.later");
        CollectionAssert.AreEqual(new[] { second.LogId, first.LogId }, merges.Select(m => (long?)m.Id).ToList());
        Assert.IsTrue(merges.All(m => m.Completed && m.Kind == MergeKind.Tag));

        await WorkAsync(await shelf.Admin.PostAsync($"/api/merges/{second.LogId}/undo", null, Cancel));
        await WorkAsync(await shelf.Admin.PostAsync($"/api/merges/{first.LogId}/undo", null, Cancel));
        await using var db = await shelf.DbAsync(Cancel);
        Assert.AreEqual(3, await db.BookTags.CountAsync(l => l.Tag!.FieldId == null, Cancel), "Ex, Why and Zed on the book again");
    }

    [TestMethod]
    public async Task AFieldMergeKeepsItsKeyAndIsUndone()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("dark.fb2", Fb2("Dark", ["Ann Writer"]));
        shelf.Write("odd.fb2", Fb2("Odd", ["Ann Writer"]));
        await shelf.ScanAsync(Cancel);
        int category;
        await using (var db = await shelf.DbAsync(Cancel))
            category = await db.Tags.Where(t => t.FieldKey == "category").Select(t => t.Id).SingleAsync(Cancel);
        var genre = await CreateAsync(shelf, new TagInput("Genre", FieldKey: "genre"));
        var horror = await CreateAsync(shelf, new TagInput("Horror", FieldId: genre.Id));
        var weird = await CreateAsync(shelf, new TagInput("Weird", FieldId: genre.Id));
        var categoryHorror = await CreateAsync(shelf, new TagInput("Horror", FieldId: category));
        await LinkAsync(shelf, "Dark", horror.Id);
        await LinkAsync(shelf, "Odd", weird.Id);

        var merged = await WorkAsync(await shelf.Admin.PostAsJsonAsync("/api/fields/genre/merge", new TagEndpoints.FieldMergeInput("category"), Cancel));

        Assert.AreEqual(1, merged.Links, "genre:Horror's one link merged; Weird moved with its own");
        Assert.AreEqual(1, await CountAsync(shelf, "genre:weird"), "the key is an alias of category now");
        Assert.AreEqual(1, await CountAsync(shelf, "category:horror"));
        Assert.AreEqual(2, await CountAsync(shelf, "category:"));
        await using (var db = await shelf.DbAsync(Cancel))
        {
            Assert.IsFalse(await db.Tags.AnyAsync(t => t.FieldKey == "genre", Cancel));
            Assert.AreEqual(category, await db.Tags.Where(t => t.Id == weird.Id).Select(t => t.FieldId).SingleAsync(Cancel));
            Assert.IsTrue(await UnderAsync(shelf, weird.Id, category));
            Assert.AreEqual(2, await TagCountAsync(shelf, categoryHorror.Id) + await TagCountAsync(shelf, weird.Id));
        }

        await WorkAsync(await shelf.Admin.PostAsync($"/api/merges/{merged.LogId}/undo", null, Cancel));

        Assert.AreEqual(1, await CountAsync(shelf, "genre:horror"));
        Assert.AreEqual(1, await CountAsync(shelf, "genre:weird"));
        Assert.AreEqual(0, await CountAsync(shelf, "category:"));
        await using (var db = await shelf.DbAsync(Cancel))
        {
            var field = await db.Tags.Where(t => t.FieldKey == "genre").Select(t => t.Id).SingleAsync(Cancel);
            Assert.AreEqual(genre.Id, field, "the field made again with its id");
            Assert.IsFalse(await db.FieldKeyAliases.AnyAsync(Cancel));
            Assert.IsTrue(await UnderAsync(shelf, weird.Id, field));
            Assert.IsFalse(await UnderAsync(shelf, weird.Id, category));
        }
    }

    [TestMethod]
    public async Task BigChangesRunInTheBackground()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", ["Ann Writer"], "Bulk"));
        shelf.Write("two.fb2", Fb2("Two", ["Ann Writer"], "Bulk, Target"));
        await shelf.ScanAsync(Cancel);
        var (bulk, target) = (await TagIdAsync(shelf, "Bulk"), await TagIdAsync(shelf, "Target"));
        shelf.Factory.Services.GetRequiredService<TagService>().Threshold = 0;

        using var started = await shelf.Admin.PostAsJsonAsync($"/api/tags/{bulk}/merge", new TagEndpoints.MergeInput(target), Cancel);
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode, await started.Content.ReadAsStringAsync(Cancel));
        var work = (await started.Content.ReadFromJsonAsync<TagWork>(ApiClients.Json, Cancel))!;
        var run = await WaitAsync(shelf.Admin, work.RunId!.Value);

        Assert.AreEqual(TaskOutcome.Succeeded, run.Outcome, run.Summary);
        Assert.AreEqual(BackgroundTaskKind.TagMerge, run.Task);
        Assert.IsNull(run.LibraryId, "a run of the whole tenant");
        Assert.AreEqual(2, run.Counts!.Changed);
        Assert.AreEqual(2, await TagCountAsync(shelf, target));
        var history = await shelf.Admin.GetAsync<List<TaskRunInfo>>("/api/tasks", Cancel);
        Assert.IsTrue(history.Any(r => r.Id == run.Id), "listed with the library runs");

        using var deleting = await shelf.Admin.DeleteAsync($"/api/tags/{target}", Cancel);
        Assert.AreEqual(HttpStatusCode.Accepted, deleting.StatusCode);
        var deleted = await WaitAsync(shelf.Admin, (await deleting.Content.ReadFromJsonAsync<TagWork>(ApiClients.Json, Cancel))!.RunId!.Value);
        Assert.AreEqual(TaskOutcome.Succeeded, deleted.Outcome);
        Assert.AreEqual(2, deleted.Counts!.Removed);
        await using var db = await shelf.DbAsync(Cancel);
        Assert.IsFalse(await db.BookTags.AnyAsync(l => l.Tag!.FieldId == null, Cancel));
    }

    [TestMethod]
    public async Task TheManagerListsEveryTagByKeyAndFindsThem()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", ["Ann Writer", "Bea King"], "Alpha, Beta"));
        await shelf.ScanAsync(Cancel);
        var parent = await TagIdAsync(shelf, "Alpha");
        await CreateAsync(shelf, new TagInput("Gamma", ParentIds: [parent]));
        await CreateAsync(shelf, new TagInput("Delta"));

        var fields = await shelf.Admin.GetAsync<ManagedFields>("/api/tags/manage/fields", Cancel);
        var first = await shelf.Admin.GetAsync<ManagedPage>("/api/tags/manage?limit=2", Cancel);
        var second = await shelf.Admin.GetAsync<ManagedPage>($"/api/tags/manage?limit=2&after={first.Next}", Cancel);
        var authors = await shelf.Admin.GetAsync<ManagedPage>("/api/tags/manage?field=author&q=king", Cancel);
        using var unknown = await shelf.Admin.GetAsync("/api/tags/manage?field=nothing", Cancel);

        Assert.AreEqual("author", fields.Fields[0].Key, "built-in fields first, in their order");
        Assert.AreEqual(2, fields.Fields.Single(f => f.Key == "author").Values);
        Assert.IsGreaterThanOrEqualTo(6, fields.PlainTags, "Alpha, Beta, Gamma, Delta, Meta Tags, Archived and the import category");
        CollectionAssert.AreEqual(new[] { "Alpha", "Archived" }, first.Items.Select(t => t.Name).ToList(), "tags without books too, by sort name");
        Assert.AreEqual(1, first.Items[0].Children);
        Assert.AreEqual(1, first.Items[0].Books);
        Assert.AreEqual("Beta", second.Items[0].Name);
        CollectionAssert.AreEqual(new[] { "Bea King" }, authors.Items.Select(t => t.Name).ToList(), "found by a word of the name");
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [TestMethod]
    public async Task AParentCountsTheBooksOfTheTagsUnderItOnce()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        var fiction = await CreateAsync(shelf, new TagInput("Fiction"));
        var other = await CreateAsync(shelf, new TagInput("Other"));
        var fantasy = await CreateAsync(shelf, new TagInput("Fantasy", ParentIds: [fiction.Id]));
        var horror = await CreateAsync(shelf, new TagInput("Horror", ParentIds: [fiction.Id]));
        shelf.Write("both.fb2", Fb2("Both", ["Ann Writer"], "Fantasy, Horror"));
        shelf.Write("one.fb2", Fb2("One", ["Ann Writer", "Bea Reader"], "Fantasy"));
        await shelf.ScanAsync(Cancel);
        int authorField;
        await using (var db = await shelf.DbAsync(Cancel))
            authorField = await db.Tags.Where(t => t.FieldKey == "author").Select(t => t.Id).SingleAsync(Cancel);

        Assert.AreEqual((0, 2), await CountsAsync(shelf, fiction.Id), "a book under two of its children counts once");
        Assert.AreEqual((2, 2), await CountsAsync(shelf, fantasy.Id));
        Assert.AreEqual((0, 2), await CountsAsync(shelf, authorField), "a field: its books with any author, once each");

        // Horror moves under Other: Fiction keeps Both through Fantasy.
        using (var moved = await shelf.Admin.PutAsJsonAsync($"/api/tags/{horror.Id}", new TagInput("Horror", ParentIds: [other.Id]), Cancel))
            Assert.AreEqual(HttpStatusCode.OK, moved.StatusCode);
        Assert.AreEqual((0, 2), await CountsAsync(shelf, fiction.Id));
        Assert.AreEqual((0, 1), await CountsAsync(shelf, other.Id));

        // Fantasy merged into Horror: Fiction loses its books, Other gains One.
        await WorkAsync(await shelf.Admin.PostAsJsonAsync($"/api/tags/{fantasy.Id}/merge", new TagEndpoints.MergeInput(horror.Id), Cancel));
        Assert.AreEqual((0, 0), await CountsAsync(shelf, fiction.Id), "no row left");
        Assert.AreEqual((0, 2), await CountsAsync(shelf, other.Id));
        Assert.AreEqual((2, 2), await CountsAsync(shelf, horror.Id));

        // A book gone missing leaves every count it was in.
        File.Delete(Path.Combine(shelf.Root, "one.fb2"));
        await shelf.ScanAsync(Cancel);
        Assert.AreEqual((0, 1), await CountsAsync(shelf, other.Id));
        Assert.AreEqual((0, 1), await CountsAsync(shelf, authorField));
    }

    [TestMethod]
    public async Task MovingATagWithManyBooksCountsInTheBackground()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        var fiction = await CreateAsync(shelf, new TagInput("Fiction"));
        var saga = await CreateAsync(shelf, new TagInput("Saga"));
        shelf.Write("one.fb2", Fb2("One", ["Ann Writer"], "Saga"));
        shelf.Write("two.fb2", Fb2("Two", ["Ann Writer"], "Saga"));
        await shelf.ScanAsync(Cancel);
        shelf.Factory.Services.GetRequiredService<TagService>().Threshold = 1;

        using var moved = await shelf.Admin.PutAsJsonAsync($"/api/tags/{saga.Id}", new TagInput("Saga", ParentIds: [fiction.Id]), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, moved.StatusCode);
        Assert.IsTrue(await UnderAsync(shelf, saga.Id, fiction.Id), "the parent is there at once");
        var runs = await shelf.Admin.GetAsync<List<TaskRunInfo>>("/api/tasks", Cancel);
        var run = await WaitAsync(shelf.Admin, runs.Single(r => r.Task == BackgroundTaskKind.TagCounts).Id);

        Assert.AreEqual(TaskOutcome.Succeeded, run.Outcome, run.Summary);
        Assert.AreEqual((0, 2), await CountsAsync(shelf, fiction.Id), "counted by the run");
    }

    [TestMethod]
    public async Task OnlyThoseWhoManageTagsChangeThem()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        var groups = await shelf.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("curator", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Curators).Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(shelf.Factory, "reader", ApiClients.Password, Cancel);
        using var curator = await ApiClients.SignInAsync(shelf.Factory, "curator", ApiClients.Password, Cancel);

        using var refused = await reader.PostAsJsonAsync("/api/tags", new TagInput("Nope"), Cancel);
        using var made = await curator.PostAsJsonAsync("/api/tags", new TagInput("Yes"), Cancel);
        using var merges = await reader.GetAsync("/api/merges", Cancel);

        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, made.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, merges.StatusCode);
    }

    // ---- Helpers ----

    private async Task<TagDefinition> CreateAsync(Shelf shelf, TagInput input)
    {
        using var response = await shelf.Admin.PostAsJsonAsync("/api/tags", input, Cancel);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
        return (await response.Content.ReadFromJsonAsync<TagDefinition>(ApiClients.Json, Cancel))!;
    }

    private async Task<TagWork> WorkAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
            return (await response.Content.ReadFromJsonAsync<TagWork>(ApiClients.Json, Cancel))!;
        }
    }

    private async Task<int> TagIdAsync(Shelf shelf, string name)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Tags.Where(t => t.Name == name).Select(t => t.Id).SingleAsync(Cancel);
    }

    private async Task<bool> UnderAsync(Shelf shelf, int descendant, int ancestor)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.TagClosure.AnyAsync(c => c.AncestorId == ancestor && c.DescendantId == descendant, Cancel);
    }

    private async Task<int> TagCountAsync(Shelf shelf, int tagId)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.TagCounts.Where(c => c.TagId == tagId).SumAsync(c => c.BookCount, Cancel);
    }

    /// <summary>A tag's (direct, total) counts in the shelf's library; (0, 0) without a row.</summary>
    private async Task<(int, int)> CountsAsync(Shelf shelf, int tagId)
    {
        await using var db = await shelf.DbAsync(Cancel);
        var row = await db.TagCounts.SingleOrDefaultAsync(c => c.TagId == tagId && c.LibraryId == shelf.LibraryId, Cancel);
        return row is null ? (0, 0) : (row.BookCount, row.TotalCount);
    }

    private async Task<int> CountAsync(Shelf shelf, string q) =>
        (await shelf.Admin.GetAsync<Page<BookListItem>>($"/api/books?library={shelf.LibraryId}&limit=50&q={Uri.EscapeDataString(q)}", Cancel)).Items.Count;

    /// <summary>A link put on a book by hand, its count with it (what tagging a book will do).</summary>
    private async Task LinkAsync(Shelf shelf, string title, int tagId)
    {
        await using var db = await shelf.DbAsync(Cancel);
        var book = await db.Books.Where(b => b.Title == title).Select(b => b.Id).SingleAsync(Cancel);
        var before = await TagLinks.SnapshotAsync(db, [book], Cancel);
        db.BookTags.Add(new BookTag { TenantId = shelf.TenantId, BookId = book, TagId = tagId, Source = TagSource.Manual });
        await db.SaveChangesAsync(Cancel);
        await TagLinks.CountAsync(db, shelf.TenantId, before, await TagLinks.SnapshotAsync(db, [book], Cancel), Cancel);
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
