namespace Librarion.Core.Data;

public enum BackgroundTaskKind : byte
{
    /// <summary>A library's roots walked, new and changed books read, files not found marked missing (never removed).</summary>
    LibraryScan,
    /// <summary>A root taken out of a library (the user confirmed): its files and the books left without one removed in batches.</summary>
    RemoveRoot,
    /// <summary>Missing books whose removal a user accepted, removed in batches.</summary>
    RemoveMissing,
    /// <summary>A manual import from another source (Calibre, INPX): its records written into the catalog.</summary>
    Import,
    /// <summary>A tag deleted with its links (in every library of the tenant: no library).</summary>
    TagDelete,
    /// <summary>A tag merged into another, its links moved in batches.</summary>
    TagMerge,
    /// <summary>A field merged into another: its values merged by name or moved.</summary>
    FieldMerge,
    /// <summary>A merge undone: the links moved back.</summary>
    MergeUndo,
    /// <summary>A tag with many books under it moved: the totals of the tags it left and joined counted again.</summary>
    TagCounts,
}

public enum TaskTrigger : byte
{
    Manual,
    Schedule,
}

public enum TaskOutcome : byte
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    /// <summary>The server stopped while it ran.</summary>
    Interrupted,
    /// <summary>A schedule came due while the same task was still running.</summary>
    Skipped,
}

/// <summary>One run of a background task (as SFE's <c>TaskRuns</c>): written at start, completed at the end.</summary>
public sealed class TaskRun
{
    public const int MaxSummaryLength = 2000;

    public long Id { get; set; }
    public int TenantId { get; set; }
    public int? LibraryId { get; set; }
    public BackgroundTaskKind Task { get; set; }
    public TaskTrigger Trigger { get; set; }

    /// <summary>The schedule that started it; not a foreign key, the run outlives its schedule.</summary>
    public int? ScheduleId { get; set; }

    /// <summary>The account that started it by hand.</summary>
    public int? UserId { get; set; }

    /// <summary>UTC.</summary>
    public DateTime Started { get; set; }

    /// <summary>UTC; null while it runs.</summary>
    public DateTime? Finished { get; set; }

    public TaskOutcome Outcome { get; set; }

    /// <summary>What it did, for people ("1 204 books added, 3 archives skipped"); never a path of the server.</summary>
    public string? Summary { get; set; }

    /// <summary>Counts as JSON (added, changed, removed, failed, skipped…), shown per run.</summary>
    public string? Counts { get; set; }
}

public enum ScheduleKind : byte
{
    /// <summary>Every <see cref="TaskSchedule.IntervalMinutes"/> from <see cref="TaskSchedule.Start"/>.</summary>
    Interval,
    /// <summary>At <see cref="TaskSchedule.Start"/>'s time of day on <see cref="TaskSchedule.Days"/> (every day: all seven).</summary>
    Weekly,
}

/// <summary>When a task runs by itself (as SFE's <c>TaskSchedules</c>).</summary>
public sealed class TaskSchedule
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int LibraryId { get; set; }
    public Library? Library { get; set; }
    /// <summary>What it runs: a scan of the library, or an import (with <see cref="ImportKind"/>, <see cref="Source"/>, <see cref="Files"/>).</summary>
    public BackgroundTaskKind Task { get; set; }
    public ScheduleKind Kind { get; set; }

    /// <summary>An import's format key (<c>calibre</c>, <c>inpx</c>).</summary>
    public string? ImportKind { get; set; }

    /// <summary>An import's source, a full path on the server (checked when saved, again when it runs).</summary>
    public string? Source { get; set; }

    /// <summary>The folder of an import's files, inside a root of the library.</summary>
    public string? Files { get; set; }

    /// <summary>UTC.</summary>
    public DateTime Start { get; set; }

    public int IntervalMinutes { get; set; }

    /// <summary>Days of a weekly schedule: bit 0 = Sunday … bit 6 = Saturday.</summary>
    public int Days { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Created or last changed (UTC): occurrences before it are not due (a new schedule doesn't run for the past).</summary>
    public DateTime Updated { get; set; }

    /// <summary>Last time it came due (whether its task could start or not).</summary>
    public DateTime? LastRun { get; set; }
}
