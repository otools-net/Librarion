using System.Collections.Concurrent;
using System.Text.Json;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Librarion.Core.Tasks;

/// <summary>A run as the tasks page shows it; <paramref name="Progress"/> while it runs.</summary>
public sealed record TaskRunInfo(
    long Id, int? LibraryId, BackgroundTaskKind Task, TaskTrigger Trigger, DateTime Started, DateTime? Finished, TaskOutcome Outcome,
    string? Summary, ScanCounts? Counts, ScanProgress? Progress, bool Waiting);

/// <summary>What a finished run did, kept with it.</summary>
/// <param name="Missing">Books that went missing (kept until a file comes back or a user accepts their removal).</param>
/// <param name="Returned">Missing books that have a file again (found where it was, or moved).</param>
public sealed record ScanCounts(int Added, int Changed, int Removed, int Unchanged, int ArchivesRead, int ArchivesSkipped, int Unreadable,
    IReadOnlyList<string> MissingRoots, IReadOnlyList<string> Problems, int Missing = 0, int Returned = 0);

/// <summary>
/// Library scans and root removals in the background (as SFE's <c>BackgroundTasks</c> and <c>TaskHistory</c>): one
/// run per library at a time, at most <see cref="LibraryOptions.MaxParallelScans"/> on the server (more wait), each a
/// <see cref="TaskRun"/> row written at its start and completed at its end; cancelled on request or when the server
/// stops; rows still running at start-up become <see cref="TaskOutcome.Interrupted"/>.
/// </summary>
public sealed class BackgroundTasks : IDisposable
{
    private const int MaxStoredProblems = 20;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TenantDb tenantDb;
    private readonly LibraryScanner scanner;
    private readonly MissingBooks missingBooks;
    private readonly TimeProvider time;
    private readonly ILogger<BackgroundTasks> logger;
    private readonly SemaphoreSlim slots;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<long, Active> active = new();
    private readonly Lock starting = new();

    public BackgroundTasks(TenantDb tenantDb, LibraryScanner scanner, MissingBooks missingBooks, TimeProvider time, IOptions<LibraryOptions> options, ILogger<BackgroundTasks> logger)
    {
        this.tenantDb = tenantDb;
        this.scanner = scanner;
        this.missingBooks = missingBooks;
        this.time = time;
        this.logger = logger;
        slots = new SemaphoreSlim(Math.Max(1, options.Value.MaxParallelScans));
    }

    private sealed class Active(long id, int tenantId, int libraryId, CancellationTokenSource cancel)
    {
        public long Id => id;
        public int TenantId => tenantId;
        public int LibraryId => libraryId;
        public CancellationTokenSource Cancel => cancel;
        public volatile ScanProgress? Progress;
        public volatile bool Waiting = true;
        public Task Work = Task.CompletedTask;
    }

    /// <summary>The running (or waiting) run of a library.</summary>
    public long? RunningIn(int tenantId, int libraryId) => active.Values.FirstOrDefault(a => a.TenantId == tenantId && a.LibraryId == libraryId)?.Id;

    /// <summary>Starts a scan of the library, or answers the run already going there (<c>Started</c> false).</summary>
    public Task<(long RunId, bool Started)> StartScanAsync(int tenantId, int libraryId, TaskTrigger trigger, int? userId, int? scheduleId, CancellationToken cancellationToken) =>
        StartAsync(tenantId, libraryId, BackgroundTaskKind.LibraryScan, trigger, userId, scheduleId,
            (progress, cancel) => scanner.ScanAsync(tenantId, libraryId, progress, cancel), cancellationToken);

    /// <summary>Starts taking a root out of its library (waits for a scan of the library to end first: one run per library).</summary>
    public Task<(long RunId, bool Started)> StartRemoveRootAsync(int tenantId, int libraryId, int rootId, int? userId, CancellationToken cancellationToken) =>
        StartAsync(tenantId, libraryId, BackgroundTaskKind.RemoveRoot, TaskTrigger.Manual, userId, null,
            (progress, cancel) => scanner.RemoveRootAsync(tenantId, rootId, progress, cancel), cancellationToken);

    /// <summary>Starts removing the missing books a user accepted (waits for a scan of the library to end first).</summary>
    public Task<(long RunId, bool Started)> StartRemoveMissingAsync(int tenantId, int libraryId, MissingRemoval removal, int? userId, CancellationToken cancellationToken) =>
        StartAsync(tenantId, libraryId, BackgroundTaskKind.RemoveMissing, TaskTrigger.Manual, userId, null,
            (progress, cancel) => missingBooks.RemoveAsync(tenantId, libraryId, removal, progress, cancel), cancellationToken);

    /// <summary>A schedule came due while the library's run was still going: recorded, not started.</summary>
    public async Task RecordSkippedAsync(int tenantId, int libraryId, int scheduleId, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        db.TaskRuns.Add(new TaskRun
        {
            TenantId = tenantId, LibraryId = libraryId, Task = BackgroundTaskKind.LibraryScan, Trigger = TaskTrigger.Schedule, ScheduleId = scheduleId,
            Started = now, Finished = now, Outcome = TaskOutcome.Skipped,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<(long RunId, bool Started)> StartAsync(int tenantId, int libraryId, BackgroundTaskKind kind, TaskTrigger trigger, int? userId, int? scheduleId,
        Func<IProgress<ScanProgress>, CancellationToken, Task<ScanResult>> work, CancellationToken cancellationToken)
    {
        if (kind == BackgroundTaskKind.LibraryScan && RunningIn(tenantId, libraryId) is { } running)
            return (running, false);
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var run = new TaskRun
        {
            TenantId = tenantId, LibraryId = libraryId, Task = kind, Trigger = trigger, UserId = userId, ScheduleId = scheduleId,
            Started = time.GetUtcNow().UtcDateTime, Outcome = TaskOutcome.Running,
        };
        db.TaskRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        Active entry;
        lock (starting)
        {
            if (kind == BackgroundTaskKind.LibraryScan && RunningIn(tenantId, libraryId) is { } raced)
            {
                // Another start won between the check and the insert: this row is closed as skipped.
                _ = CompleteAsync(tenantId, run.Id, TaskOutcome.Skipped, null, null);
                return (raced, false);
            }

            entry = new Active(run.Id, tenantId, libraryId, CancellationTokenSource.CreateLinkedTokenSource(stopping.Token));
            active[run.Id] = entry;
        }

        entry.Work = Task.Run(() => RunAsync(entry, kind, work), CancellationToken.None);
        return (run.Id, true);
    }

    private async Task RunAsync(Active entry, BackgroundTaskKind kind, Func<IProgress<ScanProgress>, CancellationToken, Task<ScanResult>> work)
    {
        var outcome = TaskOutcome.Failed;
        ScanResult? result = null;
        string? summary = null;
        try
        {
            // A removal waits for the library's scan to end; every run waits for a free slot.
            while (kind is BackgroundTaskKind.RemoveRoot or BackgroundTaskKind.RemoveMissing && active.Values.Any(a => a.LibraryId == entry.LibraryId && a.TenantId == entry.TenantId && a.Id < entry.Id))
                await Task.Delay(500, entry.Cancel.Token);
            await slots.WaitAsync(entry.Cancel.Token);
            try
            {
                entry.Waiting = false;
                result = await work(new Progress(entry), entry.Cancel.Token);
                outcome = TaskOutcome.Succeeded;
            }
            finally
            {
                slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            outcome = stopping.IsCancellationRequested ? TaskOutcome.Interrupted : TaskOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Task {Run} ({Task}) of library {Library} failed", entry.Id, kind, entry.LibraryId);
            summary = "The task stopped on an error; the server's log has the details.";
        }
        finally
        {
            active.TryRemove(entry.Id, out _);
            entry.Cancel.Dispose();
        }

        await CompleteAsync(entry.TenantId, entry.Id, outcome, summary, result);
    }

    private async Task CompleteAsync(int tenantId, long runId, TaskOutcome outcome, string? summary, ScanResult? result)
    {
        try
        {
            await using var db = await tenantDb.ForTenantAsync(tenantId, CancellationToken.None);
            var counts = result is null ? null : JsonSerializer.Serialize(new ScanCounts(result.Added, result.Changed, result.Removed, result.Unchanged,
                result.ArchivesRead, result.ArchivesSkipped, result.Unreadable, result.MissingRoots, result.Problems.Take(MaxStoredProblems).ToList(),
                result.Missing, result.Returned), Json);
            if (counts is { Length: > TaskRun.MaxSummaryLength })
                counts = JsonSerializer.Serialize(new ScanCounts(result!.Added, result.Changed, result.Removed, result.Unchanged, result.ArchivesRead,
                    result.ArchivesSkipped, result.Unreadable, result.MissingRoots.Take(5).ToList(), [], result.Missing, result.Returned), Json);
            var finished = time.GetUtcNow().UtcDateTime;
            await db.TaskRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Outcome, outcome).SetProperty(r => r.Finished, finished)
                .SetProperty(r => r.Summary, summary).SetProperty(r => r.Counts, counts), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Task {Run} could not be recorded as {Outcome}", runId, outcome);
        }
    }

    /// <summary>Asks a run to stop; false when it isn't running (or belongs to another tenant).</summary>
    public bool Cancel(int tenantId, long runId)
    {
        if (!active.TryGetValue(runId, out var entry) || entry.TenantId != tenantId)
            return false;
        entry.Cancel.Cancel();
        return true;
    }

    /// <summary>The library's runs, newest first, a page at a time by key (<paramref name="before"/>: the last id seen).</summary>
    public async Task<IReadOnlyList<TaskRunInfo>?> ListAsync(int tenantId, int libraryId, long? before, int limit, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (!await db.Libraries.AnyAsync(l => l.Id == libraryId, cancellationToken))
            return null;
        var query = db.TaskRuns.Where(r => r.LibraryId == libraryId);
        if (before is { } id)
            query = query.Where(r => r.Id < id);
        var rows = await query.OrderByDescending(r => r.Id).Take(Math.Clamp(limit, 1, 100)).ToListAsync(cancellationToken);
        return rows.Select(Info).ToList();
    }

    public async Task<TaskRunInfo?> GetAsync(int tenantId, long runId, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        return await db.TaskRuns.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken) is { } row ? Info(row) : null;
    }

    private TaskRunInfo Info(TaskRun row)
    {
        active.TryGetValue(row.Id, out var entry);
        var counts = row.Counts is null ? null : JsonSerializer.Deserialize<ScanCounts>(row.Counts, Json);
        return new TaskRunInfo(row.Id, row.LibraryId, row.Task, row.Trigger, row.Started, row.Finished, row.Outcome, row.Summary, counts, entry?.Progress,
            entry?.Waiting ?? false);
    }

    /// <summary>Runs left <see cref="TaskOutcome.Running"/> by a server that stopped: interrupted (all tenants, at start-up).</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var running = active.Keys.ToList();
        await db.TaskRuns.Where(r => r.Outcome == TaskOutcome.Running && !running.Contains(r.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Outcome, TaskOutcome.Interrupted).SetProperty(r => r.Finished, now), cancellationToken);
    }

    /// <summary>Every run asked to stop, and waited for a little (the server is stopping).</summary>
    public async Task StopAsync(TimeSpan wait)
    {
        stopping.Cancel();
        await Task.WhenAny(Task.WhenAll(active.Values.Select(a => a.Work)), Task.Delay(wait));
    }

    public void Dispose()
    {
        stopping.Cancel();
        stopping.Dispose();
        slots.Dispose();
    }

    private sealed class Progress(Active entry) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => entry.Progress = value;
    }
}
