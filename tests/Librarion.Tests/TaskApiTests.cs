using System.Net;
using System.Net.Http.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Tasks;
using Librarion.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>
/// Roots, scans, runs and schedules through the API (agents/docs/features/tasks.md): who may add which folder, a scan
/// started and followed to its end, a root taken out with its books, schedules that start scans.
/// </summary>
[TestClass]
public sealed class TaskApiTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    [TestMethod]
    public async Task AScanStartedThroughTheApiBringsTheRootsBooksAndRemovingTheRootTakesThemOut()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        var (admin, session) = await ApiClients.SetUpAsync(factory, Cancel);
        var library = (await admin.GetAsync<List<LibraryInfo>>("/api/libraries", Cancel)).Single();
        var root = Path.Combine(catalog.BasePath, "books");
        Directory.CreateDirectory(root);
        TestZips.Zip(root, "fb2.zip", ("a.fb2", "a"), ("b.fb2", "b"));
        File.WriteAllText(Path.Combine(root, "c.epub"), "not really an epub");

        using (var added = await admin.PostAsJsonAsync($"/api/libraries/{library.Id}/roots", new RootInput(root + Path.DirectorySeparatorChar), ApiClients.Json, Cancel))
            Assert.AreEqual(HttpStatusCode.OK, added.StatusCode, await added.Content.ReadAsStringAsync(Cancel));
        var roots = await admin.GetAsync<List<LibraryRootInfo>>($"/api/libraries/{library.Id}/roots", Cancel);
        Assert.AreEqual(root, roots.Single().Path, "stored without the trailing separator");
        Assert.IsTrue(roots.Single().Exists);
        using (var inside = await admin.PostAsJsonAsync($"/api/libraries/{library.Id}/roots", new RootInput(Path.Combine(root, "sub")), ApiClients.Json, Cancel))
            Assert.AreEqual(HttpStatusCode.BadRequest, inside.StatusCode, "a folder that doesn't exist (and would be inside the root)");

        var run = await StartScanAsync(admin, library.Id);
        Assert.IsTrue(run.Started);
        var done = await WaitAsync(admin, run.RunId);
        Assert.AreEqual(TaskOutcome.Succeeded, done.Outcome);
        Assert.AreEqual(3, done.Counts!.Added);
        Assert.AreEqual(TaskTrigger.Manual, done.Trigger);
        var books = await admin.GetAsync<Page<BookListItem>>("/api/books", Cancel);
        Assert.HasCount(3, books.Items);
        var history = await admin.GetAsync<List<TaskRunInfo>>($"/api/libraries/{library.Id}/tasks", Cancel);
        Assert.AreEqual(run.RunId, history.Single().Id);

        using (var removed = await admin.DeleteAsync($"/api/libraries/{library.Id}/roots/{roots.Single().Id}", Cancel))
        {
            Assert.AreEqual(HttpStatusCode.Accepted, removed.StatusCode);
            var removal = (await removed.Content.ReadFromJsonAsync<TaskStarted>(ApiClients.Json, Cancel))!;
            Assert.AreEqual(TaskOutcome.Succeeded, (await WaitAsync(admin, removal.RunId)).Outcome);
        }

        Assert.IsEmpty((await admin.GetAsync<Page<BookListItem>>("/api/books", Cancel)).Items);
        Assert.IsEmpty(await admin.GetAsync<List<LibraryRootInfo>>($"/api/libraries/{library.Id}/roots", Cancel));
        await using var db = await factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(session.Tenant!.Id, Cancel);
        Assert.AreEqual(0, db.Libraries.Single().BookCount);
        Assert.IsFalse(db.TagCounts.Any());
    }

    [TestMethod]
    public async Task TenantAdministratorsAddRootsOnlyUnderTheConfiguredFolders()
    {
        using var catalog = new TestCatalog();
        var allowed = Path.Combine(catalog.BasePath, "shared");
        var elsewhere = Path.Combine(catalog.BasePath, "private");
        Directory.CreateDirectory(Path.Combine(allowed, "family"));
        Directory.CreateDirectory(elsewhere);
        await using var factory = ApiClients.CreateFactory(catalog, settings => settings["Librarion:Libraries:Folders:0"] = allowed);
        var (admin, _) = await ApiClients.SetUpAsync(factory, Cancel);
        var groups = await admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel);
        await ApiClients.CreateUserAsync(admin, new NewUser("keeper", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Administrators).Id]), Cancel);
        using var keeper = await ApiClients.SignInAsync(factory, "keeper", ApiClients.Password, Cancel);
        var library = (await keeper.GetAsync<List<LibraryInfo>>("/api/libraries", Cancel)).Single();

        async Task<string?> AddAsync(HttpClient client, string path)
        {
            using var response = await client.PostAsJsonAsync($"/api/libraries/{library.Id}/roots", new RootInput(path), ApiClients.Json, Cancel);
            if (response.IsSuccessStatusCode)
                return null;
            var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>(ApiClients.Json, Cancel);
            return problem!.Errors["path"].Single();
        }

        Assert.AreEqual("notAllowed", await AddAsync(keeper, elsewhere));
        Assert.AreEqual("notAllowed", await AddAsync(keeper, Path.Combine(allowed, "..", "private")), "the full path is what counts");
        Assert.AreEqual("notAbsolute", await AddAsync(keeper, "shared/family"));
        Assert.IsNull(await AddAsync(keeper, Path.Combine(allowed, "family")));
        Assert.AreEqual("overlaps", await AddAsync(admin, allowed), "around a root of the tenant");
        Assert.IsNull(await AddAsync(admin, elsewhere), "a server administrator: anywhere");

        // Users of the tenant don't scan.
        await ApiClients.CreateUserAsync(admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [groups.Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(factory, "reader", ApiClients.Password, Cancel);
        using var refused = await reader.PostAsync($"/api/libraries/{library.Id}/scan", null, Cancel);
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        using var scanned = await keeper.PostAsync($"/api/libraries/{library.Id + 1000}/scan", null, Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, scanned.StatusCode, "a library the caller can't scan in");
    }

    [TestMethod]
    public async Task SchedulesAreCheckedAndADueOneStartsAScanOrRecordsASkip()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        var (admin, session) = await ApiClients.SetUpAsync(factory, Cancel);
        var library = (await admin.GetAsync<List<LibraryInfo>>("/api/libraries", Cancel)).Single();
        var start = DateTime.UtcNow.AddMinutes(-90);

        using (var tooOften = await admin.PostAsJsonAsync($"/api/libraries/{library.Id}/schedules", new ScheduleInput(ScheduleKind.Interval, start, 5, 0), ApiClients.Json, Cancel))
            Assert.AreEqual(HttpStatusCode.BadRequest, tooOften.StatusCode);
        using (var noDays = await admin.PostAsJsonAsync($"/api/libraries/{library.Id}/schedules", new ScheduleInput(ScheduleKind.Weekly, start, 0, 0), ApiClients.Json, Cancel))
            Assert.AreEqual(HttpStatusCode.BadRequest, noDays.StatusCode);
        ScheduleInfo hourly;
        using (var saved = await admin.PostAsJsonAsync($"/api/libraries/{library.Id}/schedules", new ScheduleInput(ScheduleKind.Interval, start, 60, 0), ApiClients.Json, Cancel))
            hourly = (await saved.Content.ReadFromJsonAsync<ScheduleInfo>(ApiClients.Json, Cancel))!;
        Assert.IsNotNull(hourly.NextRun);
        Assert.IsGreaterThan(DateTime.UtcNow, hourly.NextRun.Value, "the past doesn't run");

        var worker = factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<TaskScheduleWorker>().Single();
        var now = DateTime.UtcNow;
        await worker.RunDueAsync(now, now.AddMinutes(1), Cancel);
        Assert.IsEmpty(await admin.GetAsync<List<TaskRunInfo>>($"/api/libraries/{library.Id}/tasks", Cancel), "not due within the minute");

        var due = hourly.NextRun!.Value;
        await worker.RunDueAsync(due.AddSeconds(-30), due.AddSeconds(1), Cancel);
        var runs = await admin.GetAsync<List<TaskRunInfo>>($"/api/libraries/{library.Id}/tasks", Cancel);
        Assert.AreEqual(TaskTrigger.Schedule, runs.Single().Trigger);
        await WaitAsync(admin, runs.Single().Id);
        var listed = (await admin.GetAsync<List<ScheduleInfo>>($"/api/libraries/{library.Id}/schedules", Cancel)).Single();
        Assert.IsNotNull(listed.LastRun);

        using (var deleted = await admin.DeleteAsync($"/api/libraries/{library.Id}/schedules/{hourly.Id}", Cancel))
            Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.IsEmpty(await admin.GetAsync<List<ScheduleInfo>>($"/api/libraries/{library.Id}/schedules", Cancel));

        // While a run goes, a due schedule is recorded as skipped.
        var tasks = factory.Services.GetRequiredService<BackgroundTasks>();
        await tasks.RecordSkippedAsync(session.Tenant!.Id, library.Id, 99, Cancel);
        Assert.AreEqual(TaskOutcome.Skipped, (await admin.GetAsync<List<TaskRunInfo>>($"/api/libraries/{library.Id}/tasks", Cancel))[0].Outcome);
    }

    [TestMethod]
    public void IntervalAndWeeklySchedulesComeDueWhenTheySay()
    {
        var start = new DateTime(2026, 11, 2, 3, 30, 0, DateTimeKind.Utc); // a Monday
        Assert.AreEqual(start, ScheduleCalculator.Next(ScheduleKind.Interval, start, 60, 0, start.AddMinutes(-1), TimeZoneInfo.Utc));
        Assert.AreEqual(start.AddHours(3), ScheduleCalculator.Next(ScheduleKind.Interval, start, 60, 0, start.AddHours(2).AddMinutes(5), TimeZoneInfo.Utc));
        const int mondayAndFriday = (1 << 1) | (1 << 5);
        Assert.AreEqual(start.AddDays(4), ScheduleCalculator.Next(ScheduleKind.Weekly, start, 0, mondayAndFriday, start, TimeZoneInfo.Utc), "strictly after");
        Assert.IsNull(ScheduleCalculator.Next(ScheduleKind.Weekly, start, 0, 0, start, TimeZoneInfo.Utc));
    }

    private async Task<TaskStarted> StartScanAsync(HttpClient client, int library)
    {
        using var response = await client.PostAsync($"/api/libraries/{library}/scan", null, Cancel);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
        return (await response.Content.ReadFromJsonAsync<TaskStarted>(ApiClients.Json, Cancel))!;
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
