using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Tasks;

namespace Librarion.Api.Endpoints;

public sealed record RootInput(string? Path);

public sealed record TaskStarted(long RunId, bool Started);

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

        library.MapGet("/schedules", async (int library, HttpContext context, CurrentAccess current, TaskSchedules schedules) =>
                await ScanTenant(current, library) is { } state && await schedules.ListAsync(state.TenantId!.Value, library, context.RequestAborted) is { } list
                    ? Results.Ok(list)
                    : Results.NotFound())
            .WithName("ListLibrarySchedules")
            .WithSummary("When the library is scanned by itself.");

        library.MapPost("/schedules", (int library, ScheduleInput input, HttpContext context, CurrentAccess current, TaskSchedules schedules) =>
                SaveAsync(library, null, input, context, current, schedules))
            .WithName("AddLibrarySchedule")
            .WithSummary("Adds a schedule: every `intervalMinutes` (at least 15) from `start`, or weekly on `days` (bit 0 = Sunday) at `start`'s time of day.");

        library.MapPut("/schedules/{schedule:int}", (int library, int schedule, ScheduleInput input, HttpContext context, CurrentAccess current, TaskSchedules schedules) =>
                SaveAsync(library, schedule, input, context, current, schedules))
            .WithName("ChangeLibrarySchedule")
            .WithSummary("Changes a schedule (its past occurrences don't run).");

        library.MapDelete("/schedules/{schedule:int}", async (int library, int schedule, HttpContext context, CurrentAccess current, TaskSchedules schedules) =>
                await ScanTenant(current, library) is { } state && await schedules.DeleteAsync(state.TenantId!.Value, library, schedule, context.RequestAborted)
                    ? Results.NoContent()
                    : Results.NotFound())
            .WithName("DeleteLibrarySchedule")
            .WithSummary("Deletes a schedule.");

        var runs = api.MapGroup("/tasks").WithTags("Tasks").RequireRight(Rights.Scan);

        runs.MapGet("/{run:long}", async (long run, HttpContext context, CurrentAccess current, BackgroundTasks tasks) =>
                await RunOf(run, context, current, tasks) is { } info ? Results.Ok(info) : Results.NotFound())
            .WithName("GetTask")
            .WithSummary("One task run, with its progress while it runs.");

        runs.MapPost("/{run:long}/cancel", async (long run, HttpContext context, CurrentAccess current, BackgroundTasks tasks) =>
                await RunOf(run, context, current, tasks) is { } info && tasks.Cancel((await current.GetAsync()).TenantId!.Value, info.Id)
                    ? Results.Accepted()
                    : Results.NotFound())
            .WithName("CancelTask")
            .WithSummary("Asks a running task to stop; it ends as cancelled, keeping what it wrote.");
    }

    private static async Task<IResult> SaveAsync(int library, int? schedule, ScheduleInput input, HttpContext context, CurrentAccess current, TaskSchedules schedules)
    {
        if (await ScanTenant(current, library) is not { } state)
            return Results.NotFound();
        var (problem, saved) = await schedules.SaveAsync(state.TenantId!.Value, library, schedule, input, context.RequestAborted);
        return problem switch
        {
            null => Results.Ok(saved),
            "library" or "schedule" => Results.NotFound(),
            _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { [problem] = [problem switch
            {
                "intervalMinutes" => "intervalMinutes.invalid",
                "days" => "days.invalid",
                _ => "kind.invalid",
            }] }),
        };
    }

    /// <summary>The caller's state when it may scan in <paramref name="library"/> of its own tenant.</summary>
    private static async Task<AccessState?> ScanTenant(CurrentAccess current, int library)
    {
        var state = await current.GetAsync();
        return state.TenantId is not null && state.Access.HasIn(Rights.Scan, library) ? state : null;
    }

    private static async Task<TaskRunInfo?> RunOf(long run, HttpContext context, CurrentAccess current, BackgroundTasks tasks)
    {
        var state = await current.GetAsync();
        if (state.TenantId is not { } tenantId || await tasks.GetAsync(tenantId, run, context.RequestAborted) is not { } info)
            return null;
        return info.LibraryId is { } library && state.Access.HasIn(Rights.Scan, library) ? info : null;
    }

    private static string Code(RootProblem problem) => problem switch
    {
        RootProblem.NotAbsolute => "path.notAbsolute",
        RootProblem.Missing => "path.missing",
        RootProblem.NotAllowed => "path.notAllowed",
        _ => "path.overlaps",
    };
}
