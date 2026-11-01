using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Librarion.Core.Tasks;

/// <summary>A schedule as the client sends it: <see cref="Start"/> in UTC (its time of day, in the server's zone, for weekly ones).</summary>
public sealed record ScheduleInput(ScheduleKind Kind, DateTime Start, int IntervalMinutes, int Days, bool Enabled = true);

public sealed record ScheduleInfo(int Id, int LibraryId, ScheduleKind Kind, DateTime Start, int IntervalMinutes, int Days, bool Enabled, DateTime? LastRun, DateTime? NextRun);

/// <summary>When each library is scanned by itself (as SFE's schedules, agents/docs/features/tasks.md).</summary>
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

    /// <returns>Null when the library isn't there; a problem key when the input is wrong.</returns>
    public async Task<(string? Problem, ScheduleInfo? Schedule)> SaveAsync(int tenantId, int libraryId, int? scheduleId, ScheduleInput input, CancellationToken cancellationToken)
    {
        if (Check(input) is { } problem)
            return (problem, null);
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
            row = new TaskSchedule { TenantId = tenantId, LibraryId = libraryId, Task = BackgroundTaskKind.LibraryScan };
            db.TaskSchedules.Add(row);
        }

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

    private static string? Check(ScheduleInput input) => input.Kind switch
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
        return new ScheduleInfo(row.Id, row.LibraryId, row.Kind, row.Start, row.IntervalMinutes, row.Days, row.Enabled, row.LastRun, next);
    }
}

/// <summary>
/// Marks the runs a stopped server left as interrupted, then starts what came due (checked every
/// <see cref="Period"/>, never for the past: occurrences before a schedule's last change don't count; missed ones
/// aren't caught up); due while the library's run still goes → a skipped run. Stops the running tasks when the server
/// stops.
/// </summary>
public sealed class TaskScheduleWorker(TenantDb tenantDb, BackgroundTasks tasks, TimeProvider time, ILogger<TaskScheduleWorker> logger) : BackgroundService
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
            var (_, started) = await tasks.StartScanAsync(schedule.TenantId, schedule.LibraryId, TaskTrigger.Schedule, null, schedule.Id, cancellationToken);
            if (!started)
                await tasks.RecordSkippedAsync(schedule.TenantId, schedule.LibraryId, schedule.Id, cancellationToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await tasks.StopAsync(TimeSpan.FromSeconds(5));
        await base.StopAsync(cancellationToken);
    }
}
