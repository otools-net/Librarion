using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Merging;
using Librarion.Core.Settings;
using Librarion.Core.Tasks;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>
/// The stored duplicate groups, on both providers (stage 6 chunk 11, agents/docs/features/merging.md#stored-duplicate-groups):
/// built by the first scan, kept by the next scans and by edits, noise kept without members, an FB2 id grouping only
/// with the same title or first author, books marked not duplicates left out, the same-file setting, the Rebuild run.
/// </summary>
[TestClass]
public sealed class DuplicateIndexTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string? author = null, string? isbn = null, string text = "", string? id = null) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info>{(author is null ? "" : $"<author><first-name>{author.Split(' ')[0]}</first-name><last-name>{author.Split(' ')[^1]}</last-name></author>")}<book-title>{title}</book-title></title-info>{(id is null ? "" : $"<document-info><id>{id}</id></document-info>")}{(isbn is null ? "" : $"<publish-info><isbn>{isbn}</isbn></publish-info>")}</description><body><p>{text}</p></body></FictionBook>
        """;

    [TestMethod]
    public async Task TheFirstScanBuildsTheGroupsAndLaterScansKeepThem()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("Dune.fb2", Fb2("Dune", "Frank Herbert", isbn: "9780441172719", text: "one"));
        shelf.Write("dune (scan).fb2", Fb2("DUNE", "Frank Herbert", text: "two"));
        shelf.Write("Dune Messiah.fb2", Fb2("Dune Messiah", "Frank Herbert", isbn: "978-0-441-17271-9", text: "three"));
        shelf.Write("Solaris.fb2", Fb2("Solaris", "Stanislaw Lem"));
        await shelf.ScanAsync(Cancel);
        var (dune, scan, messiah) = (await BookOfAsync(shelf, "Dune.fb2"), await BookOfAsync(shelf, "dune (scan).fb2"), await BookOfAsync(shelf, "Dune Messiah.fb2"));

        await using (var db = await shelf.DbAsync(Cancel))
            Assert.IsNotNull(await db.Tenants.Select(t => t.DuplicatesBuilt).SingleAsync(Cancel), "built by the first scan");
        var groups = await GroupsAsync(shelf);
        CollectionAssert.AreEquivalent(new[] { G("Work", dune, scan), G("Identifier", dune, messiah) }, groups);

        // A third copy of the work comes in, one of the others changes its title: the groups follow.
        shelf.Write("Dune again.fb2", Fb2("Dune", "Frank Herbert", text: "four"));
        shelf.Write("dune (scan).fb2", Fb2("Dune: the scan", "Frank Herbert", text: "two, read again"));
        await shelf.ScanAsync(Cancel);
        var again = await BookOfAsync(shelf, "Dune again.fb2");
        CollectionAssert.AreEquivalent(new[] { G("Work", dune, again), G("Identifier", dune, messiah) }, await GroupsAsync(shelf));

        // A book edited on its page leaves its work's group.
        using (var edited = await shelf.Admin.PutAsJsonAsync($"/api/books/{again}", new BookEdit(new BookColumnsEdit("Dune (abridged)", null, null)), Cancel))
            Assert.AreEqual(HttpStatusCode.OK, edited.StatusCode, await edited.Content.ReadAsStringAsync(Cancel));
        CollectionAssert.AreEquivalent(new[] { G("Identifier", dune, messiah) }, await GroupsAsync(shelf));

        // The root taken out of the library: its books and their groups go.
        await shelf.Factory.Services.GetRequiredService<Librarion.Core.Import.LibraryScanner>().RemoveRootAsync(shelf.TenantId, await RootIdAsync(shelf), null, Cancel);
        Assert.AreEqual(0, (await GroupsAsync(shelf)).Count);
    }

    [TestMethod]
    public async Task AKeySharedByManyBooksIsNoise()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        for (var i = 0; i < DuplicateIndex.MaxGroup; i++)
            shelf.Write($"notes-{i}.fb2", Fb2("Notes", "Some Body", text: $"n{i}"));
        shelf.Write("Kept.fb2", Fb2("Kept", "Ann Writer", text: "a"));
        shelf.Write("Kept 2.fb2", Fb2("Kept", "Ann Writer", text: "b"));
        await shelf.ScanAsync(Cancel);

        await using var db = await shelf.DbAsync(Cancel);
        var rows = await db.DuplicateGroups.Select(g => new { g.Noise, g.BookCount, Members = g.Members.Count }).ToListAsync(Cancel);
        Assert.AreEqual(1, rows.Count(r => r.Noise && r.Members == 0), "Notes by Some Body: noise, no members");
        Assert.AreEqual(1, rows.Count(r => !r.Noise && r.BookCount == 2 && r.Members == 2));

        // A rescan touching a noise key's books doesn't read it again: it stays noise.
        shelf.Write("notes-0.fb2", Fb2("Notes", "Some Body", text: "changed"));
        await shelf.ScanAsync(Cancel);
        Assert.AreEqual(1, await db.DuplicateGroups.CountAsync(g => g.Noise, Cancel));
    }

    [TestMethod]
    public async Task AnFb2IdGroupsOnlyBooksWithTheSameTitleOrFirstAuthor()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("a.fb2", Fb2("The Hobbit", "John Tolkien", id: "template-id", text: "a"));
        shelf.Write("b.fb2", Fb2("The Hobbit", "J Tolkien", id: "template-id", text: "b"));
        shelf.Write("c.fb2", Fb2("Roadside Picnic", "Boris Strugatsky", id: "template-id", text: "c"));
        shelf.Write("d.fb2", Fb2("Monday Begins on Saturday", "Boris Strugatsky", id: "template-id", text: "d"));
        await shelf.ScanAsync(Cancel);
        var (a, b, c, d) = (await BookOfAsync(shelf, "a.fb2"), await BookOfAsync(shelf, "b.fb2"), await BookOfAsync(shelf, "c.fb2"), await BookOfAsync(shelf, "d.fb2"));

        CollectionAssert.AreEquivalent(new[] { G("Identifier", a, b), G("Identifier", c, d) }, await GroupsAsync(shelf),
            "the same title, the same first author; never the id alone");
    }

    [TestMethod]
    public async Task BooksMarkedNotDuplicatesAreLeftOut()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("Twins", "Ann Writer", text: "1"));
        shelf.Write("two.fb2", Fb2("Twins", "Ann Writer", text: "2"));
        shelf.Write("three.fb2", Fb2("Twins", "Ann Writer", text: "3"));
        await shelf.ScanAsync(Cancel);
        var (one, two, three) = (await BookOfAsync(shelf, "one.fb2"), await BookOfAsync(shelf, "two.fb2"), await BookOfAsync(shelf, "three.fb2"));
        await using (var db = await shelf.DbAsync(Cancel))
        {
            db.NotDuplicates.Add(new NotDuplicate { TenantId = shelf.TenantId, BookId = three, OtherId = three, Created = DateTime.UtcNow });
            db.NotDuplicates.Add(new NotDuplicate { TenantId = shelf.TenantId, BookId = Math.Min(one, two), OtherId = Math.Max(one, two), Created = DateTime.UtcNow });
            await db.SaveChangesAsync(Cancel);
        }

        var run = await RebuildAsync(shelf.Admin);
        Assert.AreEqual((TaskOutcome.Succeeded, BackgroundTaskKind.DuplicateBuild, 0), (run.Outcome, run.Task, run.Counts!.Changed), run.Summary);
        Assert.AreEqual(0, (await GroupsAsync(shelf)).Count, "three is unique, one and two are not duplicates");
        Assert.AreEqual(0, (await shelf.Admin.GetAsync<List<BookDuplicate>>($"/api/books/{one}/duplicates", Cancel))!.Count, "nor on the book's page");
    }

    [TestMethod]
    public async Task TheSameFileIsABookOfItsOwnWhenTheSettingIsOff()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        var text = Fb2("Copied", "Ann Writer", text: "the same bytes");
        shelf.Zip("a.zip", CompressionLevel.Optimal, ("copied.fb2", text));
        shelf.Zip("b.zip", CompressionLevel.Optimal, ("copied.fb2", text));
        await shelf.ScanAsync(Cancel);
        await using (var db = await shelf.DbAsync(Cancel))
            Assert.AreEqual(1, await db.Books.CountAsync(Cancel), "on by default: one book, two files");

        await using (var db = await shelf.DbAsync(Cancel))
        {
            db.Settings.Add(new Setting { Level = SettingLevel.Tenant, OwnerId = shelf.TenantId, TenantId = shelf.TenantId, Key = SettingCatalog.MergeSameFiles, Value = "\"off\"" });
            await db.SaveChangesAsync(Cancel);
        }
        shelf.Zip("c.zip", CompressionLevel.Optimal, ("copied.fb2", text));
        await shelf.ScanAsync(Cancel);

        await using (var db = await shelf.DbAsync(Cancel))
            Assert.AreEqual(2, await db.Books.CountAsync(Cancel));
        var groups = await GroupsAsync(shelf);
        Assert.AreEqual(1, groups.Count(g => g.StartsWith("File:", StringComparison.Ordinal)), "the copy is a duplicate by its file");
    }

    [TestMethod]
    public async Task OnlyThoseWhoMergeBooksRebuild()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        var groups = await shelf.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("curator", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Curators).Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(shelf.Factory, "reader", ApiClients.Password, Cancel);
        using var curator = await ApiClients.SignInAsync(shelf.Factory, "curator", ApiClients.Password, Cancel);

        using var refused = await reader.PostAsync("/api/duplicates/build", null, Cancel);
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.AreEqual(TaskOutcome.Succeeded, (await RebuildAsync(curator)).Outcome, "the curator follows its run");
    }

    // ---- Helpers ----

    private static string G(string level, params int[] members) => $"{level}:{string.Join(',', members.Order())}";

    /// <summary>Every group as "Level:member,member" (members by id), noise left out.</summary>
    private async Task<List<string>> GroupsAsync(Shelf shelf)
    {
        await using var db = await shelf.DbAsync(Cancel);
        var groups = await db.DuplicateGroups.Where(g => !g.Noise).Select(g => new { g.Level, Members = g.Members.Select(m => m.BookId).ToList() }).ToListAsync(Cancel);
        return groups.Select(g => $"{g.Level}:{string.Join(',', g.Members.Order())}").ToList();
    }

    private async Task<int> BookOfAsync(Shelf shelf, string path)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.BookFiles.Where(f => f.Path == path).Select(f => f.BookId).SingleAsync(Cancel);
    }

    private async Task<int> RootIdAsync(Shelf shelf)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.LibraryRoots.Select(r => r.Id).SingleAsync(Cancel);
    }

    private async Task<TaskRunInfo> RebuildAsync(HttpClient client)
    {
        using var started = await client.PostAsync("/api/duplicates/build", null, Cancel);
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode, await started.Content.ReadAsStringAsync(Cancel));
        var run = (await started.Content.ReadFromJsonAsync<DuplicateEndpoints.BuildStarted>(ApiClients.Json, Cancel))!.RunId;
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
