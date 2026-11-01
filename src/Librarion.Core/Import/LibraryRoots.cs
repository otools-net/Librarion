using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Librarion.Core.Import;

/// <summary><c>Librarion:Libraries</c>: where libraries may take their books from, and how many scans run at once.</summary>
public sealed class LibraryOptions
{
    public const string SectionName = "Librarion:Libraries";

    /// <summary>
    /// Folders under which tenant administrators may add roots. Empty (the default): only server administrators add
    /// roots, anywhere the server can read (a tenant administrator choosing any folder of the server could read every
    /// other tenant's books, or the server's own files).
    /// </summary>
    public List<string> Folders { get; set; } = [];

    /// <summary>Scans running at once on the server; more wait their turn.</summary>
    public int MaxParallelScans { get; set; } = 2;
}

public sealed record LibraryRootInfo(int Id, int LibraryId, string Path, DateTime Created, bool Exists);

public enum RootProblem
{
    None,
    NotAbsolute,
    Missing,
    NotAllowed,
    Overlaps,
    LibraryMissing,
}

/// <summary>
/// The roots of a library (agents/docs/features/archives.md, libraries): absolute folders, checked to exist, never
/// inside or around another root of the tenant (a book would be two books), and for tenant administrators only under
/// <see cref="LibraryOptions.Folders"/>.
/// </summary>
public sealed class LibraryRoots(TenantDb tenantDb, IOptionsMonitor<LibraryOptions> options, TimeProvider time)
{
    public async Task<IReadOnlyList<LibraryRootInfo>?> ListAsync(int tenantId, int libraryId, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (!await db.Libraries.AnyAsync(l => l.Id == libraryId, cancellationToken))
            return null;
        return (await db.LibraryRoots.Where(r => r.LibraryId == libraryId).OrderBy(r => r.Path).ToListAsync(cancellationToken))
            .Select(r => new LibraryRootInfo(r.Id, r.LibraryId, r.Path, r.Created, Directory.Exists(r.Path))).ToList();
    }

    /// <param name="anywhere">A server administrator: any folder the server can read.</param>
    public async Task<(RootProblem Problem, LibraryRootInfo? Root)> AddAsync(int tenantId, int libraryId, string? path, bool anywhere, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
            return (RootProblem.NotAbsolute, null);
        var full = Normalize(path.Trim());
        if (full.Length > LibraryRoot.MaxPathLength)
            return (RootProblem.NotAbsolute, null);
        if (!MayUse(full, anywhere))
            return (RootProblem.NotAllowed, null);
        if (!Directory.Exists(full))
            return (RootProblem.Missing, null);

        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (!await db.Libraries.AnyAsync(l => l.Id == libraryId, cancellationToken))
            return (RootProblem.LibraryMissing, null);
        var others = await db.LibraryRoots.Select(r => r.Path).ToListAsync(cancellationToken);
        if (others.Any(other => IsWithin(full, other) || IsWithin(other, full)))
            return (RootProblem.Overlaps, null);
        var root = new LibraryRoot { TenantId = tenantId, LibraryId = libraryId, Path = full, Created = time.GetUtcNow().UtcDateTime };
        db.LibraryRoots.Add(root);
        await db.SaveChangesAsync(cancellationToken);
        return (RootProblem.None, new LibraryRootInfo(root.Id, libraryId, full, root.Created, true));
    }

    /// <summary>Whether a tenant administrator (or, <paramref name="anywhere"/>, a server administrator) may use the full path.</summary>
    public bool MayUse(string full, bool anywhere) => anywhere || options.CurrentValue.Folders.Any(folder => IsWithin(full, Normalize(folder)));

    /// <summary>
    /// The root of the library <paramref name="path"/> lies in, added (with <see cref="AddAsync"/>'s checks) when it lies
    /// in none: an import's files become the library's (agents/docs/features/imports.md). <c>Added</c> when it is new.
    /// </summary>
    public async Task<(RootProblem Problem, LibraryRootInfo? Root, bool Added)> RootForAsync(int tenantId, int libraryId, string? path, bool anywhere,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
            return (RootProblem.NotAbsolute, null, false);
        var full = Normalize(path.Trim());
        if (await ListAsync(tenantId, libraryId, cancellationToken) is not { } roots)
            return (RootProblem.LibraryMissing, null, false);
        if (roots.Where(r => IsWithin(full, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault() is { } within)
            return within.Exists ? (RootProblem.None, within, false) : (RootProblem.Missing, null, false);
        var (problem, root) = await AddAsync(tenantId, libraryId, full, anywhere, cancellationToken);
        return (problem, root, problem == RootProblem.None);
    }

    /// <summary>The full path without a trailing separator (a drive's root keeps its own).</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return trimmed.Length == 0 ? full : trimmed;
    }

    /// <summary><paramref name="path"/> is <paramref name="folder"/> or inside it (case-insensitive on Windows).</summary>
    public static bool IsWithin(string path, string folder)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (path.Equals(folder, comparison))
            return true;
        var prefix = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }
}
