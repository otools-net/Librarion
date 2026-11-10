using System.Diagnostics;
using Librarion.Benchmarks.Data;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Merging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Benchmarks.Bench;

public sealed record TagDuplicatesBenchResult(string Provider, int Tags, double BuildSeconds, int Groups, int NoiseKeys, IReadOnlyList<string> Timings, IReadOnlyList<string> Failures)
{
    public bool WithinBudget => Failures.Count == 0;
}

/// <summary>
/// <c>tag-duplicates</c>: the possible duplicate tags of a generated catalog built whole (stage 6 chunk 14,
/// agents/docs/features/merging.md#possible-duplicate-tags-built-stage-6-chunk-14), timed against
/// <see cref="BuildBudgetSeconds"/> at 2M books on PostgreSQL; then the Possible duplicates page of the authors (the first
/// page and the pages after it, walked by key) and a tag's look-alikes, p95 against the list budget (enforced on
/// PostgreSQL, recorded on SQLite, as the other lists).
/// </summary>
public static class TagDuplicatesCommand
{
    /// <summary>The whole build at 2M books on PostgreSQL (proposed with chunk 14): as the books' groups.</summary>
    public const double BuildBudgetSeconds = 10 * 60;

    private const double ListBudget = 100;
    private const int Iterations = 20;

    public static async Task<TagDuplicatesBenchResult> RunAsync(BenchTarget target, string workFolder, Action<string> report, CancellationToken cancellationToken)
    {
        using var host = await BenchDatabase.OpenAsync(target, workFolder, cancellationToken, migrate: false);
        var services = host.Services;
        var tenantDb = services.GetRequiredService<TenantDb>();
        var index = services.GetRequiredService<TagDuplicateIndex>();
        var queries = services.GetRequiredService<TagDuplicateQueries>();
        var tenant = (await services.GetRequiredService<AccessService>().ListTenantsAsync(cancellationToken)).Single(t => t.Name == "Bench");

        var watch = Stopwatch.StartNew();
        var groups = await index.BuildAsync(tenant.Id, null, cancellationToken);
        var seconds = watch.Elapsed.TotalSeconds;
        int tags, noise, author;
        List<int> sampled;
        await using (var db = await tenantDb.ForTenantAsync(tenant.Id, cancellationToken))
        {
            tags = await db.Tags.CountAsync(t => t.SoundKey != null, cancellationToken);
            noise = await db.TagDuplicateGroups.CountAsync(g => g.Noise, cancellationToken);
            author = await db.Tags.Where(t => t.FieldKey == "author").Select(t => t.Id).SingleAsync(cancellationToken);
            sampled = await db.TagDuplicateMembers.OrderBy(m => m.GroupId).Select(m => m.TagId).Take(Iterations).ToListAsync(cancellationToken);
            foreach (var (level, count) in (await db.TagDuplicateGroups.Where(g => !g.Noise).GroupBy(g => g.Level).Select(g => new { g.Key, Count = g.Count() })
                         .ToListAsync(cancellationToken)).Select(g => (g.Key, g.Count)))
                report($"tag duplicates: {level}: {count:N0} groups");
        }

        report($"tag duplicates: built {groups:N0} groups ({noise:N0} noise keys) over {tags:N0} tags in {seconds:F1} s");

        var failures = new List<string>();
        if (target.Provider == "postgres" && tags > 400_000 && seconds > BuildBudgetSeconds)
            failures.Add($"build took {seconds:F0} s, budget {BuildBudgetSeconds:F0} s");

        // The pages: the first, then each next one, as the More button walks them.
        var cursors = new List<string?> { null };
        for (var i = 0; i < Iterations - 1; i++)
        {
            var page = await queries.ListAsync(tenant.Id, author, cursors[^1], 20, cancellationToken);
            if (page.Next is null)
                break;
            cursors.Add(page.Next);
        }

        var timings = new List<string>();
        async Task TimeAsync(string name, Func<int, Task> call, int count)
        {
            for (var i = 0; i < 3; i++)
                await call(i % count);
            var times = new List<double>();
            for (var i = 0; i < Iterations; i++)
            {
                var one = Stopwatch.StartNew();
                await call(i % count);
                times.Add(one.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            var p95 = times[(int)Math.Ceiling(times.Count * 0.95) - 1];
            var line = $"{name}: p50 {times[times.Count / 2]:F1} ms, p95 {p95:F1} ms, max {times[^1]:F1} ms (budget {ListBudget:F0})";
            timings.Add(line);
            report($"tag duplicates: {line}");
            if (target.Provider == "postgres" && p95 > ListBudget)
                failures.Add($"{name}: p95 {p95:F0} ms");
        }

        await TimeAsync("authors' first page", _ => queries.ListAsync(tenant.Id, author, null, 20, cancellationToken), 1);
        await TimeAsync($"authors' pages 1-{cursors.Count}", i => queries.ListAsync(tenant.Id, author, cursors[i], 20, cancellationToken), cursors.Count);
        if (sampled.Count > 0)
            await TimeAsync("a tag's look-alikes", i => queries.OfTagAsync(tenant.Id, sampled[i], cancellationToken), sampled.Count);

        foreach (var failure in failures)
            report($"tag duplicates: FAILED {failure}");
        return new TagDuplicatesBenchResult(target.Provider, tags, seconds, groups, noise, timings, failures);
    }
}
