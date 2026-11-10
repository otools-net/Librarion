using System.Net;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Merging;
using Librarion.Core.Tagging;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Tests;

/// <summary>
/// Possible duplicate tags (stage 6 chunk 14, agents/docs/features/merging.md#possible-duplicate-tags-built-stage-6-chunk-14):
/// the name keys, and on both providers the groups built by the first scan, ranked by books, kept by edits and
/// merges, Not the same, a tag's look-alikes, the Find again run and rights.
/// </summary>
[TestClass]
public sealed class TagDuplicateTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string first, string last, string text = "") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info><author><first-name>{first}</first-name><last-name>{last}</last-name></author><book-title>{title}</book-title></title-info></description><body><p>{text}</p></body></FictionBook>
        """;

    [TestMethod]
    public void NamesFoldToTheirWordsSoundsAndInitials()
    {
        Assert.AreEqual(NameKeys.WordsText("Stephen King"), NameKeys.WordsText("KING,  Stephen"));
        Assert.AreEqual(NameKeys.WordsText("Émile Zola"), NameKeys.WordsText("Emile Zola"));
        Assert.AreEqual("king stephen", NameKeys.WordsText("King, Stephen"));
        Assert.AreEqual("frants kafka", NameKeys.WordsText("Кафка, Франц"));

        foreach (var (one, other) in new[] { ("Stephen King", "Steven King"), ("Stephen King", "Стивен Кинг"), ("Stanislaw Lem", "Станислав Лем"),
                     ("Lev Tolstoy", "Лев Толстой"), ("King, Stephen", "Stephen King") })
            Assert.AreEqual(NameKeys.SoundKey(one), NameKeys.SoundKey(other), $"{one} / {other}");
        foreach (var (one, other) in new[] { ("Stephen King", "Stephen Fry"), ("Stephen King", "King"), ("Anna Karenina", "Anna Karenin 2") })
            Assert.AreNotEqual(NameKeys.SoundKey(one), NameKeys.SoundKey(other), $"{one} / {other}");
        Assert.IsNull(NameKeys.SoundKey("!!!"));

        Assert.AreEqual(NameKeys.InitialsKey("S. King"), NameKeys.InitialsKey("Stephen King"));
        Assert.AreEqual(NameKeys.InitialsKey("King, S."), NameKeys.InitialsKey("King, Stephen"));
        Assert.AreNotEqual(NameKeys.InitialsKey("S. King"), NameKeys.InitialsKey("Peter King"));
        Assert.IsNull(NameKeys.InitialsKey("King"));
        Assert.IsTrue(NameKeys.IsInitials("J. R. R. Tolkien") && NameKeys.IsInitials("King, S."));
        Assert.IsFalse(NameKeys.IsInitials("Stephen King") || NameKeys.IsInitials("King"));

        var field = new Tag { Name = "Stephen King", FieldKey = "author" };
        NameKeys.Apply(field);
        Assert.IsNull(field.SoundKey, "a field's own row has no keys");
    }

    [TestMethod]
    public async Task TheFirstScanFindsLookAlikesRankedByBooks()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("a.fb2", Fb2("Carrie", "Stephen", "King", "a"));
        shelf.Write("b.fb2", Fb2("It", "Stephen", "King", "b"));
        shelf.Write("c.fb2", Fb2("Misery", "Стивен", "Кинг", "c"));
        shelf.Write("d.fb2", Fb2("Cujo", "S.", "King", "d"));
        shelf.Write("e.fb2", Fb2("Solaris", "Stanislaw", "Lem", "e"));
        shelf.Write("f.fb2", Fb2("Eden", "Станислав", "Лем", "f"));
        shelf.Write("g.fb2", Fb2("Matilda", "Roald", "Dahl", "g"));
        await shelf.ScanAsync(Cancel);

        var page = await shelf.Admin.GetAsync<TagDuplicatePage>("/api/tag-duplicates?field=author", Cancel);
        Assert.IsTrue(page.Built, "built by the first scan");
        var groups = page.Items.Select(Describe).ToList();
        CollectionAssert.AreEqual(new[]
        {
            "Initials: Stephen King, Стивен Кинг, S. King",
            "Sound: Stephen King, Стивен Кинг",
            "Sound: Stanislaw Lem, Станислав Лем",
        }, groups, string.Join(" | ", groups));
        Assert.AreEqual(0, (await shelf.Admin.GetAsync<TagDuplicatePage>("/api/tag-duplicates", Cancel)).Items.Count, "plain tags: none");

        // A tag made by hand joins its group; its look-alikes say why.
        var author = await FieldAsync(shelf, "author");
        using (var made = await shelf.Admin.PostAsJsonAsync("/api/tags", new TagInput("King, Stephen", FieldId: author), Cancel))
            Assert.AreEqual(HttpStatusCode.Created, made.StatusCode, await made.Content.ReadAsStringAsync(Cancel));
        var stephen = await TagAsync(shelf, "Stephen King");
        var alike = await shelf.Admin.GetAsync<List<TagLookAlike>>($"/api/tags/{stephen}/look-alikes", Cancel);
        CollectionAssert.AreEquivalent(new[] { "Words: King, Stephen", "Sound: Стивен Кинг", "Initials: S. King" },
            alike.Select(a => $"{a.Level}: {a.Tag.Name}").ToList());
    }

    [TestMethod]
    public async Task MergesAndNotTheSameLeaveTheList()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("a.fb2", Fb2("Carrie", "Stephen", "King", "a"));
        shelf.Write("b.fb2", Fb2("Misery", "Steven", "King", "b"));
        shelf.Write("e.fb2", Fb2("Solaris", "Stanislaw", "Lem", "e"));
        shelf.Write("f.fb2", Fb2("Eden", "Stanislav", "Lem", "f"));
        await shelf.ScanAsync(Cancel);
        Assert.AreEqual(2, (await shelf.Admin.GetAsync<TagDuplicatePage>("/api/tag-duplicates?field=author", Cancel)).Items.Count);

        using (var merged = await shelf.Admin.PostAsJsonAsync($"/api/tags/{await TagAsync(shelf, "Steven King")}/merge",
                   new TagEndpoints.MergeInput(await TagAsync(shelf, "Stephen King")), Cancel))
            Assert.AreEqual(HttpStatusCode.OK, merged.StatusCode, await merged.Content.ReadAsStringAsync(Cancel));
        using (var apart = await shelf.Admin.PostAsJsonAsync("/api/tag-duplicates/not",
                   new TagDuplicateEndpoints.NotSameInput([await TagAsync(shelf, "Stanislaw Lem"), await TagAsync(shelf, "Stanislav Lem")]), Cancel))
            Assert.AreEqual(HttpStatusCode.NoContent, apart.StatusCode);
        Assert.AreEqual(0, (await shelf.Admin.GetAsync<TagDuplicatePage>("/api/tag-duplicates?field=author", Cancel)).Items.Count);

        // Find again keeps them apart.
        using var started = await shelf.Admin.PostAsync("/api/tag-duplicates/build", null, Cancel);
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<DuplicateEndpoints.BuildStarted>(ApiClients.Json, Cancel))!.RunId;
        for (var i = 0; i < 300 && (await shelf.Admin.GetAsync<Librarion.Core.Tasks.TaskRunInfo>($"/api/tasks/{run}", Cancel)).Outcome == TaskOutcome.Running; i++)
            await Task.Delay(50, Cancel);
        Assert.AreEqual(0, (await shelf.Admin.GetAsync<TagDuplicatePage>("/api/tag-duplicates?field=author", Cancel)).Items.Count);
    }

    [TestMethod]
    public async Task OnlyThoseWhoManageTagsSeeThem()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        var groups = await shelf.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(shelf.Factory, "reader", ApiClients.Password, Cancel);
        foreach (var url in new[] { "/api/tag-duplicates", "/api/tags/1/look-alikes" })
        {
            using var response = await reader.GetAsync(url, Cancel);
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, url);
        }

        using var build = await reader.PostAsync("/api/tag-duplicates/build", null, Cancel);
        Assert.AreEqual(HttpStatusCode.Forbidden, build.StatusCode);
    }

    // ---- Helpers ----

    private static string Describe(TagDuplicateItem group) => $"{group.Level}: {string.Join(", ", group.Tags.Select(t => t.Name))}";

    private async Task<int> TagAsync(TestLibrary shelf, string name)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Tags.Where(t => t.Name == name).Select(t => t.Id).SingleAsync(Cancel);
    }

    private async Task<int> FieldAsync(TestLibrary shelf, string key)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Tags.Where(t => t.FieldKey == key).Select(t => t.Id).SingleAsync(Cancel);
    }
}
