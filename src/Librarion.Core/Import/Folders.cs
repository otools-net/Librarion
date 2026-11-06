using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Import;

/// <summary>
/// The folders of one library's files (<see cref="Folder"/>, agents/docs/features/folders.md): a file's folder and every
/// folder above it found by path key or made, parents first, a batch of files at a time; a cache of the folders seen
/// (cleared past <see cref="MaxCached"/>: memory stays flat whatever the library's size). Used by one writer at a time
/// (the library's scan or import), so two can't make the same folder. Keeps the folders' counts by deltas
/// (<see cref="Count"/>, written by <see cref="FlushAsync"/>): a folder's own files, its total with every folder under
/// it, its folders.
/// </summary>
public sealed class Folders(int tenantId, int libraryId)
{
    private const int MaxCached = 100_000;
    private const int BatchSize = 1000;

    private readonly Dictionary<(int Root, string Path), int> ids = [];
    private readonly Dictionary<int, (int Root, string Path)> archives = [];
    private readonly Dictionary<int, int?> parents = [];
    private readonly Dictionary<int, (int Files, int Folders)> deltas = [];

    /// <summary>Present files came into (positive) or left (negative) a folder: counted at the next <see cref="FlushAsync"/>.</summary>
    public void Count(int folderId, int files) => Add(folderId, files, 0);

    private void Add(int folderId, int files, int folders)
    {
        var (f, d) = deltas.GetValueOrDefault(folderId);
        deltas[folderId] = (f + files, d + folders);
    }

    /// <summary>
    /// The counts moved since the last flush, written: each folder's own, the totals of the folders above it, grouped by
    /// the change so most batches are a few statements, never one per folder.
    /// </summary>
    public async Task FlushAsync(LibrarionDbContext db, CancellationToken cancellationToken)
    {
        if (deltas.Count == 0)
            return;
        var changes = deltas.Where(d => d.Value != default).ToList();
        deltas.Clear();
        await ParentsAsync(db, changes.Where(c => c.Value.Files != 0).Select(c => c.Key), cancellationToken);
        var totals = new Dictionary<int, int>();
        foreach (var (folderId, (files, _)) in changes)
        {
            if (files == 0)
                continue;
            for (int? at = folderId; at is { } id; at = parents.GetValueOrDefault(id))
                totals[id] = totals.GetValueOrDefault(id) + files;
        }

        var rows = changes.ToDictionary(c => c.Key, c => (c.Value.Files, Total: 0, c.Value.Folders));
        foreach (var (id, total) in totals)
            rows[id] = rows.TryGetValue(id, out var row) ? row with { Total = total } : (0, total, 0);
        foreach (var group in rows.Where(r => r.Value != default).GroupBy(r => r.Value))
        {
            var (files, total, folders) = group.Key;
            foreach (var chunk in group.Select(g => g.Key).Chunk(BatchSize))
                await db.Folders.Where(f => chunk.Contains(f.Id)).ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.FileCount, f => f.FileCount + files)
                    .SetProperty(f => f.TotalFileCount, f => f.TotalFileCount + total)
                    .SetProperty(f => f.FolderCount, f => f.FolderCount + folders), cancellationToken);
        }
    }

    /// <summary>The parents of <paramref name="folderIds"/> and of every folder above them, into the cache.</summary>
    private async Task ParentsAsync(LibrarionDbContext db, IEnumerable<int> folderIds, CancellationToken cancellationToken)
    {
        if (parents.Count > MaxCached)
            parents.Clear();
        var unknown = folderIds.Where(id => !parents.ContainsKey(id)).Distinct().ToList();
        while (unknown.Count > 0)
        {
            var next = new List<int>();
            foreach (var chunk in unknown.Chunk(BatchSize))
                foreach (var row in await db.Folders.Where(f => chunk.Contains(f.Id)).Select(f => new { f.Id, f.ParentId }).ToListAsync(cancellationToken))
                {
                    parents[row.Id] = row.ParentId;
                    if (row.ParentId is { } parent && !parents.ContainsKey(parent))
                        next.Add(parent);
                }

            unknown = next.Distinct().ToList();
        }
    }

    /// <summary>Sets <see cref="BookFile.FolderId"/> of the files that have none, making the folders they need.</summary>
    /// <param name="files">Loose files carry their <see cref="BookFile.RootId"/>, archive entries their <see cref="BookFile.ArchiveId"/>.</param>
    public async Task AssignAsync(LibrarionDbContext db, IEnumerable<BookFile> files, CancellationToken cancellationToken)
    {
        var pending = files.Where(f => f.FolderId == 0).ToList();
        if (pending.Count == 0)
            return;
        var unknown = pending.Where(f => f.ArchiveId is { } id && !archives.ContainsKey(id)).Select(f => f.ArchiveId!.Value).Distinct().ToList();
        foreach (var chunk in unknown.Chunk(BatchSize))
            foreach (var archive in await db.Archives.Where(a => chunk.Contains(a.Id)).Select(a => new { a.Id, a.RootId, a.Path }).ToListAsync(cancellationToken))
                archives[archive.Id] = (archive.RootId, archive.Path);

        var places = pending.Select(PlaceOf).ToList();
        await EnsureAsync(db, places, cancellationToken);
        for (var i = 0; i < pending.Count; i++)
            pending[i].FolderId = ids[(places[i].Root, places[i].Path)];
    }

    /// <summary>
    /// Of <paramref name="folderIds"/> (folders some files left), those holding neither a file nor a folder are removed,
    /// then their parents the same way; a root's own folder stays.
    /// </summary>
    public async Task PruneAsync(LibrarionDbContext db, IEnumerable<int> folderIds, CancellationToken cancellationToken)
    {
        var candidates = folderIds.Distinct().ToList();
        var removed = false;
        while (candidates.Count > 0)
        {
            var above = new HashSet<int>();
            foreach (var chunk in candidates.Chunk(BatchSize))
            {
                var empty = await db.Folders
                    .Where(f => chunk.Contains(f.Id) && f.ParentId != null && !db.BookFiles.Any(b => b.FolderId == f.Id) && !db.Folders.Any(c => c.ParentId == f.Id))
                    .Select(f => new { f.Id, f.ParentId }).ToListAsync(cancellationToken);
                if (empty.Count == 0)
                    continue;
                var gone = empty.Select(f => f.Id).ToList();
                await db.Folders.Where(f => gone.Contains(f.Id)).ExecuteDeleteAsync(cancellationToken);
                foreach (var folder in empty)
                {
                    above.Add(folder.ParentId!.Value);
                    Add(folder.ParentId!.Value, 0, -1);
                    deltas.Remove(folder.Id);
                    this.parents.Remove(folder.Id);
                }
                removed = true;
            }

            candidates = above.ToList();
        }

        if (removed)
            ids.Clear();
    }

    /// <summary>Where a file is: its root, its folder's path, and the ZIP it is in (its path and id) for an entry.</summary>
    private Place PlaceOf(BookFile file)
    {
        if (file.ArchiveId is { } archiveId)
        {
            var (root, path) = archives[archiveId];
            var inside = Directory(file.Path);
            return new Place(root, inside.Length == 0 ? path : $"{path}/{inside}", path, archiveId);
        }

        return new Place(file.RootId ?? throw new InvalidOperationException("A loose file has a root."), Directory(file.Path), null, null);
    }

    private static string Directory(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "";

    private async Task EnsureAsync(LibrarionDbContext db, List<Place> places, CancellationToken cancellationToken)
    {
        if (ids.Count > MaxCached)
            ids.Clear();

        // Every folder the files need, the ones above them too; a ZIP's own folder knows its archive.
        var wanted = new Dictionary<(int Root, string Path), int?>();
        foreach (var place in places.Distinct())
        {
            if (ids.ContainsKey((place.Root, place.Path)))
                continue;
            foreach (var path in Above(place.Path))
                if (path == place.Archive)
                    wanted[(place.Root, path)] = place.ArchiveId;
                else
                    wanted.TryAdd((place.Root, path), null);
        }

        var missing = wanted.Where(w => !ids.ContainsKey(w.Key)).ToList();
        if (missing.Count == 0)
            return;

        foreach (var chunk in missing.Chunk(BatchSize))
        {
            var roots = chunk.Select(m => m.Key.Root).Distinct().ToList();
            var keys = chunk.Select(m => PathKeys.Of(m.Key.Path)).Distinct().ToList();
            var paths = chunk.Select(m => m.Key).ToHashSet();
            foreach (var row in await db.Folders.Where(f => roots.Contains(f.RootId) && keys.Contains(f.PathKey))
                         .Select(f => new { f.Id, f.RootId, f.Path, f.ArchiveId }).ToListAsync(cancellationToken))
            {
                if (!paths.Contains((row.RootId, row.Path)))
                    continue;
                ids[(row.RootId, row.Path)] = row.Id;
                // A ZIP made again (removed, then back) is a new archive row: its folder follows.
                if (wanted[(row.RootId, row.Path)] is { } archiveId && row.ArchiveId != archiveId)
                    await db.Folders.Where(f => f.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.ArchiveId, archiveId), cancellationToken);
            }
        }

        // The rest made a depth at a time, so each one's parent has its id.
        var made = missing.Where(m => !ids.ContainsKey(m.Key)).GroupBy(m => Depth(m.Key.Path)).OrderBy(g => g.Key);
        foreach (var level in made)
        {
            var folders = level.Select(m => (m.Key, Row: new Folder
            {
                TenantId = tenantId, LibraryId = libraryId, RootId = m.Key.Root,
                ParentId = m.Key.Path.Length == 0 ? null : ids[(m.Key.Root, Directory(m.Key.Path))],
                Name = Name(m.Key.Path), Path = m.Key.Path, PathKey = PathKeys.Of(m.Key.Path), ArchiveId = m.Value,
            })).ToList();
            foreach (var chunk in folders.Chunk(BatchSize))
            {
                db.Folders.AddRange(chunk.Select(f => f.Row));
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
                foreach (var (key, row) in chunk)
                {
                    ids[key] = row.Id;
                    parents[row.Id] = row.ParentId;
                    if (row.ParentId is { } parent)
                        Add(parent, 0, 1);
                }
            }
        }
    }

    /// <summary>The path and every path above it, the root's own (empty) first.</summary>
    private static IEnumerable<string> Above(string path)
    {
        yield return "";
        for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            yield return path[..slash];
        if (path.Length > 0)
            yield return path;
    }

    private static int Depth(string path) => path.Length == 0 ? 0 : path.Count(c => c == '/') + 1;

    private static string Name(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.Length > Folder.MaxNameLength ? name[..Folder.MaxNameLength] : name;
    }

    private readonly record struct Place(int Root, string Path, string? Archive, int? ArchiveId);
}
