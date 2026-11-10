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

namespace Librarion.Tests;

/// <summary>
/// Book merges undone, groups merged in bulk and books split, on both providers (stage 6 chunk 13,
/// agents/docs/features/merging.md#undo-bulk-merge-and-split-built-stage-6-chunk-13): a whole merge put back with its
/// counts and groups, values changed since kept, a later merge of the kept book blocking; bulk merges only of the
/// confident levels and never of books marked apart; a split read from its files or copied from an import; rights.
/// </summary>
[TestClass]
public sealed class BookMergeUndoTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string author, string? isbn = null, string text = "", string? keywords = null) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info><author><first-name>{author.Split(' ')[0]}</first-name><last-name>{author.Split(' ')[^1]}</last-name></author><book-title>{title}</book-title>{(keywords is null ? "" : $"<keywords>{keywords}</keywords>")}</title-info>{(isbn is null ? "" : $"<publish-info><isbn>{isbn}</isbn></publish-info>")}</description><body><p>{text}</p></body></FictionBook>
        """;

    [TestMethod]
    public async Task AnUndoPutsTheWholeMergeBack()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("Dune.fb2", Fb2("Dune", "Frank Herbert", text: "one"));
        shelf.Write("dune (scan).fb2", Fb2("DUNE: the complete edition", "Frank Herbert", isbn: "9780441172719", text: "two", keywords: "classic"));
        shelf.Write("dune (old).fb2", Fb2("Dune", "Frank Herbert", text: "three", keywords: "classic, desert"));
        await shelf.ScanAsync(Cancel);
        var (kept, scan, old) = (await BookOfAsync(shelf, "Dune.fb2"), await BookOfAsync(shelf, "dune (scan).fb2"), await BookOfAsync(shelf, "dune (old).fb2"));
        var groupsBefore = await GroupsAsync(shelf);
        var countsBefore = await TagCountsAsync(shelf);
        var libraryBefore = await LibraryCountAsync(shelf);

        await MergeAsync(shelf.Admin, new BookMergeInput(kept, [scan, old], TitleFrom: scan));
        Assert.AreEqual(libraryBefore - 2, await LibraryCountAsync(shelf));
        var merges = await shelf.Admin.GetAsync<List<BookMergeInfo>>($"/api/duplicates/merges?library={shelf.LibraryId}", Cancel);
        Assert.AreEqual(1, merges.Count, "one merge, whatever the number of books");
        Assert.AreEqual((kept, 2, "DUNE: the complete edition"), (merges[0].TargetId, merges[0].Books, merges[0].SourceNames[0]));

        // A child log stands for its merge.
        long child;
        await using (var db = await shelf.DbAsync(Cancel))
            child = await db.MergeLogs.Where(l => l.ParentId == merges[0].Id).Select(l => l.Id).SingleAsync(Cancel);
        using (var undone = await shelf.Admin.PostAsync($"/api/duplicates/merges/{child}/undo", null, Cancel))
        {
            Assert.AreEqual(HttpStatusCode.OK, undone.StatusCode, await undone.Content.ReadAsStringAsync(Cancel));
            Assert.AreEqual(2, (await undone.Content.ReadFromJsonAsync<DuplicateEndpoints.MergeUndone>(ApiClients.Json, Cancel))!.Books);
        }

        var keptBook = await shelf.Admin.GetAsync<BookDetail>($"/api/books/{kept}", Cancel);
        var scanBook = await shelf.Admin.GetAsync<BookDetail>($"/api/books/{scan}", Cancel);
        Assert.AreEqual(("Dune", 1, 0), (keptBook.Title, keptBook.Files.Count, keptBook.Identifiers.Count), "the kept book as it was");
        Assert.IsFalse(keptBook.Tags.Any(t => t.Name == "classic"));
        Assert.AreEqual((null, "DUNE: the complete edition", 1), (scanBook.MergedIntoId, scanBook.Title, scanBook.Files.Count));
        Assert.IsTrue(scanBook.Identifiers.Any(i => i.Key == "isbn") && scanBook.Tags.Any(t => t.Name == "classic"));
        Assert.IsTrue((await shelf.Admin.GetAsync<BookDetail>($"/api/books/{old}", Cancel)).Tags.Any(t => t.Name == "desert"));
        Assert.AreEqual(libraryBefore, await LibraryCountAsync(shelf));
        CollectionAssert.AreEquivalent(countsBefore, await TagCountsAsync(shelf), "every count as before");
        CollectionAssert.AreEquivalent(groupsBefore, await GroupsAsync(shelf), "the groups as before");

        using var again = await shelf.Admin.PostAsync($"/api/duplicates/merges/{merges[0].Id}/undo", null, Cancel);
        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
        StringAssert.Contains(await again.Content.ReadAsStringAsync(Cancel), "merge.undone");
        Assert.IsNotNull((await shelf.Admin.GetAsync<List<BookMergeInfo>>($"/api/duplicates/merges?library={shelf.LibraryId}", Cancel))[0].Undone);
    }

    [TestMethod]
    public async Task AValueChangedSinceTheMergeStays()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("a.fb2", Fb2("Twin", "Ann Writer", text: "a"));
        shelf.Write("b.fb2", Fb2("Twin, the second edition", "Ann Writer", text: "b"));
        await shelf.ScanAsync(Cancel);
        var (a, b) = (await BookOfAsync(shelf, "a.fb2"), await BookOfAsync(shelf, "b.fb2"));
        await MergeAsync(shelf.Admin, new BookMergeInput(a, [b], TitleFrom: b));
        await using (var db = await shelf.DbAsync(Cancel))
            await db.Books.Where(x => x.Id == a).ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, "Twin (curated)"), Cancel);

        await UndoAsync(shelf.Admin, await LastMergeAsync(shelf));
        Assert.AreEqual("Twin (curated)", (await shelf.Admin.GetAsync<BookDetail>($"/api/books/{a}", Cancel)).Title);
    }

    [TestMethod]
    public async Task ALaterMergeOfTheKeptBookIsUndoneFirst()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("a.fb2", Fb2("A", "Ann Writer", text: "a"));
        shelf.Write("b.fb2", Fb2("B", "Ann Writer", text: "b"));
        shelf.Write("c.fb2", Fb2("C", "Ann Writer", text: "c"));
        await shelf.ScanAsync(Cancel);
        var (a, b, c) = (await BookOfAsync(shelf, "a.fb2"), await BookOfAsync(shelf, "b.fb2"), await BookOfAsync(shelf, "c.fb2"));
        await MergeAsync(shelf.Admin, new BookMergeInput(a, [b]));
        var first = await LastMergeAsync(shelf);
        await MergeAsync(shelf.Admin, new BookMergeInput(c, [a]));
        var second = await LastMergeAsync(shelf);

        using (var blocked = await shelf.Admin.PostAsync($"/api/duplicates/merges/{first}/undo", null, Cancel))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, blocked.StatusCode);
            StringAssert.Contains(await blocked.Content.ReadAsStringAsync(Cancel), "merge.later");
        }

        await UndoAsync(shelf.Admin, second);
        await UndoAsync(shelf.Admin, first);
        await using var db = await shelf.DbAsync(Cancel);
        Assert.AreEqual(3, await db.Books.CountAsync(x => x.MergedIntoId == null, Cancel));
        Assert.AreEqual(1, await db.BookFiles.CountAsync(f => f.BookId == b, Cancel));
    }

    [TestMethod]
    public async Task BulkMergesTakeOnlyTheConfidentLevelsAndLeaveBooksMarkedApart()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        await using (var db = await shelf.DbAsync(Cancel))
        {
            db.Settings.Add(new Setting { Level = SettingLevel.Tenant, OwnerId = shelf.TenantId, TenantId = shelf.TenantId, Key = SettingCatalog.MergeSameFiles, Value = "\"off\"" });
            await db.SaveChangesAsync(Cancel);
        }

        var copied = Fb2("Copied", "Ann Writer", text: "the same bytes");
        shelf.Zip("a.zip", CompressionLevel.Optimal, ("copied.fb2", copied));
        shelf.Zip("b.zip", CompressionLevel.Optimal, ("copied.fb2", copied));
        shelf.Write("isbn-1.fb2", Fb2("Messiah", "Frank Herbert", isbn: "9780441172696", text: "1"));
        shelf.Write("isbn-2.fb2", Fb2("Dune Messiah", "Frank Herbert", isbn: "9780441172696", text: "2"));
        foreach (var i in Enumerable.Range(0, 3))
            shelf.Write($"apart-{i}.fb2", Fb2($"Apart {i}", "Bob Writer", isbn: "9780000000002", text: $"{i}"));
        shelf.Write("work-1.fb2", Fb2("Same Work", "Cid Writer", text: "w1"));
        shelf.Write("work-2.fb2", Fb2("Same Work", "Cid Writer", text: "w2"));
        await shelf.ScanAsync(Cancel);
        var apart = new[] { await BookOfAsync(shelf, "apart-0.fb2"), await BookOfAsync(shelf, "apart-1.fb2") };
        using (var marked = await shelf.Admin.PostAsJsonAsync("/api/duplicates/not", new DuplicateEndpoints.NotDuplicatesInput(apart), Cancel))
            Assert.AreEqual(HttpStatusCode.NoContent, marked.StatusCode);
        var libraryBefore = await LibraryCountAsync(shelf);

        using (var refused = await shelf.Admin.PostAsJsonAsync("/api/duplicates/merge", new DuplicateEndpoints.BulkMergeInput(shelf.LibraryId, null, "work"), Cancel))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
            StringAssert.Contains(await refused.Content.ReadAsStringAsync(Cancel), "merge.level");
        }

        var files = await BulkAsync(shelf.Admin, new DuplicateEndpoints.BulkMergeInput(shelf.LibraryId, null, "file"));
        Assert.AreEqual((TaskOutcome.Succeeded, 1), (files.Outcome, files.Counts!.Changed));
        var identifiers = await BulkAsync(shelf.Admin, new DuplicateEndpoints.BulkMergeInput(shelf.LibraryId, null, "identifier"));
        Assert.AreEqual((1, 1), (identifiers.Counts!.Changed, identifiers.Counts.Skipped), "the group with books marked apart is left");

        Assert.AreEqual(libraryBefore - 2, await LibraryCountAsync(shelf));
        await using (var db = await shelf.DbAsync(Cancel))
        {
            Assert.AreEqual(2, await db.Books.CountAsync(x => x.MergedIntoId == null && x.Title == "Same Work", Cancel), "title and author: through Compare only");
            Assert.AreEqual(3, await db.Books.CountAsync(x => x.MergedIntoId == null && x.Title!.StartsWith("Apart"), Cancel));
            var merged = await db.Books.Where(x => x.Title == "Messiah" || x.Title == "Dune Messiah").Select(x => x.MergedIntoId).ToListAsync(Cancel);
            Assert.AreEqual(1, merged.Count(m => m is null));
        }

        // Chosen groups: the work group's id is skipped all the same.
        var page = await shelf.Admin.GetAsync<DuplicatePage>($"/api/duplicates?library={shelf.LibraryId}&level=work", Cancel);
        var chosen = await BulkAsync(shelf.Admin, new DuplicateEndpoints.BulkMergeInput(shelf.LibraryId, page.Items.Select(g => g.Id).ToList(), null));
        Assert.AreEqual(0, chosen.Counts!.Changed);
    }

    [TestMethod]
    public async Task ASplitReadsItsFilesAndSetsTheBooksApart()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("Dune.fb2", Fb2("Dune", "Frank Herbert", text: "one"));
        shelf.Write("Emma.fb2", Fb2("Emma", "Jane Austen", isbn: "9780141439587", text: "two", keywords: "classic"));
        await shelf.ScanAsync(Cancel);
        var (dune, emma) = (await BookOfAsync(shelf, "Dune.fb2"), await BookOfAsync(shelf, "Emma.fb2"));
        await MergeAsync(shelf.Admin, new BookMergeInput(dune, [emma]));
        var libraryBefore = await LibraryCountAsync(shelf);
        var book = await shelf.Admin.GetAsync<BookDetail>($"/api/books/{dune}", Cancel);
        var emmaFile = book.Files.Single(f => f.Name == "Emma.fb2").Id;

        foreach (var files in new[] { Array.Empty<int>(), book.Files.Select(f => f.Id).ToArray(), [emmaFile, 999_999] })
        {
            using var refused = await shelf.Admin.PostAsJsonAsync($"/api/books/{dune}/split", new DuplicateEndpoints.SplitInput(files), Cancel);
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
            StringAssert.Contains(await refused.Content.ReadAsStringAsync(Cancel), "split.files");
        }

        using var split = await shelf.Admin.PostAsJsonAsync($"/api/books/{dune}/split", new DuplicateEndpoints.SplitInput([emmaFile]), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, split.StatusCode, await split.Content.ReadAsStringAsync(Cancel));
        var created = (await split.Content.ReadFromJsonAsync<BookDetail>(ApiClients.Json, Cancel))!;
        Assert.AreNotEqual(emma, created.Id, "a new book, not the merged one");
        Assert.AreEqual(("Emma", 1), (created.Title, created.Files.Count));
        Assert.IsTrue(created.Tags.Any(t => t.Name == "Jane Austen") && created.Identifiers.Any(i => i.Key == "isbn"), "read from its file");
        Assert.AreEqual(1, (await shelf.Admin.GetAsync<BookDetail>($"/api/books/{dune}", Cancel)).Files.Count);
        Assert.AreEqual(libraryBefore + 1, await LibraryCountAsync(shelf));
        await using var db = await shelf.DbAsync(Cancel);
        Assert.IsTrue(await db.NotDuplicates.AnyAsync(n => n.BookId == Math.Min(dune, created.Id) && n.OtherId == Math.Max(dune, created.Id), Cancel));
        // The book split keeps its own metadata (the user's answer): undo is what takes a merge's tags back.
        var classic = await db.Tags.Where(t => t.Name == "classic").Select(t => t.Id).SingleAsync(Cancel);
        Assert.AreEqual(2, await db.TagCounts.Where(c => c.TagId == classic).Select(c => c.BookCount).SingleAsync(Cancel));
    }

    [TestMethod]
    public async Task ASplitOfAnImportedBookCopiesItsMetadata()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("a.fb2", Fb2("Read from the file", "Ann Writer", text: "a"));
        shelf.Write("b.fb2", Fb2("Another file", "Bob Writer", text: "b", keywords: "kept"));
        await shelf.ScanAsync(Cancel);
        var (a, b) = (await BookOfAsync(shelf, "a.fb2"), await BookOfAsync(shelf, "b.fb2"));
        await MergeAsync(shelf.Admin, new BookMergeInput(a, [b]));
        await using (var db = await shelf.DbAsync(Cancel))
        {
            await db.Books.Where(x => x.Id == a).ExecuteUpdateAsync(s => s.SetProperty(x => x.MetadataSource, MetadataSource.Calibre).SetProperty(x => x.Title, "Calibre's title"), Cancel);
            db.BookIdentifiers.Add(new BookIdentifier { TenantId = shelf.TenantId, BookId = a, Scheme = "calibre-uuid", Value = "1234" });
            await db.SaveChangesAsync(Cancel);
        }

        var file = (await shelf.Admin.GetAsync<BookDetail>($"/api/books/{a}", Cancel)).Files.Single(f => f.Name == "b.fb2").Id;
        using var split = await shelf.Admin.PostAsJsonAsync($"/api/books/{a}/split", new DuplicateEndpoints.SplitInput([file]), Cancel);
        var created = (await split.Content.ReadFromJsonAsync<BookDetail>(ApiClients.Json, Cancel))!;
        Assert.AreEqual("Calibre's title", created.Title);
        Assert.IsTrue(created.Tags.Any(t => t.Name == "kept") && created.Tags.Any(t => t.Name == "Ann Writer"), "every link copied");
        Assert.AreEqual(0, created.Identifiers.Count, "the record's identifiers stay with it");
    }

    [TestMethod]
    public async Task OnlyThoseWhoMergeBooksUndoSplitOrMergeInBulk()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("a.fb2", Fb2("Twin", "Ann Writer", text: "a"));
        shelf.Write("b.fb2", Fb2("Twin", "Ann Writer", text: "b"));
        await shelf.ScanAsync(Cancel);
        var (a, b) = (await BookOfAsync(shelf, "a.fb2"), await BookOfAsync(shelf, "b.fb2"));
        await MergeAsync(shelf.Admin, new BookMergeInput(a, [b]));
        var log = await LastMergeAsync(shelf);
        var file = (await shelf.Admin.GetAsync<BookDetail>($"/api/books/{a}", Cancel)).Files[0].Id;
        var groups = await shelf.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(shelf.Factory, "reader", ApiClients.Password, Cancel);

        using (var list = await reader.GetAsync($"/api/duplicates/merges?library={shelf.LibraryId}", Cancel))
            Assert.AreEqual(HttpStatusCode.Forbidden, list.StatusCode);
        using (var undo = await reader.PostAsync($"/api/duplicates/merges/{log}/undo", null, Cancel))
            Assert.AreEqual(HttpStatusCode.Forbidden, undo.StatusCode);
        using (var split = await reader.PostAsJsonAsync($"/api/books/{a}/split", new DuplicateEndpoints.SplitInput([file]), Cancel))
            Assert.AreEqual(HttpStatusCode.Forbidden, split.StatusCode);
        using (var bulk = await reader.PostAsJsonAsync("/api/duplicates/merge", new DuplicateEndpoints.BulkMergeInput(shelf.LibraryId, null, "file"), Cancel))
            Assert.AreEqual(HttpStatusCode.Forbidden, bulk.StatusCode);
    }

    // ---- Helpers ----

    private async Task MergeAsync(HttpClient client, BookMergeInput input)
    {
        using var response = await client.PostAsJsonAsync("/api/books/merge", input, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
    }

    private async Task UndoAsync(HttpClient client, long log)
    {
        using var response = await client.PostAsync($"/api/duplicates/merges/{log}/undo", null, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
    }

    private async Task<long> LastMergeAsync(Shelf shelf) =>
        (await shelf.Admin.GetAsync<List<BookMergeInfo>>($"/api/duplicates/merges?library={shelf.LibraryId}", Cancel))[0].Id;

    private async Task<TaskRunInfo> BulkAsync(HttpClient client, DuplicateEndpoints.BulkMergeInput input)
    {
        using var started = await client.PostAsJsonAsync("/api/duplicates/merge", input, Cancel);
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

    private async Task<int> BookOfAsync(Shelf shelf, string path)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.BookFiles.Where(f => f.Path == path || f.Path.EndsWith("/" + path)).Select(f => f.BookId).SingleAsync(Cancel);
    }

    private async Task<int> LibraryCountAsync(Shelf shelf)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Libraries.Where(l => l.Id == shelf.LibraryId).Select(l => l.BookCount).SingleAsync(Cancel);
    }

    /// <summary>Every tag count as "tag:direct/total".</summary>
    private async Task<List<string>> TagCountsAsync(Shelf shelf)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return (await db.TagCounts.Where(c => c.BookCount > 0 || c.TotalCount > 0).Select(c => new { c.TagId, c.BookCount, c.TotalCount }).ToListAsync(Cancel))
            .Select(c => $"{c.TagId}:{c.BookCount}/{c.TotalCount}").ToList();
    }

    private async Task<List<string>> GroupsAsync(Shelf shelf)
    {
        await using var db = await shelf.DbAsync(Cancel);
        var groups = await db.DuplicateGroups.Where(g => !g.Noise).Select(g => new { g.Level, Members = g.Members.Select(m => m.BookId).ToList() }).ToListAsync(Cancel);
        return groups.Select(g => $"{g.Level}:{string.Join(',', g.Members.Order())}").ToList();
    }
}
