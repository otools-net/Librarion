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
/// The Imported tags page on both providers (stage 6 chunk 9, agents/docs/features/imported-tags.md): raw names
/// recorded by the importer, listed by books (read whole or walked by key), kept, mapped and ignored, an ignore undone,
/// re-imports respecting each, the rights.
/// </summary>
[TestClass]
public sealed class ImportedTagTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string keywords, string genre = "sf_fantasy", string author = "Ann Writer", string body = "") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info><genre>{genre}</genre><author><first-name>{author.Split(' ')[0]}</first-name><last-name>{author.Split(' ')[^1]}</last-name></author><book-title>{title}</book-title><keywords>{keywords}</keywords><lang>en</lang></title-info></description><body><p>{title}{body}</p></body></FictionBook>
        """;

    [TestMethod]
    public async Task TheImporterRecordsRawNamesButNotPeopleOrSeries()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", "Space Opera, dragons"));
        shelf.Write("two.fb2", Fb2("Two", "space  opera"));
        await shelf.ScanAsync(Cancel);
        shelf.Write("two.fb2", Fb2("Two", "space  opera", body: " read again"));
        await shelf.ScanAsync(Cancel);

        await using var db = await shelf.DbAsync(Cancel);
        var mappings = await db.ImportTagMappings.Select(m => new { m.Source, Field = m.FieldId == null ? null : db.Tags.Where(t => t.Id == m.FieldId).Select(t => t.FieldKey).First(), m.Name, m.NormalizedName, m.Reviewed })
            .ToListAsync(Cancel);
        var plain = mappings.Where(m => m.Field is null).OrderBy(m => m.NormalizedName).ToList();
        CollectionAssert.AreEqual(new[] { "dragons", "space opera" }, plain.Select(m => m.NormalizedName).ToList(), "one row per normalized name, a re-read adds none");
        Assert.AreEqual("Space Opera", plain[1].Name, "the name as first met");
        Assert.IsTrue(mappings.All(m => m.Source == ImportTagSourceKind.Fb2 && !m.Reviewed));
        Assert.IsTrue(mappings.Any(m => m.Field == "category"), "a genre is a raw name of its field");
        Assert.IsTrue(mappings.Any(m => m.Field == "language"));
        Assert.IsFalse(mappings.Any(m => m.Field == "author"), "people's names get no mapping");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "read whole")]
    [DataRow(true, DisplayName = "walked by key")]
    public async Task ThePageListsTagsByBooksAPageAtATime(bool walk)
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", "Alpha, Beta, Gamma"));
        shelf.Write("two.fb2", Fb2("Two", "Alpha, Beta"));
        shelf.Write("three.fb2", Fb2("Three", "Alpha"));
        await shelf.ScanAsync(Cancel);
        if (walk)
        {
            var service = shelf.Factory.Services.GetRequiredService<ImportedTags>();
            service.Small = 0;
            service.ChunkSize = 1;
        }

        var names = new List<string>();
        string? next = null;
        var pages = 0;
        do
        {
            var page = await shelf.Admin.GetAsync<Page<ImportedTag>>($"/api/imported-tags?library={shelf.LibraryId}&limit=2&after={next}", Cancel);
            names.AddRange(page.Items.Select(t => $"{t.Name}:{t.Books}"));
            next = page.Next;
            Assert.IsTrue(++pages < 20);
        } while (next is not null);

        CollectionAssert.AreEqual(new[] { "Alpha:3", "Beta:2", "Gamma:1" }, names);
        var summary = await shelf.Admin.GetAsync<ImportedSummary>($"/api/imported-tags/summary?library={shelf.LibraryId}", Cancel);
        Assert.AreEqual((3, 6L, 6L, 0L), (summary.Tags, summary.Links, summary.Top, summary.Reviewed));
        var category = await shelf.Admin.GetAsync<Page<ImportedTag>>($"/api/imported-tags?library={shelf.LibraryId}&field=category&source=fb2", Cancel);
        Assert.AreEqual(3, category.Items.Single().Books, "a field's values, of one source");
        var calibre = await shelf.Admin.GetAsync<Page<ImportedTag>>($"/api/imported-tags?library={shelf.LibraryId}&source=calibre", Cancel);
        Assert.AreEqual(0, calibre.Items.Count);
        var fields = await shelf.Admin.GetAsync<List<ImportedField>>("/api/imported-tags/fields", Cancel);
        Assert.AreEqual((null, 3), (fields[0].Key, fields[0].Tags));
        Assert.IsTrue(fields.Any(f => f.Key == "category") && fields.All(f => f.Key is not ("author" or "series")));
        using var people = await shelf.Admin.GetAsync($"/api/imported-tags?library={shelf.LibraryId}&field=author", Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, people.StatusCode);
    }

    [TestMethod]
    public async Task KeepMapAndIgnoreHoldThroughAReRead()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", "Alpha, Beta, Junk"));
        shelf.Write("two.fb2", Fb2("Two", "Alpha, Junk"));
        await shelf.ScanAsync(Cancel);
        var (alpha, beta, junk) = (await TagIdAsync(shelf, "Alpha"), await TagIdAsync(shelf, "Beta"), await TagIdAsync(shelf, "Junk"));
        await LinkAsync(shelf, "One", junk);

        var before = await shelf.Admin.GetAsync<ImportedSummary>($"/api/imported-tags/summary?library={shelf.LibraryId}", Cancel);
        using (var kept = await shelf.Admin.PostAsJsonAsync("/api/imported-tags/review", new ImportedTagEndpoints.ReviewInput([alpha]), Cancel))
            Assert.AreEqual(HttpStatusCode.OK, kept.StatusCode);
        var after = await shelf.Admin.GetAsync<ImportedSummary>($"/api/imported-tags/summary?library={shelf.LibraryId}", Cancel);
        Assert.AreEqual((0L, 2L), (before.Reviewed, after.Reviewed), "the kept coverage line is read again after a change");
        var mapped = await WorkAsync(await shelf.Admin.PostAsJsonAsync("/api/imported-tags/map", new ImportedTagEndpoints.MapInput([beta], alpha), Cancel));
        var ignored = await WorkAsync(await shelf.Admin.PostAsJsonAsync("/api/imported-tags/ignore", new ImportedTagEndpoints.TagsInput([junk]), Cancel));

        Assert.AreEqual(1, mapped.Links);
        Assert.AreEqual(2, ignored.Links, "the imported links only");
        Assert.AreEqual((2, 1), (await CountAsync(shelf, alpha), await CountAsync(shelf, junk)), "the manual link stays");
        var review = await ListAsync(shelf, "review");
        var done = await ListAsync(shelf, "reviewed");
        Assert.AreEqual(0, review.Count, "Alpha kept, Beta mapped onto it, Junk ignored");
        Assert.AreEqual("Alpha", done.Single().Name);
        CollectionAssert.AreEquivalent(new[] { "Alpha", "Beta" }, done.Single().Names.Select(n => n.Name).ToList(), "Beta's raw name now gives Alpha");

        shelf.Write("one.fb2", Fb2("One", "Alpha, Beta, Junk", body: " read again"));
        shelf.Write("two.fb2", Fb2("Two", "Alpha, Junk", body: " read again"));
        await shelf.ScanAsync(Cancel);
        Assert.AreEqual((2, 1), (await CountAsync(shelf, alpha), await CountAsync(shelf, junk)), "the re-read follows the mappings, the ignored name gives nothing");
        await using (var db = await shelf.DbAsync(Cancel))
            Assert.IsFalse(await db.BookTags.AnyAsync(l => l.TagId == junk && l.Source == TagSource.Imported, Cancel));

        var ignores = await shelf.Admin.GetAsync<List<IgnoredTagInfo>>("/api/imported-tags/ignored", Cancel);
        Assert.AreEqual(("Junk", 2L), (ignores.Single().TagName, ignores.Single().Links));
        CollectionAssert.AreEqual(new[] { "Junk" }, ignores.Single().Names.ToList());
        await WorkAsync(await shelf.Admin.PostAsync($"/api/imported-tags/ignored/{ignores.Single().Id}/undo", null, Cancel));

        Assert.AreEqual(2, await CountAsync(shelf, junk), "its imported links back (the manual one held a book already)");
        Assert.AreEqual("Junk", (await ListAsync(shelf, "review")).Single().Name, "unreviewed again");
        Assert.AreEqual(0, (await shelf.Admin.GetAsync<List<IgnoredTagInfo>>("/api/imported-tags/ignored", Cancel)).Count);
        using var again = await shelf.Admin.PostAsync($"/api/imported-tags/ignored/{ignores.Single().Id}/undo", null, Cancel);
        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
    }

    [TestMethod]
    public async Task AMergeReviewsTheNamesAndItsUndoDoesNot()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", "Alpha, Beta"));
        await shelf.ScanAsync(Cancel);
        var (alpha, beta) = (await TagIdAsync(shelf, "Alpha"), await TagIdAsync(shelf, "Beta"));

        var merge = await WorkAsync(await shelf.Admin.PostAsJsonAsync($"/api/tags/{beta}/merge", new TagEndpoints.MergeInput(alpha), Cancel));
        Assert.IsTrue(await ReviewedAsync(shelf, "beta"));
        await WorkAsync(await shelf.Admin.PostAsync($"/api/merges/{merge.LogId}/undo", null, Cancel));

        Assert.IsFalse(await ReviewedAsync(shelf, "beta"), "back to the importer's");
        Assert.AreEqual(beta, await TagIdAsync(shelf, "Beta"));
        using var notIgnore = await shelf.Admin.PostAsync($"/api/imported-tags/ignored/{merge.LogId}/undo", null, Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, notIgnore.StatusCode, "a merge isn't undone as an ignore");
    }

    [TestMethod]
    public async Task ManyTagsIgnoredInTheBackground()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", "Alpha, Beta"));
        shelf.Write("two.fb2", Fb2("Two", "Alpha"));
        await shelf.ScanAsync(Cancel);
        var (alpha, beta) = (await TagIdAsync(shelf, "Alpha"), await TagIdAsync(shelf, "Beta"));
        shelf.Factory.Services.GetRequiredService<TagService>().Threshold = 0;

        using var started = await shelf.Admin.PostAsJsonAsync("/api/imported-tags/ignore", new ImportedTagEndpoints.TagsInput([alpha, beta]), Cancel);
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode, await started.Content.ReadAsStringAsync(Cancel));
        var run = await WaitAsync(shelf.Admin, (await started.Content.ReadFromJsonAsync<TagWork>(ApiClients.Json, Cancel))!.RunId!.Value);

        Assert.AreEqual((TaskOutcome.Succeeded, BackgroundTaskKind.TagIgnore), (run.Outcome, run.Task), run.Summary);
        Assert.AreEqual(3, run.Counts!.Changed);
        Assert.AreEqual((0, 0), (await CountAsync(shelf, alpha), await CountAsync(shelf, beta)));
        using var refused = await shelf.Admin.PostAsJsonAsync("/api/imported-tags/ignore", new ImportedTagEndpoints.TagsInput([alpha]), Cancel);
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode, "no imported name left to ignore");
        StringAssert.Contains(await refused.Content.ReadAsStringAsync(Cancel), "tag.notImported");
    }

    [TestMethod]
    public async Task OnlyCuratorsSeeAndChangeThem()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", "Alpha"));
        await shelf.ScanAsync(Cancel);
        var alpha = await TagIdAsync(shelf, "Alpha");
        var groups = await shelf.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("curator", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Curators).Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(shelf.Factory, "reader", ApiClients.Password, Cancel);
        using var curator = await ApiClients.SignInAsync(shelf.Factory, "curator", ApiClients.Password, Cancel);

        using var refused = await reader.GetAsync($"/api/imported-tags?library={shelf.LibraryId}", Cancel);
        var context = await curator.GetAsync<ImportedTagEndpoints.ImportedContext>("/api/imported-tags/context", Cancel);
        using var kept = await curator.PostAsJsonAsync("/api/imported-tags/review", new ImportedTagEndpoints.ReviewInput([alpha]), Cancel);
        using var elsewhere = await curator.GetAsync($"/api/imported-tags?library={shelf.LibraryId + 100}", Cancel);

        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        CollectionAssert.AreEqual(new[] { shelf.LibraryId }, context.Libraries.ToList());
        Assert.IsTrue(context.CanChange && context.CanMap);
        Assert.AreEqual(HttpStatusCode.OK, kept.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, elsewhere.StatusCode);
    }

    // ---- Helpers ----

    private async Task<List<ImportedTag>> ListAsync(Shelf shelf, string status) =>
        (await shelf.Admin.GetAsync<Page<ImportedTag>>($"/api/imported-tags?library={shelf.LibraryId}&status={status}", Cancel)).Items.ToList();

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
        return await db.Tags.Where(t => t.Name == name && t.FieldId == null).Select(t => t.Id).SingleAsync(Cancel);
    }

    private async Task<bool> ReviewedAsync(Shelf shelf, string normalized)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.ImportTagMappings.Where(m => m.NormalizedName == normalized && m.FieldId == null).Select(m => m.Reviewed).SingleAsync(Cancel);
    }

    private async Task<int> CountAsync(Shelf shelf, int tagId)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.TagCounts.Where(c => c.TagId == tagId).SumAsync(c => c.BookCount, Cancel);
    }

    /// <summary>A link put on a book by hand, its count with it.</summary>
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
