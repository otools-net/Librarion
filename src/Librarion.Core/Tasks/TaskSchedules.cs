using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Librarion.Core.Tasks;

/// <summary>
/// A schedule as the client sends it: what it runs (<see cref="Task"/>: a scan, or an import with its kind, source and
/// files folder), then when: <see cref="Start"/> in UTC (its time of day, in the server's zone, for weekly ones).
/// </summary>
public sealed record ScheduleInput(ScheduleKind Kind, DateTime Start, int IntervalMinutes, int Days, bool Enabled = true,
    BackgroundTaskKind Task = BackgroundTaskKind.LibraryScan, string? ImportKind = null, string? Source = null, string? Files = null);

public sealed record ScheduleInfo(int Id, int LibraryId, BackgroundTaskKind Task, string? ImportKind, string? Source, string? Files, ScheduleKind Kind,
    DateTime Start, int IntervalMinutes, int Days, bool Enabled, DateTime? LastRun, DateTime? NextRun);

/// <summary>A checked import of a schedule: what <see cref="TaskSchedules.SaveAsync"/> stores.</summary>
public sealed record ScheduledImport(string Kind, string Source, string Files);

/// <summary>What each library does by itself, and when: scans and imports (as SFE's schedules, agents/docs/features/tasks.md).</summary>
public sealed class TaskSchedules(TenantDb tenantDb, TimeProvider time)
{
    /// <summary>Shortest interval: a scan of an unchanged library is cheap, but not free.</summary>
    public const int MinIntervalMinutes = 15;

    public async Task<IReadOnlyList<ScheduleInfo>?> ListAsync(int tenantId, int libraryId, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (!await db.Libraries.AnyAsync(l => l.Id == libraryId, cancellationToken))
            return null;
        var rows = await db.TaskSchedules.Where(s => s.LibraryId == libraryId).OrderBy(s => s.Id).ToListAsync(cancellationToken);
        return rows.Select(Info).ToList();
    }

    /// <summary>The schedules of <paramref name="libraryIds"/> (the Background tasks page: every library the caller may scan).</summary>
    public async Task<IReadOnlyList<ScheduleInfo>> ListAsync(int tenantId, IReadOnlyCollection<int> libraryIds, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var rows = await db.TaskSchedules.Where(s => libraryIds.Contains(s.LibraryId)).OrderBy(s => s.Id).ToListAsync(cancellationToken);
        return rows.Select(Info).ToList();
    }

    /// <param name="import">An import schedule's source, checked by <c>ImportRequests</c> (null for a scan).</param>
    /// <returns>Null when the library isn't there; a problem key when the input is wrong.</returns>
    public async Task<(string? Problem, ScheduleInfo? Schedule)> SaveAsync(int tenantId, int libraryId, int? scheduleId, ScheduleInput input, ScheduledImport? import,
        CancellationToken cancellationToken)
    {
        if (Check(input) is { } problem)
            return (problem, null);
        if (input.Task == BackgroundTaskKind.Import && import is null)
            return ("source", null);
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (!await db.Libraries.AnyAsync(l => l.Id == libraryId, cancellationToken))
            return ("library", null);
        TaskSchedule? row;
        if (scheduleId is { } id)
        {
            row = await db.TaskSchedules.SingleOrDefaultAsync(s => s.Id == id && s.LibraryId == libraryId, cancellationToken);
            if (row is null)
                return ("schedule", null);
        }
        else
        {
            row = new TaskSchedule { TenantId = tenantId, LibraryId = libraryId };
            db.TaskSchedules.Add(row);
        }

        row.Task = input.Task;
        row.ImportKind = input.Task == BackgroundTaskKind.Import ? import!.Kind : null;
        row.Source = input.Task == BackgroundTaskKind.Import ? import!.Source : null;
        row.Files = input.Task == BackgroundTaskKind.Import ? import!.Files : null;

        row.Kind = input.Kind;
        row.Start = DateTime.SpecifyKind(input.Start, DateTimeKind.Utc);
        row.IntervalMinutes = input.Kind == ScheduleKind.Interval ? input.IntervalMinutes : 0;
        row.Days = input.Kind == ScheduleKind.Weekly ? input.Days & ScheduleCalculator.AllDays : 0;
        row.Enabled = input.Enabled;
        row.Updated = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return (null, Info(row));
    }

    public async Task<bool> DeleteAsync(int tenantId, int libraryId, int scheduleId, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        return await db.TaskSchedules.Where(s => s.Id == scheduleId && s.LibraryId == libraryId).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    private static string? Check(ScheduleInput input) => input.Task is not (BackgroundTaskKind.LibraryScan or BackgroundTaskKind.Import) ? "task" : input.Kind switch
    {
        ScheduleKind.Interval when input.IntervalMinutes < MinIntervalMinutes || input.IntervalMinutes > 60 * 24 * 31 => "intervalMinutes",
        ScheduleKind.Weekly when (input.Days & ScheduleCalculator.AllDays) == 0 => "days",
        ScheduleKind.Interval or ScheduleKind.Weekly => null,
        _ => "kind",
    };

    private ScheduleInfo Info(TaskSchedule row)
    {
        var after = row.LastRun is { } last && last > row.Updated ? last : row.Updated;
        var now = time.GetUtcNow().UtcDateTime;
        var next = row.Enabled ? ScheduleCalculator.Next(row.Kind, row.Start, row.IntervalMinutes, row.Days, after > now ? after : now, TimeZoneInfo.Local) : null;
        return new ScheduleInfo(row.Id, row.LibraryId, row.Task, row.ImportKind, row.Source, row.Files, row.Kind, row.Start, row.IntervalMinutes, row.Days,
            row.Enabled, row.LastRun, next);
    }
}

/// <summary>
/// Marks the runs a stopped server left as interrupted, then starts what came due (checked every
/// <see cref="Period"/>, never for the past: occurrences before a schedule's last change don't count; missed ones
/// aren't caught up); due while the library's run still goes → a skipped run. Stops the running tasks when the server
/// stops.
/// </summary>
public sealed class TaskScheduleWorker(TenantDb tenantDb, BackgroundTasks tasks, Imports.ImportRequests imports, TimeProvider time, ILogger<TaskScheduleWorker> logger)
    : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await tasks.RecoverAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Interrupted task runs could not be marked");
        }

        var last = time.GetUtcNow().UtcDateTime;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Period, time, stoppingToken);
                var now = time.GetUtcNow().UtcDateTime;
                await RunDueAsync(last, now, stoppingToken);
                last = now;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Task schedules could not be checked");
            }
        }
    }

    /// <summary>Starts every enabled schedule with an occurrence in (<paramref name="since"/>, <paramref name="now"/>].</summary>
    public async Task RunDueAsync(DateTime since, DateTime now, CancellationToken cancellationToken)
    {
        List<TaskSchedule> schedules;
        await using (var db = await tenantDb.ForServerAsync(cancellationToken))
            schedules = await db.TaskSchedules.Where(s => s.Enabled).ToListAsync(cancellationToken);
        foreach (var schedule in schedules)
        {
            var after = new[] { since, schedule.Updated, schedule.LastRun ?? DateTime.MinValue }.Max();
            if (ScheduleCalculator.Next(schedule.Kind, schedule.Start, schedule.IntervalMinutes, schedule.Days, after, TimeZoneInfo.Local) is not { } due || due > now)
                continue;
            await using (var db = await tenantDb.ForTenantAsync(schedule.TenantId, cancellationToken))
                await db.TaskSchedules.Where(s => s.Id == schedule.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastRun, now), cancellationToken);
            if (schedule.Task == BackgroundTaskKind.Import)
            {
                // One run per library: a due import doesn't queue behind a scan, it is skipped as a scan would be.
                if (tasks.RunningIn(schedule.TenantId, schedule.LibraryId) is not null)
                    await tasks.RecordUnstartedAsync(schedule, TaskOutcome.Skipped, null, cancellationToken);
                else if (await imports.StartScheduledAsync(schedule, cancellationToken) is { } problem)
                {
                    logger.LogWarning("Scheduled import {Schedule} of library {Library} could not start: {Problem}", schedule.Id, schedule.LibraryId, problem);
                    await tasks.RecordUnstartedAsync(schedule, TaskOutcome.Failed, problem, cancellationToken);
                }
                continue;
            }
            var (_, started) = await tasks.StartScanAsync(schedule.TenantId, schedule.LibraryId, TaskTrigger.Schedule, null, schedule.Id, cancellationToken);
            if (!started)
                await tasks.RecordUnstartedAsync(schedule, TaskOutcome.Skipped, null, cancellationToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await tasks.StopAsync(TimeSpan.FromSeconds(5));
        await base.StopAsync(cancellationToken);
    }
}
