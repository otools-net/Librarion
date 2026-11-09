using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Merging;

/// <summary>
/// The books a run may have changed the duplicate keys of (added, read again, given or losing a file, removed). Past
/// <see cref="DuplicateIndex.RebuildAbove"/> only that there were many is kept: the groups are built whole instead.
/// </summary>
public sealed class TouchedBooks
{
    private HashSet<int>? ids = [];

    /// <summary>More than <see cref="DuplicateIndex.RebuildAbove"/>: their ids were let go.</summary>
    public bool Overflowed => ids is null;

    /// <summary>Books were deleted: groups they were in may be left with fewer members.</summary>
    public bool Shrunk { get; set; }

    public IReadOnlyCollection<int> Ids => (IReadOnlyCollection<int>?)ids ?? [];

    public bool Any => ids is null || ids.Count > 0 || Shrunk;

    public void Add(IEnumerable<int> books)
    {
        if (ids is null)
            return;
        foreach (var id in books)
            ids.Add(id);
        if (ids.Count > DuplicateIndex.RebuildAbove)
            ids = null;
    }
}

/// <summary>
/// The stored duplicate groups (stage 6 chunk 11, D-6.6, agents/docs/features/merging.md#stored-duplicate-groups): books
/// sharing a blocking key (the same file by size and CRC-32, the same identifier, the same work key), never compared
/// pairwise. <see cref="BuildAsync"/> walks each key's own index in key order, a page of <see cref="WalkSize"/> rows at a
/// time; <see cref="RefreshAsync"/> recomputes the keys of given books (their old and new ones) by keyed seeks. A key
/// shared by <see cref="MaxGroup"/> books or more is stored as noise, without members, and not read again until a
/// rebuild. An FB2 id (weak) groups only books that also share the title or the first author. Books marked unique, and
/// groups whose every pair is marked not duplicates (<see cref="NotDuplicate"/>), are left out.
/// </summary>
public sealed class DuplicateIndex(TenantDb tenantDb, TimeProvider time)
{
    public const int MaxGroup = BookQueries.MaxGroup;

    /// <summary>Rows read a page at a time by the build's walks: a key with more is noise for sure.</summary>
    public const int WalkSize = 5000;

    /// <summary>A run that touched more books than this builds the groups whole: cheaper than refreshing each.</summary>
    public const int RebuildAbove = 50_000;

    private const int Chunk = 1000;

    private readonly record struct Unit(DuplicateReason Level, string Key)
    {
        public long Hash => (long)XxHash64.HashToUInt64(Encoding.UTF8.GetBytes($"{(int)Level}\n{Key}"));
    }

    private sealed record Sharer(int BookId, string SortTitle = "", string SortAuthor = "");

    /// <summary>
    /// One build or end-of-run upkeep of a tenant at a time: scans of several libraries ending together each found no
    /// groups and built them all at once, every group written twice (found by the e2e run, 2026-11-09).
    /// </summary>
    private readonly ConcurrentDictionary<int, SemaphoreSlim> running = new();

    /// <summary>
    /// The end of a scan, import or removal (the user's answer, 2026-11-08): the groups built whole when the tenant has
    /// none yet or the run touched many books (only after a run that completed; one stopped half-way leaves the tenant
    /// unbuilt then), else the touched books' keys refreshed and, when books were deleted, the groups left short cleaned.
    /// </summary>
    public async Task AfterRunAsync(int tenantId, TouchedBooks touched, bool completed, CancellationToken cancellationToken)
    {
        if (!touched.Any)
            return;
        var gate = running.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await AfterRunLockedAsync(tenantId, touched, completed, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task AfterRunLockedAsync(int tenantId, TouchedBooks touched, bool completed, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var built = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.DuplicatesBuilt).FirstOrDefaultAsync(cancellationToken);
        if (built is null || touched.Overflowed)
        {
            if (completed)
                await BuildLockedAsync(tenantId, null, cancellationToken);
            else if (built is not null)
                await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DuplicatesBuilt, (DateTime?)null), cancellationToken);
            return;
        }

        await RefreshAsync(db, touched.Ids, cancellationToken);
        if (touched.Shrunk)
            await CleanAsync(db, cancellationToken);
    }

    /// <summary>
    /// Every group of the tenant built again: the old ones deleted, then the work keys, files and identifiers walked by
    /// their indexes. The tenant is marked built at the end only; how many groups (noise left out) it made.
    /// </summary>
    public async Task<int> BuildAsync(int tenantId, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var gate = running.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await BuildLockedAsync(tenantId, progress, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> BuildLockedAsync(int tenantId, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DuplicatesBuilt, (DateTime?)null), cancellationToken);
        await db.DuplicateGroups.ExecuteDeleteAsync(cancellationToken);
        var marks = await NotMarks.ReadAsync(db, null, cancellationToken);
        var (groups, seen) = (0, 0);

        async Task WriteRunsAsync<TRow>(IEnumerable<(Unit Unit, List<TRow> Rows)> runs, Func<TRow, Sharer> sharer, string current)
        {
            var results = runs.Select(r => (r.Unit, Sets(r.Unit, r.Rows.Select(sharer).ToList(), marks))).ToList();
            groups += await WriteAsync(db, tenantId, results, replace: false, cancellationToken);
            progress?.Report(new ScanProgress(seen, 0, groups, 0, 0, current));
        }

        long? lastWork = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = db.Books.Where(b => b.WorkKey != null && b.MergedIntoId == null);
            if (lastWork is { } after)
                query = query.Where(b => b.WorkKey > after);
            var rows = await query.OrderBy(b => b.WorkKey).Take(WalkSize).Select(b => new { Key = b.WorkKey!.Value, b.Id }).ToListAsync(cancellationToken);
            if (rows.Count == 0)
                break;
            seen += rows.Count;
            var runs = Split(rows, r => r.Key, rows.Count == WalkSize);
            await WriteRunsAsync(runs.Select(r => (new Unit(DuplicateReason.Work, r.Key.ToString(CultureInfo.InvariantCulture)), r.Rows)), r => new Sharer(r.Id), "work");
            lastWork = runs[^1].Key;
            if (rows.Count < WalkSize)
                break;
        }

        (long Size, long Crc)? lastFile = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = db.BookFiles.Where(f => f.Crc32 != null);
            // Size >= the last one's starts the index range there; the rest of the condition skips what was read.
            if (lastFile is { } after)
                query = query.Where(f => f.Size >= after.Size && (f.Size > after.Size || f.Crc32 > after.Crc));
            var rows = await query.OrderBy(f => f.Size).ThenBy(f => f.Crc32).Take(WalkSize)
                .Select(f => new { f.Size, Crc = f.Crc32!.Value, f.BookId }).ToListAsync(cancellationToken);
            if (rows.Count == 0)
                break;
            seen += rows.Count;
            var runs = Split(rows, r => (r.Size, r.Crc), rows.Count == WalkSize);
            await WriteRunsAsync(runs.Select(r => (new Unit(DuplicateReason.File, FileKey(r.Key.Size, r.Key.Crc)), r.Rows)), r => new Sharer(r.BookId), "file");
            lastFile = runs[^1].Key;
            if (rows.Count < WalkSize)
                break;
        }

        var schemes = await db.BookIdentifiers.Select(i => i.Scheme).Distinct().ToListAsync(cancellationToken);
        foreach (var scheme in schemes.Where(s => !BookQueries.SourceSchemes.Contains(s)).Order(StringComparer.Ordinal))
        {
            var weak = BookQueries.WeakSchemes.Contains(scheme);
            string? lastValue = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var query = db.BookIdentifiers.Where(i => i.Scheme == scheme);
                if (lastValue is not null)
                    query = query.Where(i => string.Compare(i.Value, lastValue) > 0);
                var rows = await query.OrderBy(i => i.Value).Take(WalkSize).Select(i => new { i.Value, i.BookId }).ToListAsync(cancellationToken);
                if (rows.Count == 0)
                    break;
                seen += rows.Count;
                var runs = Split(rows, r => r.Value, rows.Count == WalkSize);
                var names = weak ? await NamesAsync(db, runs.Where(r => r.Rows.Count is > 1 and < MaxGroup).SelectMany(r => r.Rows.Select(row => row.BookId)), cancellationToken) : null;
                await WriteRunsAsync(runs.Select(r => (new Unit(DuplicateReason.Identifier, $"{scheme}:{r.Key}"), r.Rows)),
                    r => names?.GetValueOrDefault(r.BookId) ?? new Sharer(r.BookId), scheme);
                lastValue = runs[^1].Key;
                if (rows.Count < WalkSize)
                    break;
            }
        }

        // SQLite plans by statistics it only has once asked: without them the Duplicates page's member lookup walked the
        // tenant's (TenantId, BookId) index instead of the key (850 ms at 2M). PostgreSQL's autovacuum keeps its own.
        if (db.Database.IsSqlite())
            await db.Database.ExecuteSqlRawAsync("ANALYZE \"DuplicateGroups\"; ANALYZE \"DuplicateMembers\";", cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DuplicatesBuilt, now), cancellationToken);
        return groups;
    }

    /// <summary>
    /// The groups of <paramref name="bookIds"/>' keys, old (the groups they are in) and new (their files, identifiers,
    /// work key), computed again by keyed seeks, a thousand books at a time; keys known as noise are skipped. Nothing
    /// before the tenant's first build (it makes them all).
    /// </summary>
    public static async Task RefreshAsync(LibrarionDbContext db, IEnumerable<int> bookIds, CancellationToken cancellationToken)
    {
        var tenantId = db.CurrentTenantId;
        foreach (var chunk in bookIds.Distinct().Chunk(Chunk))
        {
            var units = new HashSet<Unit>();
            var old = await (from m in db.DuplicateMembers
                             where chunk.Contains(m.BookId)
                             join g in db.DuplicateGroups on m.GroupId equals g.Id
                             select new { g.Level, g.Key }).Distinct().ToListAsync(cancellationToken);
            units.UnionWith(old.Select(o => new Unit(o.Level, o.Key)));
            var files = await db.BookFiles.Where(f => chunk.Contains(f.BookId) && f.Crc32 != null).Select(f => new { f.Size, f.Crc32 }).Distinct().ToListAsync(cancellationToken);
            units.UnionWith(files.Select(f => new Unit(DuplicateReason.File, FileKey(f.Size, f.Crc32!.Value))));
            var identifiers = await db.BookIdentifiers.Where(i => chunk.Contains(i.BookId) && !BookQueries.SourceSchemes.Contains(i.Scheme))
                .Select(i => new { i.Scheme, i.Value }).Distinct().ToListAsync(cancellationToken);
            units.UnionWith(identifiers.Select(i => new Unit(DuplicateReason.Identifier, $"{i.Scheme}:{i.Value}")));
            var works = await db.Books.Where(b => chunk.Contains(b.Id) && b.WorkKey != null && b.MergedIntoId == null).Select(b => b.WorkKey!.Value).Distinct().ToListAsync(cancellationToken);
            units.UnionWith(works.Select(w => new Unit(DuplicateReason.Work, w.ToString(CultureInfo.InvariantCulture))));
            if (units.Count == 0)
                continue;

            var hashes = units.Select(u => u.Hash).ToList();
            var noise = (await db.DuplicateGroups.Where(g => g.Noise && hashes.Contains(g.KeyHash)).Select(g => new { g.Level, g.Key }).ToListAsync(cancellationToken))
                .Select(g => new Unit(g.Level, g.Key)).ToHashSet();
            units.ExceptWith(noise);
            var sharers = await SharersAsync(db, units, cancellationToken);
            var marks = await NotMarks.ReadAsync(db, sharers.Values.SelectMany(s => s.Select(x => x.BookId)).Distinct().ToList(), cancellationToken);
            var results = units.Select(u => (u, Sets(u, sharers.GetValueOrDefault(u) ?? [], marks))).ToList();
            await WriteAsync(db, tenantId, results, replace: true, cancellationToken);
        }
    }

    /// <summary>Groups left with fewer than two members by deleted books go; the others' counts are set again. Reads the groups, not the books.</summary>
    public static async Task CleanAsync(LibrarionDbContext db, CancellationToken cancellationToken)
    {
        await db.DuplicateGroups.Where(g => !g.Noise && db.DuplicateMembers.Count(m => m.GroupId == g.Id) < 2).ExecuteDeleteAsync(cancellationToken);
        await db.DuplicateGroups.Where(g => !g.Noise && db.DuplicateMembers.Count(m => m.GroupId == g.Id) != g.BookCount)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.BookCount, g => db.DuplicateMembers.Count(m => m.GroupId == g.Id)), cancellationToken);
    }

    /// <summary>Whether the tenant's groups were built (the edits refresh theirs only then).</summary>
    public static Task<bool> BuiltAsync(LibrarionDbContext db, CancellationToken cancellationToken) =>
        db.Tenants.Where(t => t.Id == db.CurrentTenantId).AnyAsync(t => t.DuplicatesBuilt != null, cancellationToken);

    private static string FileKey(long size, long crc) => string.Create(CultureInfo.InvariantCulture, $"{size}:{crc}");

    /// <summary>Rows in key order cut into runs of one key; a full page's last run may go on in the next page and is left for it, unless it is the only one.</summary>
    private static List<(TKey Key, List<TRow> Rows)> Split<TRow, TKey>(List<TRow> rows, Func<TRow, TKey> key, bool full)
    {
        var runs = new List<(TKey Key, List<TRow> Rows)>();
        foreach (var row in rows)
        {
            var value = key(row);
            if (runs.Count == 0 || !EqualityComparer<TKey>.Default.Equals(runs[^1].Key, value))
                runs.Add((value, []));
            runs[^1].Rows.Add(row);
        }

        // A key filling a whole page is noise: kept, and the next page starts after it.
        if (full && runs.Count > 1)
            runs.RemoveAt(runs.Count - 1);
        return runs;
    }

    /// <summary>Whether a key is noise, and its groups: one, or for a weak key one per shared title and per shared first author.</summary>
    private static (bool Noise, List<int[]> Sets) Sets(Unit unit, IReadOnlyList<Sharer> sharers, NotMarks marks)
    {
        var books = sharers.DistinctBy(s => s.BookId).ToList();
        if (books.Count >= MaxGroup)
            return (true, []);
        if (books.Count < 2)
            return (false, []);
        var weak = unit.Level == DuplicateReason.Identifier && BookQueries.WeakSchemes.Any(w => unit.Key.StartsWith(w + ":", StringComparison.Ordinal));
        IEnumerable<int[]> sets = weak
            ? books.Where(b => b.SortTitle != "").GroupBy(b => b.SortTitle, StringComparer.Ordinal)
                .Concat(books.Where(b => b.SortAuthor != "").GroupBy(b => b.SortAuthor, StringComparer.Ordinal))
                .Select(g => g.Select(b => b.BookId).Order().ToArray())
            : [books.Select(b => b.BookId).Order().ToArray()];
        return (false, sets.Select(marks.Apply).Where(s => s.Length >= 2).DistinctBy(s => string.Join(',', s)).ToList());
    }

    /// <summary>The books sharing each key (merged books left out), by one query per level and identifier scheme.</summary>
    private static async Task<Dictionary<Unit, List<Sharer>>> SharersAsync(LibrarionDbContext db, IReadOnlyCollection<Unit> units, CancellationToken cancellationToken)
    {
        var found = new Dictionary<Unit, List<Sharer>>();
        void Add(Unit unit, Sharer sharer)
        {
            if (!found.TryGetValue(unit, out var list))
                found[unit] = list = [];
            list.Add(sharer);
        }

        var works = units.Where(u => u.Level == DuplicateReason.Work).Select(u => (long?)long.Parse(u.Key, CultureInfo.InvariantCulture)).ToList();
        foreach (var chunk in works.Chunk(Chunk))
            foreach (var row in await db.Books.Where(b => chunk.Contains(b.WorkKey) && b.MergedIntoId == null).Select(b => new { b.WorkKey, b.Id }).ToListAsync(cancellationToken))
                Add(new Unit(DuplicateReason.Work, row.WorkKey!.Value.ToString(CultureInfo.InvariantCulture)), new Sharer(row.Id));

        var files = units.Where(u => u.Level == DuplicateReason.File).ToHashSet();
        foreach (var chunk in files.Chunk(Chunk))
        {
            var pairs = chunk.Select(u => u.Key.Split(':')).Select(p => (Size: long.Parse(p[0], CultureInfo.InvariantCulture), Crc: (long?)long.Parse(p[1], CultureInfo.InvariantCulture))).ToList();
            var sizes = pairs.Select(p => p.Size).Distinct().ToList();
            var crcs = pairs.Select(p => p.Crc).Distinct().ToList();
            foreach (var row in await db.BookFiles.Where(f => sizes.Contains(f.Size) && crcs.Contains(f.Crc32)).Select(f => new { f.Size, f.Crc32, f.BookId }).ToListAsync(cancellationToken))
                if (new Unit(DuplicateReason.File, FileKey(row.Size, row.Crc32!.Value)) is var unit && files.Contains(unit))
                    Add(unit, new Sharer(row.BookId));
        }

        foreach (var scheme in units.Where(u => u.Level == DuplicateReason.Identifier).GroupBy(u => u.Key[..u.Key.IndexOf(':')], StringComparer.Ordinal))
        {
            var weak = BookQueries.WeakSchemes.Contains(scheme.Key);
            foreach (var chunk in scheme.Chunk(Chunk))
            {
                var values = chunk.Select(u => u.Key[(scheme.Key.Length + 1)..]).ToList();
                var rows = await db.BookIdentifiers.Where(i => i.Scheme == scheme.Key && values.Contains(i.Value)).Select(i => new { i.Value, i.BookId }).ToListAsync(cancellationToken);
                // Noise not yet known as such is cut before the names are read.
                var names = weak ? await NamesAsync(db, rows.GroupBy(r => r.Value).Where(g => g.Count() < MaxGroup).SelectMany(g => g.Select(r => r.BookId)), cancellationToken) : null;
                foreach (var row in rows)
                    Add(new Unit(DuplicateReason.Identifier, $"{scheme.Key}:{row.Value}"), names?.GetValueOrDefault(row.BookId) ?? new Sharer(row.BookId));
            }
        }

        return found;
    }

    /// <summary>Titles and first authors of books, for the weak keys' groups.</summary>
    private static async Task<Dictionary<int, Sharer>> NamesAsync(LibrarionDbContext db, IEnumerable<int> bookIds, CancellationToken cancellationToken)
    {
        var names = new Dictionary<int, Sharer>();
        foreach (var chunk in bookIds.Distinct().Chunk(Chunk))
            foreach (var book in await db.Books.Where(b => chunk.Contains(b.Id) && b.MergedIntoId == null).Select(b => new { b.Id, b.SortTitle, b.SortAuthor }).ToListAsync(cancellationToken))
                names[book.Id] = new Sharer(book.Id, book.SortTitle, book.SortAuthor);
        return names;
    }

    /// <summary>The keys' groups written (with <paramref name="replace"/>, their rows deleted first); how many groups, noise left out.</summary>
    private static async Task<int> WriteAsync(LibrarionDbContext db, int tenantId, IReadOnlyList<(Unit Unit, (bool Noise, List<int[]> Sets) Result)> results, bool replace,
        CancellationToken cancellationToken)
    {
        if (replace)
        {
            foreach (var level in results.GroupBy(r => r.Unit.Level))
                foreach (var chunk in level.Chunk(Chunk))
                {
                    var hashes = chunk.Select(r => r.Unit.Hash).ToList();
                    var keys = chunk.Select(r => r.Unit.Key).ToList();
                    var l = level.Key;
                    await db.DuplicateGroups.Where(g => g.Level == l && hashes.Contains(g.KeyHash) && keys.Contains(g.Key)).ExecuteDeleteAsync(cancellationToken);
                }
        }

        var groups = new List<DuplicateGroup>();
        foreach (var (unit, (noise, sets)) in results)
        {
            if (noise)
                groups.Add(new DuplicateGroup { TenantId = tenantId, Level = unit.Level, Key = unit.Key, KeyHash = unit.Hash, Noise = true });
            foreach (var set in sets)
                groups.Add(new DuplicateGroup
                {
                    TenantId = tenantId, Level = unit.Level, Key = unit.Key, KeyHash = unit.Hash, BookCount = set.Length,
                    Members = set.Select(id => new DuplicateMember { TenantId = tenantId, BookId = id }).ToList(),
                });
        }

        if (groups.Count == 0)
            return 0;
        db.DuplicateGroups.AddRange(groups);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return groups.Count(g => !g.Noise);
    }

    /// <summary>Books marked unique and pairs marked not duplicates, read for the books at hand (all of the tenant's for a build).</summary>
    private sealed class NotMarks
    {
        private readonly HashSet<int> unique = [];
        private readonly HashSet<(int, int)> pairs = [];

        public static async Task<NotMarks> ReadAsync(LibrarionDbContext db, IReadOnlyCollection<int>? bookIds, CancellationToken cancellationToken)
        {
            var marks = new NotMarks();
            if (!await db.NotDuplicates.AnyAsync(cancellationToken))
                return marks;
            var rows = new List<(int BookId, int OtherId)>();
            if (bookIds is null)
                rows.AddRange((await db.NotDuplicates.Select(n => new { n.BookId, n.OtherId }).ToListAsync(cancellationToken)).Select(n => (n.BookId, n.OtherId)));
            else
                foreach (var chunk in bookIds.Chunk(Chunk))
                    rows.AddRange((await db.NotDuplicates.Where(n => chunk.Contains(n.BookId) || chunk.Contains(n.OtherId)).Select(n => new { n.BookId, n.OtherId })
                        .ToListAsync(cancellationToken)).Select(n => (n.BookId, n.OtherId)));
            foreach (var (book, other) in rows)
                if (book == other)
                    marks.unique.Add(book);
                else
                    marks.pairs.Add((book, other));
            return marks;
        }

        /// <summary>The set without its unique books; empty when every pair left is marked.</summary>
        public int[] Apply(int[] set)
        {
            var kept = unique.Count == 0 ? set : set.Where(id => !unique.Contains(id)).ToArray();
            if (pairs.Count == 0 || kept.Length < 2)
                return kept;
            for (var i = 0; i < kept.Length; i++)
                for (var j = i + 1; j < kept.Length; j++)
                    if (!pairs.Contains((kept[i], kept[j])))
                        return kept;
            return [];
        }
    }
}
