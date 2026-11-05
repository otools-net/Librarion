using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Imports;
using Librarion.Core.Tasks;

namespace Librarion.Api.Endpoints;

public sealed record RootInput(string? Path);

public sealed record TaskStarted(long RunId, bool Started);

/// <summary>Which missing books to remove: chosen ones, those past the retention, or all of them (one of the three).</summary>
public sealed record MissingRemovalInput(IReadOnlyList<int>? BookIds = null, bool Ready = false, bool All = false);

/// <summary>
/// A library's roots (<see cref="Rights.ManageLibraries"/>; another tenant's: <see cref="Rights.ManageTenants"/>) and
/// its scans, runs and schedules (<see cref="Rights.Scan"/> in that library). A library the caller can't act on is not
/// found (agents/docs/features/tasks.md).
/// </summary>
public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this IEndpointRouteBuilder api)
    {
        var roots = api.MapGroup("/libraries/{library:int}/roots").WithTags("Libraries").RequireRight(Rights.ManageLibraries, Rights.ManageTenants);

        roots.MapGet("/", async (int library, int? tenant, HttpContext context, CurrentAccess current, LibraryRoots service) =>
                ApiResults.TargetTenant(await current.GetAsync(), tenant, Rights.ManageLibraries, Rights.ManageTenants) is { } tenantId
                && await service.ListAsync(tenantId, library, context.RequestAborted) is { } list
                    ? Results.Ok(list)
                    : Results.NotFound())
            .WithName("ListLibraryRoots")
            .WithSummary("The folders a library's books are found under, and whether each is there now.");

        roots.MapPost("/", async (int library, int? tenant, RootInput input, HttpContext context, CurrentAccess current, LibraryRoots service, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                if (ApiResults.TargetTenant(state, tenant, Rights.ManageLibraries, Rights.ManageTenants) is not { } tenantId)
                    return Results.NotFound();
                var (problem, root) = await service.AddAsync(tenantId, library, input.Path, state.Access.IsServerAdmin, context.RequestAborted);
                if (problem == RootProblem.LibraryMissing)
                    return Results.NotFound();
                if (problem != RootProblem.None)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["path"] = [Code(problem)] });
                await log.LogAsync(context, state, SecurityEventKind.LibraryChanged, tenantId, $"root added: {root!.Path}");
                return Results.Ok(root);
            })
            .WithName("AddLibraryRoot")
            .WithSummary("Adds a folder to a library: absolute, existing, not inside or around another root; tenant administrators only under the configured `Librarion:Libraries:Folders`.");

        roots.MapDelete("/{root:int}", async (int library, int root, int? tenant, HttpContext context, CurrentAccess current, LibraryRoots service, BackgroundTasks tasks, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                if (ApiResults.TargetTenant(state, tenant, Rights.ManageLibraries, Rights.ManageTenants) is not { } tenantId
                    || await service.ListAsync(tenantId, library, context.RequestAborted) is not { } list || list.FirstOrDefault(r => r.Id == root) is not { } found)
                    return Results.NotFound();
                var (runId, _) = await tasks.StartRemoveRootAsync(tenantId, library, root, state.User?.Id, context.RequestAborted);
                await log.LogAsync(context, state, SecurityEventKind.LibraryChanged, tenantId, $"root removed: {found.Path}");
                return Results.Accepted(value: new TaskStarted(runId, true));
            })
            .WithName("RemoveLibraryRoot")
            .WithSummary("Takes a folder out of a library: its books go in a background task (the run's id is answered).");

        var library = api.MapGroup("/libraries/{library:int}").WithTags("Tasks").RequireRight(Rights.Scan);

        library.MapPost("/scan", async (int library, HttpContext context, CurrentAccess current, BackgroundTasks tasks) =>
            {
                if (await ScanTenant(current, library) is not { } state)
                    return Results.NotFound();
                var (runId, started) = await tasks.StartScanAsync(state.TenantId!.Value, library, TaskTrigger.Manual, state.User?.Id, null, context.RequestAborted);
                return Results.Accepted(value: new TaskStarted(runId, started));
            })
            .WithName("ScanLibrary")
            .WithSummary("Scans the library's roots now; when a scan already runs there, its run is answered (`started` false).");

        library.MapGet("/tasks", async (int library, long? before, int? limit, HttpContext context, CurrentAccess current, BackgroundTasks tasks) =>
                await ScanTenant(current, library) is { } state && await tasks.ListAsync(state.TenantId!.Value, library, before, limit ?? 20, context.RequestAborted) is { } runs
                    ? Results.Ok(runs)
                    : Results.NotFound())
            .WithName("ListLibraryTasks")
            .WithSummary("The library's task runs, newest first, with the running one's progress; `before` = the last id of the page before.");

        library.MapGet("/missing", async (int library, string? after, int? limit, HttpContext context, CurrentAccess current, MissingBooks missing) =>
                await ScanTenant(current, library) is { } state
                    ? Results.Ok(await missing.ListAsync(state.TenantId!.Value, library, after, limit ?? 50, context.RequestAborted))
                    : Results.NotFound())
            .WithName("ListMissingBooks")
            .WithSummary("Books whose files the scans no longer find, oldest first, kept until a file comes back or their removal is accepted.")
            .WithDescription("`ready` marks the ones missing longer than the library's `library.keepMissingDays` (offered for removal first); `total` and `ready` count the library's missing books.");

        library.MapPost("/missing/remove", async (int library, MissingRemovalInput input, HttpContext context, CurrentAccess current, MissingBooks missing,
                BackgroundTasks tasks, SecurityLog log, TimeProvider time) =>
            {
                if (await ScanTenant(current, library) is not { } state)
                    return Results.NotFound();
                var tenantId = state.TenantId!.Value;
                MissingRemoval removal;
                string what;
                if (input.BookIds is { Count: > 0 } ids)
                    (removal, what) = (new MissingRemoval(ids.Take(MissingBooks.BatchSize).ToList(), null), $"{Math.Min(ids.Count, MissingBooks.BatchSize)} chosen");
                else if (input.Ready)
                {
                    var days = await missing.KeepDaysAsync(tenantId, library, context.RequestAborted);
                    (removal, what) = (new MissingRemoval(null, time.GetUtcNow().UtcDateTime.AddDays(-days)), $"missing over {days} days");
                }
                else if (input.All)
                    (removal, what) = (new MissingRemoval(null, null), "all");
                else
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["removal"] = ["removal.nothing"] });

                var (runId, _) = await tasks.StartRemoveMissingAsync(tenantId, library, removal, state.User?.Id, context.RequestAborted);
                await log.LogAsync(context, state, SecurityEventKind.LibraryChanged, tenantId, $"missing books removed: {what}");
                return Results.Accepted(value: new TaskStarted(runId, true));
            })
            .WithName("RemoveMissingBooks")
            .WithSummary("Removes missing books whose removal the user accepted, in the background: `bookIds` (up to 1000), `ready` (missing longer than the retention) or `all`.")
            .WithDescription("Books that came back in the meantime are not removed. Nothing is ever removed without this call: the retention only decides what is offered first.");

        var import = api.MapGroup("/libraries/{library:int}/import").WithTags("Tasks").RequireRight(Rights.Import);

        import.MapGet("/kinds", async (int library, CurrentAccess current, ImportFormats formats) =>
                await RightTenant(current, library, Rights.Import) is not null ? Results.Ok(formats.Keys.Order().ToList()) : Results.NotFound())
            .WithName("ListImportKinds")
            .WithSummary("The kinds of source a library can import from (`calibre`, `inpx`).");

        import.MapPost("/", async (int library, ImportInput input, HttpContext context, CurrentAccess current, ImportRequests imports, SecurityLog log) =>
            {
                if (await RightTenant(current, library, Rights.Import) is not { } state)
                    return Results.NotFound();
                var tenantId = state.TenantId!.Value;
                var (request, field, code) = await imports.CheckAsync(tenantId, library, input, state.Access.IsServerAdmin, context.RequestAborted);
                if (request is null)
                    return field == "library" ? Results.NotFound() : Invalid(field!, code!);
                if (request.RootAdded)
                    await log.LogAsync(context, state, SecurityEventKind.LibraryChanged, tenantId, $"root added: {request.Root.Path}");
                var (runId, _) = await imports.StartAsync(tenantId, library, request, state.User?.Id, context.RequestAborted);
                await log.LogAsync(context, state, SecurityEventKind.LibraryChanged, tenantId, $"import from {request.Format.Key}: {request.Source}");
                return Results.Accepted(value: new TaskStarted(runId, true));
            })
            .WithName("ImportIntoLibrary")
            .WithSummary("Imports a library of another program (`kind`: `calibre` with its `metadata.db`, `inpx` with its `.inpx` file) into the library, in the background.")
            .WithDescription("What the source says is stored in Librarion's database; the source is read once and never written. `files` is the folder of its books' files "
                + "(default: the source's folder), added to the library as a folder when it lies in none. Running it again brings in what changed; it never removes a book.");

        library.MapGet("/schedules", async (int library, HttpContext context, CurrentAccess current, TaskSchedules schedules) =>
                await ScanTenant(current, library) is { } state && await schedules.ListAsync(state.TenantId!.Value, library, context.RequestAborted) is { } list
                    ? Results.Ok(list)
                    : Results.NotFound())
            .WithName("ListLibrarySchedules")
            .WithSummary("What the library does by itself (scans, imports), and when.");

        library.MapPost("/schedules", (int library, ScheduleInput input, HttpContext context, CurrentAccess current, TaskSchedules schedules, ImportRequests imports,
                SecurityLog log) =>
                SaveAsync(library, null, input, context, current, schedules, imports, log))
            .WithName("AddLibrarySchedule")
            .WithSummary("Adds a schedule: `task` `libraryScan` (default) or `import` (with `importKind`, `source` and `files`, checked as `POST …/import`, the "
                + "right Import needed), every `intervalMinutes` (at least 15) from `start`, or weekly on `days` (bit 0 = Sunday) at `start`'s time of day.");

        library.MapPut("/schedules/{schedule:int}", (int library, int schedule, ScheduleInput input, HttpContext context, CurrentAccess current, TaskSchedules schedules,
                ImportRequests imports, SecurityLog log) =>
                SaveAsync(library, schedule, input, context, current, schedules, imports, log))
            .WithName("ChangeLibrarySchedule")
            .WithSummary("Changes a schedule (its past occurrences don't run).");

        library.MapDelete("/schedules/{schedule:int}", async (int library, int schedule, HttpContext context, CurrentAccess current, TaskSchedules schedules) =>
                await ScanTenant(current, library) is { } state && await schedules.DeleteAsync(state.TenantId!.Value, library, schedule, context.RequestAborted)
                    ? Results.NoContent()
                    : Results.NotFound())
            .WithName("DeleteLibrarySchedule")
            .WithSummary("Deletes a schedule.");

        // Tag runs (deletes, merges, undos) belong to no library: those who manage tags follow them.
        // Those who tag books follow the runs of their libraries too (scope tagging), and cancel only those.
        var runs = api.MapGroup("/tasks").WithTags("Tasks").RequireRight(Rights.Scan, Rights.ManageTags, Rights.TagBooks);

        runs.MapGet("/running", async (HttpContext context, CurrentAccess current, BackgroundTasks tasks) =>
                await current.GetAsync() is { TenantId: { } tenantId } state
                    ? Results.Ok(await tasks.RunningAsync(tenantId, FollowedLibraries(state), context.RequestAborted, state.Has(Rights.ManageTags)))
                    : Results.NotFound())
            .WithName("ListRunningTasks")
            .WithSummary("The runs going on (or waiting) in the libraries the caller may scan, and the tenant's tag runs for those who manage tags, with their progress: what the progress panel follows.");

        runs.MapGet("/overview", async (HttpContext context, CurrentAccess current, BackgroundTasks tasks, TaskSchedules schedules) =>
            {
                if (await current.GetAsync() is not { TenantId: { } tenantId } state)
                    return Results.NotFound();
                var libraries = ScannableLibraries(state);
                var last = new List<TaskRunInfo>();
                foreach (var kind in Enum.GetValues<BackgroundTaskKind>())
                    if (await tasks.LastAsync(tenantId, libraries, kind, context.RequestAborted) is { } run)
                        last.Add(run);
                return Results.Ok(new TaskOverview(await tasks.RunningAsync(tenantId, libraries, context.RequestAborted), last,
                    await schedules.ListAsync(tenantId, libraries, context.RequestAborted), libraries.Order().ToList()));
            })
            .WithName("GetTaskOverview")
            .WithSummary("The Background tasks page: the runs going on, the last finished run of each kind, and the schedules, in the libraries the caller may scan.");

        runs.MapGet("/", async (long? before, int? limit, HttpContext context, CurrentAccess current, BackgroundTasks tasks) =>
                await current.GetAsync() is { TenantId: { } tenantId } state
                    ? Results.Ok(await tasks.HistoryAsync(tenantId, FollowedLibraries(state), before, limit ?? 20, context.RequestAborted, state.Has(Rights.ManageTags)))
                    : Results.NotFound())
            .WithName("ListTasks")
            .WithSummary("The task runs of every library the caller may scan, newest first; `before` = the last id of the page before.");

        runs.MapGet("/{run:long}", async (long run, HttpContext context, CurrentAccess current, BackgroundTasks tasks) =>
                await RunOf(run, context, current, tasks) is { } info ? Results.Ok(info) : Results.NotFound())
            .WithName("GetTask")
            .WithSummary("One task run, with its progress while it runs.");

        runs.MapPost("/{run:long}/cancel", async (long run, HttpContext context, CurrentAccess current, BackgroundTasks tasks) =>
                await RunOf(run, context, current, tasks) is { } info && await MayCancelAsync(info, current)
                    && tasks.Cancel((await current.GetAsync()).TenantId!.Value, info.Id)
                    ? Results.Accepted()
                    : Results.NotFound())
            .WithName("CancelTask")
            .WithSummary("Asks a running task to stop; it ends as cancelled, keeping what it wrote.");
    }

    private static async Task<IResult> SaveAsync(int library, int? schedule, ScheduleInput input, HttpContext context, CurrentAccess current, TaskSchedules schedules,
        ImportRequests imports, SecurityLog log)
    {
        if (await ScanTenant(current, library) is not { } state)
            return Results.NotFound();
        var tenantId = state.TenantId!.Value;
        ScheduledImport? import = null;
        if (input.Task == BackgroundTaskKind.Import)
        {
            if (!state.Access.HasIn(Rights.Import, library))
                return Results.NotFound();
            var (request, field, code) = await imports.CheckAsync(tenantId, library, new ImportInput(input.ImportKind, input.Source, input.Files),
                state.Access.IsServerAdmin, context.RequestAborted);
            if (request is null)
                return field == "library" ? Results.NotFound() : Invalid(field!, code!);
            if (request.RootAdded)
                await log.LogAsync(context, state, SecurityEventKind.LibraryChanged, tenantId, $"root added: {request.Root.Path}");
            import = new ScheduledImport(request.Format.Key, request.Source, request.Files);
        }

        var (problem, saved) = await schedules.SaveAsync(tenantId, library, schedule, input, import, context.RequestAborted);
        return problem switch
        {
            null => Results.Ok(saved),
            "library" or "schedule" => Results.NotFound(),
            _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { [problem] = [problem switch
            {
                "intervalMinutes" => "intervalMinutes.invalid",
                "days" => "days.invalid",
                "task" => "task.invalid",
                _ => "kind.invalid",
            }] }),
        };
    }

    /// <summary>The caller's state when it may scan in <paramref name="library"/> of its own tenant.</summary>
    private static Task<AccessState?> ScanTenant(CurrentAccess current, int library) => RightTenant(current, library, Rights.Scan);

    /// <summary>The caller's state when it has <paramref name="right"/> in <paramref name="library"/> of its own tenant.</summary>
    private static async Task<AccessState?> RightTenant(CurrentAccess current, int library, string right)
    {
        var state = await current.GetAsync();
        return state.TenantId is not null && state.Access.HasIn(right, library) ? state : null;
    }

    private static IReadOnlyCollection<int> ScannableLibraries(AccessState state) => state.Access.LibrariesWith(Rights.Scan).ToHashSet();

    /// <summary>The libraries whose runs the caller follows: those it may scan or tag books in.</summary>
    private static IReadOnlyCollection<int> FollowedLibraries(AccessState state) =>
        state.Access.LibrariesWith(Rights.Scan).Concat(state.Access.LibrariesWith(Rights.TagBooks)).ToHashSet();

    /// <summary>A library's run is cancelled by those who may scan there; a scope tagging also by those who tag there.</summary>
    private static async Task<bool> MayCancelAsync(TaskRunInfo info, CurrentAccess current)
    {
        var state = await current.GetAsync();
        return info.LibraryId is not { } library || state.Access.HasIn(Rights.Scan, library)
            || info.Task == BackgroundTaskKind.ScopeTags && state.Access.HasIn(Rights.TagBooks, library);
    }

    private static IResult Invalid(string field, string code) => TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [code] });

    private static async Task<TaskRunInfo?> RunOf(long run, HttpContext context, CurrentAccess current, BackgroundTasks tasks)
    {
        var state = await current.GetAsync();
        if (state.TenantId is not { } tenantId || await tasks.GetAsync(tenantId, run, context.RequestAborted) is not { } info)
            return null;
        return info.LibraryId is { } library
            ? state.Access.HasIn(Rights.Scan, library) || state.Access.HasIn(Rights.TagBooks, library) ? info : null
            : state.Has(Rights.ManageTags) ? info : null;
    }

    private static string Code(RootProblem problem) => problem switch
    {
        RootProblem.NotAbsolute => "path.notAbsolute",
        RootProblem.Missing => "path.missing",
        RootProblem.NotAllowed => "path.notAllowed",
        _ => "path.overlaps",
    };
}
