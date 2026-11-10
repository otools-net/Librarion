using System.Diagnostics;
using Librarion.Benchmarks.Data;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Merging;
using Librarion.Core.Tagging;
using Librarion.Core.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Benchmarks.Bench;

public sealed record CurationBenchResult(string Provider, int Books, IReadOnlyList<string> Timings, IReadOnlyList<string> Failures)
{
    public bool WithinBudget => Failures.Count == 0;
}

/// <summary>
/// <c>curation</c>: the big changes of stage 6 on a generated catalog, each timed whole (they are background runs past
/// 10,000 links, D-6.4: no budget, the times recorded) and put back after, so the catalog stays as generated: the most
/// prolific author merged into the second (<see cref="TagMerger.MergeAsync"/>) and the merge undone; a new plain tag
/// given to the books of the 1 % filter tag (a scope tagging, <see cref="BookTagger.TagScopeAsync"/>), its count first,
/// then taken off again and the tag deleted. Fails only when a run doesn't succeed or the counts don't come back.
/// </summary>
public static class CurationCommand
{
    public static async Task<CurationBenchResult> RunAsync(BenchTarget target, string workFolder, Action<string> report, CancellationToken cancellationToken)
    {
        using var host = await BenchDatabase.OpenAsync(target, workFolder, cancellationToken, migrate: false);
        var services = host.Services;
        var tenantDb = services.GetRequiredService<TenantDb>();
        var merger = services.GetRequiredService<TagMerger>();
        var tagger = services.GetRequiredService<BookTagger>();
        var tagService = services.GetRequiredService<TagService>();
        var tasks = services.GetRequiredService<BackgroundTasks>();
        var tenant = (await services.GetRequiredService<AccessService>().ListTenantsAsync(cancellationToken)).Single(t => t.Name == "Bench");

        int libraryId, books, source, targetAuthor, filterTag;
        await using (var db = await tenantDb.ForTenantAsync(tenant.Id, cancellationToken))
        {
            var library = await db.Libraries.SingleAsync(cancellationToken);
            (libraryId, books) = (library.Id, library.BookCount);
            var authorField = await db.Tags.Where(t => t.BuiltIn == BuiltInTag.Author).Select(t => t.Id).SingleAsync(cancellationToken);
            var prolific = await db.TagCounts.Where(c => db.Tags.Any(t => t.Id == c.TagId && t.FieldId == authorField))
                .OrderByDescending(c => c.BookCount).Select(c => c.TagId).Take(2).ToListAsync(cancellationToken);
            (source, targetAuthor) = (prolific[0], prolific[1]);
            filterTag = await db.Tags.Where(t => t.Name == "filter 1%").Select(t => t.Id).SingleAsync(cancellationToken);
        }

        var open = new BookVisibility(new Dictionary<int, IReadOnlyList<FilterProfile>> { [libraryId] = [new FilterProfile([], [])] });
        var timings = new List<string>();
        var failures = new List<string>();

        async Task<long> TimeAsync(string name, Func<Task<AccessResult<TagWork>>> call)
        {
            var watch = Stopwatch.StartNew();
            var result = await call();
            if (result.Value is not { } work)
            {
                failures.Add($"{name}: refused ({result.Status})");
                return 0;
            }

            var outcome = TaskOutcome.Succeeded;
            if (work.RunId is { } runId)
            {
                TaskRunInfo? run;
                while ((run = await tasks.GetAsync(tenant.Id, runId, cancellationToken)) is { Finished: null })
                    await Task.Delay(100, cancellationToken);
                outcome = run?.Outcome ?? TaskOutcome.Failed;
            }

            var seconds = watch.Elapsed.TotalSeconds;
            var line = $"{name}: {work.Links:N0} links in {seconds:F1} s ({(work.RunId is null ? "inline" : "a background run")}, {outcome})";
            timings.Add(line);
            report($"curation: {line}");
            if (outcome != TaskOutcome.Succeeded)
                failures.Add($"{name}: {outcome}");
            return work.LogId ?? 0;
        }

        async Task<long> CountAsync(int tagId)
        {
            await using var db = await tenantDb.ForTenantAsync(tenant.Id, cancellationToken);
            return await db.BookTags.LongCountAsync(l => l.TagId == tagId, cancellationToken);
        }

        // A big author merged into another, then put back.
        var before = (await CountAsync(source), await CountAsync(targetAuthor));
        var log = await TimeAsync("the most prolific author merged into the second", () => merger.MergeAsync(tenant.Id, source, targetAuthor, null, cancellationToken));
        if (log != 0)
            await TimeAsync("that merge undone", () => merger.UndoAsync(tenant.Id, log, null, cancellationToken));
        var after = (await CountAsync(source), await CountAsync(targetAuthor));
        if (after != before)
            failures.Add($"the authors' links after the undo: {after}, before {before}");

        // A scope tagging of 1 % of the library: counted (what the dialog confirms), given, taken off.
        var scope = new BookScope(libraryId, Tag: filterTag);
        var count = Stopwatch.StartNew();
        var size = await tagger.CountAsync(tenant.Id, open, scope, cancellationToken);
        timings.Add($"the 1 % scope counted: {(size.Capped ? "at least " : "")}{size.Books:N0} books in {count.Elapsed.TotalMilliseconds:F0} ms");
        report($"curation: {timings[^1]}");
        var name = $"bench scope {DateTime.UtcNow.Ticks}";
        await TimeAsync("a new tag given to 1 % of the books", () => tagger.TagScopeAsync(tenant.Id, open, scope, new TagChange([new BookTagEdit(Name: name)]), null, cancellationToken));
        int tagId;
        await using (var db = await tenantDb.ForTenantAsync(tenant.Id, cancellationToken))
            tagId = await db.Tags.Where(t => t.Name == name).Select(t => t.Id).SingleAsync(cancellationToken);
        // The tag's books, counted whole here (the scope's count stops at its cap).
        var scoped = await CountAsync(filterTag);
        var given = await CountAsync(tagId);
        if (given != scoped)
            failures.Add($"the scope tagging linked {given} books of {scoped}");
        await TimeAsync("that tag taken off them", () => tagger.TagScopeAsync(tenant.Id, open, scope, new TagChange(Remove: [tagId]), null, cancellationToken));
        if (await CountAsync(tagId) != 0)
            failures.Add("the scope tag still has links");
        await TimeAsync("the tag deleted", () => tagService.DeleteAsync(tenant.Id, tagId, null, cancellationToken));

        // Tag all on a search: a word on about 2 % of the titles; the walk tests each book of the library by key.
        var search = new BookScope(libraryId, Query: "night");
        var searched = $"bench search {DateTime.UtcNow.Ticks}";
        await TimeAsync("a new tag given to a search's books (a common word)",
            () => tagger.TagScopeAsync(tenant.Id, open, search, new TagChange([new BookTagEdit(Name: searched)]), null, cancellationToken));
        int searchTag;
        await using (var db = await tenantDb.ForTenantAsync(tenant.Id, cancellationToken))
            searchTag = await db.Tags.Where(t => t.Name == searched).Select(t => t.Id).SingleAsync(cancellationToken);
        report($"curation: the search's books tagged: {await CountAsync(searchTag):N0}");
        await TimeAsync("that tag deleted", () => tagService.DeleteAsync(tenant.Id, searchTag, null, cancellationToken));
        if (await CountAsync(searchTag) != 0)
            failures.Add("the search's tag still has links");

        foreach (var failure in failures)
            report($"curation: FAILED {failure}");
        return new CurationBenchResult(target.Provider, books, timings, failures);
    }
}
