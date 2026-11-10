using Librarion.Core.Merging;
using System.Text.Json;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Import;

/// <summary>A missing book as the library panel lists it.</summary>
/// <param name="Name">Its title, else its file's name.</param>
/// <param name="Path">Where its first file was (the archive's path and the entry's name for an entry).</param>
/// <param name="Ready">Missing longer than the library's retention: offered for removal first.</param>
public sealed record MissingBookItem(int Id, string Name, string Path, DateTime MissingSince, bool Ready);

/// <param name="Total">Missing books of the library.</param>
/// <param name="Ready">Of them, missing longer than <paramref name="KeepDays"/>.</param>
public sealed record MissingBookPage(IReadOnlyList<MissingBookItem> Items, string? Next, int Total, int Ready, int KeepDays);

/// <summary>Which missing books a user accepted to remove: these ids, or every one missing since before a time (all: null).</summary>
public sealed record MissingRemoval(IReadOnlyList<int>? BookIds, DateTime? MissingBefore);

/// <summary>
/// Books whose files are gone (agents/docs/features/missing-books.md, as SFE's missing files): kept with their
/// metadata and curation, found again when a file comes back, and removed **only when a user accepts it**. The
/// library's retention (<see cref="SettingCatalog.KeepMissingDays"/>) only says which ones are offered first.
/// </summary>
public sealed class MissingBooks(TenantDb tenantDb, SettingsService settings, DuplicateIndex duplicates, TimeProvider time)
{
    public const int BatchSize = 1000;

    /// <summary>The library's missing books, oldest first, a page at a time by key.</summary>
    public async Task<MissingBookPage> ListAsync(int tenantId, int libraryId, string? after, int limit, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var keepDays = await KeepDaysAsync(tenantId, libraryId, cancellationToken);
        var limitTime = time.GetUtcNow().UtcDateTime.AddDays(-keepDays);
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var missing = db.Books.Where(b => b.LibraryId == libraryId && b.MissingSince != null);
        var page = missing;
        if (KeysetCursor.Decode(after) is { Number: { } ticks, Id: var lastId } && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks)
        {
            var since = new DateTime(ticks, DateTimeKind.Utc);
            page = page.Where(b => b.MissingSince >= since && (b.MissingSince > since || b.Id > lastId));
        }

        var rows = await page.OrderBy(b => b.MissingSince).ThenBy(b => b.Id).Take(limit + 1)
            .Select(b => new
            {
                b.Id, b.Title, MissingSince = b.MissingSince!.Value,
                File = db.BookFiles.Where(f => f.BookId == b.Id).OrderBy(f => f.Id)
                    .Select(f => new { f.Path, Archive = f.Archive != null ? f.Archive.Path : null }).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        var more = rows.Count > limit;
        if (more)
            rows.RemoveAt(limit);
        var items = rows.Select(r => new MissingBookItem(r.Id, r.Title ?? FileTitles.FromPath(r.File?.Path ?? ""),
            r.File is null ? "" : r.File.Archive is null ? r.File.Path : $"{r.File.Archive}/{r.File.Path}", r.MissingSince, r.MissingSince < limitTime)).ToList();
        // Both counts read only the partial index of missing books.
        var total = await missing.CountAsync(cancellationToken);
        var ready = await missing.CountAsync(b => b.MissingSince < limitTime, cancellationToken);
        return new MissingBookPage(items, more ? new KeysetCursor(null, rows[^1].MissingSince.Ticks, rows[^1].Id).Encode() : null, total, ready, keepDays);
    }

    /// <summary>The library's retention in days (<see cref="SettingCatalog.KeepMissingDays"/>, by its levels).</summary>
    public async Task<int> KeepDaysAsync(int tenantId, int libraryId, CancellationToken cancellationToken = default)
    {
        var states = await settings.GetAsync(new SettingOwner(SettingLevel.Library, libraryId), tenantId, cancellationToken);
        var value = states?.FirstOrDefault(s => s.Key == SettingCatalog.KeepMissingDays)?.Value;
        return value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var days) ? days : 30;
    }

    /// <summary>
    /// Removes the missing books a user accepted (only books still missing: one that came back in the meantime stays),
    /// in batches: their files, their links, the archives left missing and empty. Counts don't move (they went down when
    /// the books went missing).
    /// </summary>
    public async Task<ScanResult> RemoveAsync(int tenantId, int libraryId, MissingRemoval removal, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var writer = new CatalogWriter(tenantId, libraryId, await ImportTagResolver.CreateAsync(db, tenantId, cancellationToken), time);
        var ids = removal.BookIds?.Distinct().ToList();
        var afterId = 0;
        // The folders the removed files were in: removed after them when nothing else is in them.
        var left = new HashSet<int>();
        var completed = false;
        try
        {
            while (true)
            {
                var after = afterId;
                var query = db.Books.Where(b => b.LibraryId == libraryId && b.MissingSince != null && b.Id > after);
                if (ids is not null)
                    query = query.Where(b => ids.Contains(b.Id));
                if (removal.MissingBefore is { } before)
                    query = query.Where(b => b.MissingSince < before);
                var batch = await query.OrderBy(b => b.Id).Take(BatchSize).Select(b => b.Id).ToListAsync(cancellationToken);
                if (batch.Count == 0)
                    break;
                left.UnionWith(await db.BookFiles.Where(f => batch.Contains(f.BookId)).Select(f => f.FolderId).Distinct().ToListAsync(cancellationToken));
                await db.BookFiles.Where(f => batch.Contains(f.BookId)).ExecuteDeleteAsync(cancellationToken);
                await writer.RemoveEmptyBooksAsync(db, batch, cancellationToken);
                afterId = batch[^1];
                progress?.Report(new ScanProgress(0, 0, 0, 0, writer.Removed, null));
            }

            await db.Archives.Where(a => a.LibraryId == libraryId && a.MissingSince != null && !db.BookFiles.Any(f => f.ArchiveId == a.Id))
                .ExecuteDeleteAsync(cancellationToken);
            await writer.Folders.PruneAsync(db, left, cancellationToken);
            completed = true;
        }
        finally
        {
            await writer.FlushCountsAsync(db, CancellationToken.None);
            await duplicates.AfterRunAsync(tenantId, writer.Touched, completed, completed ? cancellationToken : CancellationToken.None);
        }

        return new ScanResult(0, 0, writer.Removed, 0, 0, 0, 0, [], []);
    }
}
