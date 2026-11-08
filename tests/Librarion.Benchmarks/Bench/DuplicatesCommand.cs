using System.Diagnostics;
using Librarion.Benchmarks.Data;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Merging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Benchmarks.Bench;

/// <param name="Found">Per level: planted pairs found in one group, of those whose books share the key and whose key isn't noise.</param>
public sealed record DuplicatesBenchResult(string Provider, int Books, double BuildSeconds, int Groups, int NoiseKeys, IReadOnlyDictionary<string, (int Found, int Expected, int Noise)> Found,
    IReadOnlyList<string> Failures)
{
    public bool WithinBudget => Failures.Count == 0;
}

/// <summary>
/// <c>duplicates</c>: the stored duplicate groups of a generated catalog built whole (stage 6 chunk 11,
/// agents/docs/features/merging.md#stored-duplicate-groups), timed against <see cref="BuildBudgetSeconds"/> at 2M books
/// on PostgreSQL; then the planted duplicates of <c>CatalogGenerator.Plan</c> (book <c>id</c> and <c>id - 1</c>: the same
/// file every 100th from 7, the same ISBN from 13, the same work every 50th from 21) looked for: each pair whose books
/// do share the key must be in one group of its level, unless the key is noise.
/// </summary>
public static class DuplicatesCommand
{
    /// <summary>The whole build at 2M books on PostgreSQL (proposed with chunk 11).</summary>
    public const double BuildBudgetSeconds = 10 * 60;

    private const int Sample = 2000;

    public static async Task<DuplicatesBenchResult> RunAsync(BenchTarget target, string workFolder, Action<string> report, CancellationToken cancellationToken)
    {
        using var host = await BenchDatabase.OpenAsync(target, workFolder, cancellationToken, migrate: false);
        var services = host.Services;
        var tenantDb = services.GetRequiredService<TenantDb>();
        var index = services.GetRequiredService<DuplicateIndex>();
        var tenant = (await services.GetRequiredService<AccessService>().ListTenantsAsync(cancellationToken)).Single(t => t.Name == "Bench");

        var watch = Stopwatch.StartNew();
        var groups = await index.BuildAsync(tenant.Id, new Progress<Librarion.Core.Import.ScanProgress>(p => { }), cancellationToken);
        var seconds = watch.Elapsed.TotalSeconds;
        report($"duplicates: built {groups:N0} groups in {seconds:F1} s");

        await using var db = await tenantDb.ForTenantAsync(tenant.Id, cancellationToken);
        var books = await db.Books.CountAsync(cancellationToken);
        var noiseKeys = await db.DuplicateGroups.CountAsync(g => g.Noise, cancellationToken);
        var found = new Dictionary<string, (int, int, int)>
        {
            ["file"] = await CheckAsync(db, DuplicateReason.File, 100, 7, books, cancellationToken),
            ["identifier"] = await CheckAsync(db, DuplicateReason.Identifier, 100, 13, books, cancellationToken),
            ["work"] = await CheckAsync(db, DuplicateReason.Work, 50, 21, books, cancellationToken),
        };
        foreach (var (level, (hit, expected, noise)) in found)
            report($"duplicates: planted {level}: {hit:N0} of {expected:N0} found ({noise:N0} more under noise keys)");

        var failures = new List<string>();
        if (books >= 2_000_000 && target.Provider == "postgres" && seconds > BuildBudgetSeconds)
            failures.Add($"build took {seconds:F0} s, budget {BuildBudgetSeconds:F0} s");
        failures.AddRange(found.Where(f => f.Value.Item1 < f.Value.Item2).Select(f => $"planted {f.Key}: {f.Value.Item1} of {f.Value.Item2} found"));
        var result = new DuplicatesBenchResult(target.Provider, books, seconds, groups, noiseKeys, found, failures);
        foreach (var failure in failures)
            report($"duplicates: FAILED {failure}");
        return result;
    }

    /// <summary>
    /// Of the first <see cref="Sample"/> planted pairs of a level: how many share their key in the database (expected),
    /// how many of those are in one group of the level (found), and how many share only a noise key.
    /// </summary>
    private static async Task<(int Found, int Expected, int Noise)> CheckAsync(LibrarionDbContext db, DuplicateReason level, int every, int from, int books,
        CancellationToken cancellationToken)
    {
        var ids = Enumerable.Range(0, Sample).Select(i => from + i * every).Where(id => id <= books).ToList();
        var both = ids.Concat(ids.Select(id => id - 1)).ToList();
        // The pairs sharing their key, with that key as the groups store it.
        var shared = new Dictionary<int, string>();
        switch (level)
        {
            case DuplicateReason.File:
                var files = await db.BookFiles.Where(f => both.Contains(f.BookId) && f.Crc32 != null).Select(f => new { f.BookId, f.Size, f.Crc32 }).ToListAsync(cancellationToken);
                var byBook = files.ToLookup(f => f.BookId, f => $"{f.Size}:{f.Crc32}");
                foreach (var id in ids)
                    if (byBook[id].Intersect(byBook[id - 1]).FirstOrDefault() is { } key)
                        shared[id] = key;
                break;
            case DuplicateReason.Identifier:
                var isbns = await db.BookIdentifiers.Where(i => both.Contains(i.BookId) && i.Scheme == "isbn").Select(i => new { i.BookId, i.Value }).ToListAsync(cancellationToken);
                var byIsbn = isbns.ToLookup(i => i.BookId, i => $"isbn:{i.Value}");
                foreach (var id in ids)
                    if (byIsbn[id].Intersect(byIsbn[id - 1]).FirstOrDefault() is { } key)
                        shared[id] = key;
                break;
            default:
                var works = await db.Books.Where(b => both.Contains(b.Id)).Select(b => new { b.Id, b.WorkKey }).ToDictionaryAsync(b => b.Id, b => b.WorkKey, cancellationToken);
                foreach (var id in ids)
                    if (works.GetValueOrDefault(id) is { } key && key == works.GetValueOrDefault(id - 1))
                        shared[id] = key.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
        }

        var keys = shared.Values.Distinct().ToList();
        var noiseKeys = (await db.DuplicateGroups.Where(g => g.Noise && g.Level == level && keys.Contains(g.Key)).Select(g => g.Key).ToListAsync(cancellationToken)).ToHashSet();
        var members = await (from m in db.DuplicateMembers
                             where both.Contains(m.BookId)
                             join g in db.DuplicateGroups on m.GroupId equals g.Id
                             where g.Level == level
                             select new { m.GroupId, m.BookId }).ToListAsync(cancellationToken);
        var groupsOf = members.ToLookup(m => m.BookId, m => m.GroupId);
        var noise = shared.Count(p => noiseKeys.Contains(p.Value));
        var found = shared.Keys.Count(id => !noiseKeys.Contains(shared[id]) && groupsOf[id].Intersect(groupsOf[id - 1]).Any());
        return (found, shared.Count - noise, noise);
    }
}
