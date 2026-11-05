using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace Librarion.Core.Imports;

/// <summary>An import from another source: its kind, the source's path on the server, and the folder of its books' files
/// (default: the source's folder).</summary>
public sealed record ImportInput(string? Kind, string? Source, string? Files = null);

/// <summary>An import checked and ready: its format, the source's full path, and the library's root holding its files.</summary>
public sealed record ImportRequest(ImportFormat Format, string Source, string Files, LibraryRootInfo Root, bool RootAdded);

/// <summary>
/// The checks of an import (agents/docs/features/imports.md), shared by the import route and the import schedules:
/// a known kind, an absolute source this account may read (server administrators anywhere, others under the
/// configured folders), a file the format accepts, and a files folder inside a root of the library (added as a root
/// when it lies in none, with the checks of adding one by hand).
/// </summary>
public sealed class ImportRequests(ImportFormats formats, LibraryRoots roots, BackgroundTasks tasks, ILogger<ImportRequests> logger)
{
    /// <returns>The request, or the field and error code that refuse it (a missing library: field "library").</returns>
    public async Task<(ImportRequest? Request, string? Field, string? Code)> CheckAsync(int tenantId, int libraryId, ImportInput input, bool anywhere,
        CancellationToken cancellationToken)
    {
        if (formats.Find(input.Kind) is not { } format)
            return (null, "kind", "kind.unknown");
        if (string.IsNullOrWhiteSpace(input.Source) || !Path.IsPathFullyQualified(input.Source.Trim()))
            return (null, "source", "source.notAbsolute");
        var source = LibraryRoots.Normalize(input.Source.Trim());
        if (!roots.MayUse(source, anywhere))
            return (null, "source", "source.notAllowed");
        if (!File.Exists(source))
            return (null, "source", "source.missing");
        if (format.Check(source) is { } why)
        {
            logger.LogInformation("Import source {Source} refused: {Why}", source, why);
            return (null, "source", "source.invalid");
        }

        var files = string.IsNullOrWhiteSpace(input.Files) ? Path.GetDirectoryName(source) : input.Files;
        var (problem, root, added) = await roots.RootForAsync(tenantId, libraryId, files, anywhere, cancellationToken);
        return problem switch
        {
            RootProblem.None => (new ImportRequest(format, source, LibraryRoots.Normalize(files!), root!, added), null, null),
            RootProblem.LibraryMissing => (null, "library", null),
            RootProblem.NotAbsolute => (null, "files", "files.notAbsolute"),
            RootProblem.Missing => (null, "files", "files.missing"),
            RootProblem.NotAllowed => (null, "files", "files.notAllowed"),
            _ => (null, "files", "files.overlaps"),
        };
    }

    /// <summary>Starts a checked import in the background.</summary>
    public Task<(long RunId, bool Started)> StartAsync(int tenantId, int libraryId, ImportRequest request, int? userId, CancellationToken cancellationToken) =>
        tasks.StartImportAsync(tenantId, libraryId, request.Root.Id, () => request.Format.Open(request.Source, request.Files), TaskTrigger.Manual, userId, null,
            cancellationToken);

    /// <summary>
    /// Starts the import of a schedule that came due. Checked again (the source may be gone), but its files folder must
    /// still be in a root of the library: a root removed since is not added back by a schedule.
    /// </summary>
    /// <returns>Why it can't start (logged and recorded as a failed run), or null once started.</returns>
    public async Task<string?> StartScheduledAsync(TaskSchedule schedule, CancellationToken cancellationToken)
    {
        if (formats.Find(schedule.ImportKind) is not { } format)
            return $"unknown import kind {schedule.ImportKind}";
        if (schedule.Source is not { } source || !File.Exists(source))
            return $"source not found: {schedule.Source}";
        if (format.Check(source) is { } why)
            return $"source refused: {why}";
        var files = schedule.Files ?? Path.GetDirectoryName(source)!;
        var root = (await roots.ListAsync(schedule.TenantId, schedule.LibraryId, cancellationToken) ?? [])
            .Where(r => LibraryRoots.IsWithin(files, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
        if (root is null || !root.Exists)
            return $"files folder not in the library: {files}";
        await tasks.StartImportAsync(schedule.TenantId, schedule.LibraryId, root.Id, () => format.Open(source, files), TaskTrigger.Schedule, null, schedule.Id,
            cancellationToken);
        return null;
    }
}
