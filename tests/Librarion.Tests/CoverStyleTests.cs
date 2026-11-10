using System.Net;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Covers.Generated;
using Librarion.Core.Data;
using Librarion.Core.Tasks;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Librarion.Tests;

/// <summary>
/// Administration › Catalog › Cover styles, on both providers (stage 6 chunk 10b, agents/docs/features/book-display.md#cover-styles):
/// templates turned off aren't given to new books, one stays on, a template's books given another by a run (a style
/// chosen on a book's page stays), the previews, the rights.
/// </summary>
[TestClass]
public sealed class CoverStyleTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info><author><first-name>Ann</first-name><last-name>Writer</last-name></author><book-title>{title}</book-title><lang>en</lang></title-info></description><body><p>{title}</p></body></FictionBook>
        """;

    [TestMethod]
    public void BooksGetOnlyTemplatesThatAreOn()
    {
        for (long identity = 0; identity < 2000; identity++)
            Assert.AreEqual(GeneratedCovers.Assign(identity), GeneratedCovers.Assign(identity, 0), "every template on: a book keeps its look");

        var off = (1 << 0) | (1 << 3);
        var given = Enumerable.Range(0, 3000).Select(i => GeneratedCovers.Assign(i * 7919L, off)).ToList();
        CollectionAssert.AreEquivalent(new short[] { 1, 2, 4 }, given.Select(g => g.Template).Distinct().ToList());
        foreach (var count in given.GroupBy(g => g.Template).Select(g => g.Count()))
            Assert.IsTrue(Math.Abs(count - 1000) < 200, $"spread evenly ({count} of 3000)");
        Assert.AreEqual(GeneratedCovers.Palettes.Count, given.Select(g => g.Variant).Distinct().Count());
        CollectionAssert.AreEqual(Enumerable.Range(0, GeneratedCovers.Templates.Count).ToList(), GeneratedCovers.On(-1).ToList(), "none on reads as all");
    }

    [TestMethod]
    public async Task NewBooksGetOnlyTemplatesOnAndOneStaysOn()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        foreach (var template in new[] { 0, 1, 2, 4 })
            await SetAsync(shelf.Admin, template, false);

        using var last = await shelf.Admin.PutAsJsonAsync("/api/covers/templates/3", new CoverEndpoints.TemplateInput(false), Cancel);
        Assert.AreEqual(HttpStatusCode.BadRequest, last.StatusCode);
        StringAssert.Contains(await last.Content.ReadAsStringAsync(Cancel), "coverStyle.lastOn");
        using var unknown = await shelf.Admin.PutAsJsonAsync("/api/covers/templates/9", new CoverEndpoints.TemplateInput(true), Cancel);
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);

        var list = await shelf.Admin.GetAsync<CoverEndpoints.CoverTemplates>("/api/covers/templates", Cancel);
        CollectionAssert.AreEqual(new[] { false, false, false, true, false }, list.Templates.Select(t => t.On).ToList());
        Assert.AreEqual(GeneratedCovers.Palettes.Count, list.Palettes);
        var styles = await shelf.Admin.GetAsync<CoverEndpoints.CoverStyles>("/api/covers/styles", Cancel);
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 4 }, styles.Off.ToList(), "the book page learns which are off");

        for (var i = 0; i < 12; i++)
            shelf.Write($"b{i}.fb2", Fb2($"Book {i}"));
        await shelf.ScanAsync(Cancel);

        await using var db = await shelf.DbAsync(Cancel);
        CollectionAssert.AreEqual(new short?[] { 3 }, await db.Books.Select(b => b.GeneratedTemplate).Distinct().ToListAsync(Cancel));
    }

    [TestMethod]
    public async Task AReassignGivesATemplatesBooksAnotherAndLeavesChosenStyles()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        for (var i = 0; i < 20; i++)
            shelf.Write($"b{i}.fb2", Fb2($"Book {i}"));
        await shelf.ScanAsync(Cancel);
        var books = await TemplatesAsync(shelf);
        var template = books.GroupBy(b => b.Value).OrderByDescending(g => g.Count()).First().Key;
        var moving = books.Where(b => b.Value == template).Select(b => b.Key).ToList();
        // One book given the template by hand: its style stays.
        var chosen = books.First(b => b.Value != template).Key;
        using (var style = await shelf.Admin.PutAsJsonAsync($"/api/books/{chosen}/cover/style", new CoverEndpoints.StyleInput(template, 1), Cancel))
            Assert.AreEqual(HttpStatusCode.OK, style.StatusCode);

        using (var on = await shelf.Admin.PostAsync($"/api/covers/templates/{template}/reassign", null, Cancel))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, on.StatusCode, "only a template turned off");
            StringAssert.Contains(await on.Content.ReadAsStringAsync(Cancel), "coverStyle.on");
        }

        Assert.AreEqual(template, (await TemplatesAsync(shelf))[moving[0]], "turning off changes no book");
        await SetAsync(shelf.Admin, template, false);
        using var started = await shelf.Admin.PostAsync($"/api/covers/templates/{template}/reassign", null, Cancel);
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode);
        var run = await WaitAsync(shelf.Admin, (await started.Content.ReadFromJsonAsync<CoverEndpoints.ReassignStarted>(ApiClients.Json, Cancel))!.RunId);

        Assert.AreEqual((TaskOutcome.Succeeded, BackgroundTaskKind.CoverReassign, moving.Count), (run.Outcome, run.Task, run.Counts!.Changed), run.Summary);
        var after = await TemplatesAsync(shelf);
        Assert.IsTrue(moving.All(id => after[id] != template), "its books have another");
        Assert.AreEqual(template, after[chosen], "a chosen style stays");
        Assert.IsTrue(books.Where(b => b.Value != template && b.Key != chosen).All(b => after[b.Key] == b.Value), "other books are left");
    }

    [TestMethod]
    public async Task PreviewsDrawASampleOfEachStyle()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);

        using (var preview = SKBitmap.Decode(await shelf.Admin.GetByteArrayAsync("/api/covers/templates/2/preview/5?title=A%20title&author=Someone", Cancel)))
            Assert.AreEqual((240, 360), (preview.Width, preview.Height));
        using var none = await shelf.Admin.GetAsync("/api/covers/templates/2/preview/99", Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, none.StatusCode);
    }

    [TestMethod]
    public async Task OnlyThoseWhoManageCoverStylesChangeThem()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        var groups = await shelf.Admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        await ApiClients.CreateUserAsync(shelf.Admin, new NewUser("curator", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Curators).Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(shelf.Factory, "reader", ApiClients.Password, Cancel);
        using var curator = await ApiClients.SignInAsync(shelf.Factory, "curator", ApiClients.Password, Cancel);

        using var read = await reader.GetAsync("/api/covers/templates", Cancel);
        using var refused = await reader.PutAsJsonAsync("/api/covers/templates/1", new CoverEndpoints.TemplateInput(false), Cancel);
        Assert.AreEqual((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (read.StatusCode, refused.StatusCode));

        await SetAsync(curator, 1, false);
        using var started = await curator.PostAsync("/api/covers/templates/1/reassign", null, Cancel);
        var run = (await started.Content.ReadFromJsonAsync<CoverEndpoints.ReassignStarted>(ApiClients.Json, Cancel))!.RunId;
        Assert.AreEqual(BackgroundTaskKind.CoverReassign, (await WaitAsync(curator, run)).Task, "the curator follows its run");
    }

    // ---- Helpers ----

    private async Task SetAsync(HttpClient client, int template, bool on)
    {
        using var response = await client.PutAsJsonAsync($"/api/covers/templates/{template}", new CoverEndpoints.TemplateInput(on), Cancel);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
    }

    private async Task<Dictionary<int, short>> TemplatesAsync(TestLibrary shelf)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Books.ToDictionaryAsync(b => b.Id, b => b.GeneratedTemplate!.Value, Cancel);
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
