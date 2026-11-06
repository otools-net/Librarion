using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Catalog;

/// <param name="Zip">A ZIP container (its books inside), not a directory.</param>
/// <param name="Files">Present files directly in it; <paramref name="TotalFiles"/> with those under it.</param>
/// <param name="Folders">Folders directly under it.</param>
public sealed record FolderItem(int Id, string Name, bool Zip, int Files, int TotalFiles, int Folders);

public sealed record FolderCrumb(int Id, string Name);

/// <param name="Path">The folders above it, from the top (a library's only root left out: it is the library itself).</param>
public sealed record FolderDetail(FolderItem Folder, int LibraryId, IReadOnlyList<FolderCrumb> Path);

/// <summary>
/// The folder view (agents/docs/features/folders.md): a library's roots, a folder with the folders above it, a folder's
/// folders by name a page at a time by key (index <c>(ParentId, Name, Id)</c>); counts stored on the rows. Folder names
/// and counts are not narrowed by content filters: the route asks <c>library.folders</c> (the user's answer, 2026-11-06).
/// </summary>
public sealed class FolderQueries(TenantDb tenantDb)
{
    public const int MaxLimit = 200;

    /// <summary>The library's roots that hold books, named after their folders on disk.</summary>
    public async Task<IReadOnlyList<FolderItem>> RootsAsync(int tenantId, int libraryId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var rows = await db.Folders.Where(f => f.LibraryId == libraryId && f.ParentId == null)
            .Select(f => new { f.Id, RootPath = f.Root!.Path, f.FileCount, f.TotalFileCount, f.FolderCount }).ToListAsync(cancellationToken);
        return rows.Select(r => new FolderItem(r.Id, RootName(r.RootPath), false, r.FileCount, r.TotalFileCount, r.FolderCount))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Id).ToList();
    }

    /// <summary>A folder with the folders above it; null when there is none.</summary>
    public async Task<FolderDetail?> GetAsync(int tenantId, int id, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var chain = new List<(Folder Row, string RootPath)>();
        for (int? at = id; at is { } current && chain.Count < 256;)
        {
            var row = await db.Folders.AsNoTracking().Where(f => f.Id == current).Select(f => new { Folder = f, RootPath = f.Root!.Path }).FirstOrDefaultAsync(cancellationToken);
            if (row is null)
                break;
            chain.Add((row.Folder, row.RootPath));
            at = row.Folder.ParentId;
        }

        if (chain.Count == 0)
            return null;
        var folder = chain[0].Row;
        var libraryId = folder.LibraryId;
        var roots = await db.Folders.CountAsync(f => f.LibraryId == libraryId && f.ParentId == null, cancellationToken);
        var above = chain.Skip(1).Reverse().Where(c => roots > 1 || c.Row.ParentId is not null).Select(c => new FolderCrumb(c.Row.Id, Name(c.Row, c.RootPath))).ToList();
        return new FolderDetail(Item(folder, chain[0].RootPath), libraryId, above);
    }

    /// <summary>A folder's folders by name (then id), a page at a time.</summary>
    public async Task<Page<FolderItem>> ChildrenAsync(int tenantId, int id, string? after, int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        limit = Math.Clamp(limit, 1, MaxLimit);
        var query = db.Folders.Where(f => f.ParentId == id);
        if (KeysetCursor.Decode(after) is { Text: { } name } cursor)
            query = query.Where(f => string.Compare(f.Name, name) >= 0 && (string.Compare(f.Name, name) > 0 || f.Id > cursor.Id));
        var rows = await query.OrderBy(f => f.Name).ThenBy(f => f.Id).Take(limit + 1).AsNoTracking().ToListAsync(cancellationToken);
        var more = rows.Count > limit;
        if (more)
            rows.RemoveAt(limit);
        return new Page<FolderItem>(rows.Select(r => Item(r, "")).ToList(), more ? new KeysetCursor(rows[^1].Name, null, rows[^1].Id).Encode() : null);
    }

    private static FolderItem Item(Folder folder, string rootPath) =>
        new(folder.Id, Name(folder, rootPath), folder.ArchiveId is not null, folder.FileCount, folder.TotalFileCount, folder.FolderCount);

    /// <summary>A root's own folder is named after the root's folder on disk (its last part, not the server's path).</summary>
    private static string Name(Folder folder, string rootPath) => folder.ParentId is null ? RootName(rootPath) : folder.Name;

    private static string RootName(string rootPath) =>
        System.IO.Path.GetFileName(rootPath.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : rootPath;
}
