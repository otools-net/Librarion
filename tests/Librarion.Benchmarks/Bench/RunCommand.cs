using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Librarion.Benchmarks.Data;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Benchmarks.Bench;

/// <param name="Budget">p95 limit in milliseconds (agents/docs/development/foundation-plan.md#scale-budgets).</param>
public sealed record ScenarioResult(string Name, double BudgetMs, double P50Ms, double P95Ms, double MaxMs, int Rows, IReadOnlyList<string> Scans, IReadOnlyList<string> Plans)
{
    public bool WithinBudget => P95Ms <= BudgetMs;
}

/// <summary>
/// <c>run</c>: the main catalog queries over a generated catalog, warm, timed, their plans checked
/// (agents/tools/benchmark/README.md). Exit 1 when a query on PostgreSQL passes its budget or any plan scans a big
/// table; SQLite's times are recorded, not enforced (the stage 2 exit criteria).
/// </summary>
public static class RunCommand
{
    private const double ListBudget = 100;
    private const double DetailBudget = 50;
    private const double SearchBudget = 300;

    /// <summary>A figure asked once when a page or a choice opens and kept after (the Imported tags page's coverage line).</summary>
    private const double OnDemandBudget = 500;

    /// <param name="only">Only the scenarios whose name contains it (empty: all).</param>
    public static async Task<int> RunAsync(BenchTarget target, string workFolder, int iterations, Action<string> report, CancellationToken cancellationToken, string only = "")
    {
        using var host = await BenchDatabase.OpenAsync(target, workFolder, cancellationToken, migrate: false);
        var services = host.Services;
        var tenantDb = services.GetRequiredService<TenantDb>();
        var books = services.GetRequiredService<BookQueries>();
        var tags = services.GetRequiredService<TagQueries>();
        var folders = services.GetRequiredService<FolderQueries>();
        var imported = services.GetRequiredService<ImportedTags>();
        var suggestions = services.GetRequiredService<Librarion.Core.Search.SearchSuggestions>();
        var facets = services.GetRequiredService<Librarion.Core.Search.SearchFacets>();
        var visibilities = services.GetRequiredService<BookVisibilities>();
        var accounts = services.GetRequiredService<AccessService>();

        // What the scenarios need, found once (slow queries allowed here).
        var tenant = (await accounts.ListTenantsAsync(cancellationToken)).Single(t => t.Name == "Bench");
        int libraryId, prolificAuthor, middleAuthor, bigSeries, fiction, bookCount;
        string middleTitle, middleAuthorName, middleValueCursor;
        int middleTitleId, middleAuthorId;
        DateTime middleAdded;
        int middleAddedId;
        string prolificName, middleName;
        List<int> sampleIds;
        int zipFolder, looseParent, looseFolder;
        string importedCursor;
        await using (var db = await tenantDb.ForTenantAsync(tenant.Id, cancellationToken))
        {
            // The folder view (stage 6 chunk 8b): a ZIP of 100k entries, the folder of the thousand folders of loose files, one of those.
            zipFolder = await db.Folders.Where(f => f.ArchiveId != null).OrderByDescending(f => f.FileCount).Select(f => f.Id).FirstAsync(cancellationToken);
            looseParent = await db.Folders.Where(f => f.Path == "books").Select(f => f.Id).SingleAsync(cancellationToken);
            looseFolder = await db.Folders.Where(f => f.Path == "books/500").Select(f => f.Id).SingleAsync(cancellationToken);
            var library = await db.Libraries.SingleAsync(cancellationToken);
            libraryId = library.Id;
            bookCount = library.BookCount;
            var authorField = await db.Tags.Where(t => t.BuiltIn == BuiltInTag.Author).Select(t => t.Id).SingleAsync(cancellationToken);
            var seriesField = await db.Tags.Where(t => t.BuiltIn == BuiltInTag.Series).Select(t => t.Id).SingleAsync(cancellationToken);
            var authorCounts = db.TagCounts.Where(c => db.Tags.Any(t => t.Id == c.TagId && t.FieldId == authorField));
            prolificAuthor = await authorCounts.OrderByDescending(c => c.BookCount).Select(c => c.TagId).FirstAsync(cancellationToken);
            middleAuthor = await authorCounts.Where(c => c.BookCount >= 5 && c.BookCount <= 20).Select(c => c.TagId).FirstAsync(cancellationToken);
            prolificName = await db.Tags.Where(t => t.Id == prolificAuthor).Select(t => t.Name).SingleAsync(cancellationToken);
            middleName = await db.Tags.Where(t => t.Id == middleAuthor).Select(t => t.Name).SingleAsync(cancellationToken);
            bigSeries = await db.TagCounts.Where(c => db.Tags.Any(t => t.Id == c.TagId && t.FieldId == seriesField))
                .OrderByDescending(c => c.BookCount).Select(c => c.TagId).FirstAsync(cancellationToken);
            fiction = await db.Tags.Where(t => t.Name == "Fiction" && t.FieldId != null).Select(t => t.Id).FirstAsync(cancellationToken);
            var byTitle = await db.Books.OrderBy(b => b.SortTitle).ThenBy(b => b.Id).Skip(bookCount / 2).Select(b => new { b.SortTitle, b.Id }).FirstAsync(cancellationToken);
            (middleTitle, middleTitleId) = (byTitle.SortTitle, byTitle.Id);
            var byAuthor = await db.Books.OrderBy(b => b.SortAuthor).ThenBy(b => b.Id).Skip(bookCount / 2).Select(b => new { b.SortAuthor, b.Id }).FirstAsync(cancellationToken);
            (middleAuthorName, middleAuthorId) = (byAuthor.SortAuthor, byAuthor.Id);
            var byAdded = await db.Books.OrderByDescending(b => b.Added).ThenByDescending(b => b.Id).Skip(bookCount / 2).Select(b => new { b.Added, b.Id }).FirstAsync(cancellationToken);
            (middleAdded, middleAddedId) = (byAdded.Added, byAdded.Id);
            var authorsTotal = await db.Tags.CountAsync(t => t.FieldId == authorField, cancellationToken);
            var middleValue = await db.Tags.Where(t => t.FieldId == authorField).OrderBy(t => t.SortName).ThenBy(t => t.Id).Skip(authorsTotal / 2)
                .Select(t => new { t.SortName, t.Id }).FirstAsync(cancellationToken);
            middleValueCursor = new KeysetCursor(middleValue.SortName, null, middleValue.Id).Encode();
            // The Imported tags page's deep page: the plain tag 5,000th by books.
            var plainCount = await db.TagCounts.Where(c => c.LibraryId == libraryId && db.Tags.Any(t => t.Id == c.TagId && t.FieldId == null && t.FieldKey == null))
                .OrderByDescending(c => c.BookCount).ThenByDescending(c => c.TagId).Skip(5000).Select(c => new { c.BookCount, c.TagId }).FirstAsync(cancellationToken);
            importedCursor = new KeysetCursor(null, plainCount.BookCount, plainCount.TagId).Encode();
            var random = new Random(42);
            sampleIds = Enumerable.Range(0, iterations + 3).Select(_ => 1 + random.Next(bookCount)).ToList();
        }

        var open = new BookVisibility(new Dictionary<int, IReadOnlyList<FilterProfile>> { [libraryId] = [new FilterProfile([], [])] });
        var filtered = new List<(string Name, BookVisibility Visibility)>();
        foreach (var name in new[] { "filter-0", "filter-1", "filter-2" })
        {
            var user = (await accounts.ListUsersAsync(tenant.Id, name, null, cancellationToken: cancellationToken)).Single();
            filtered.Add((name, await visibilities.GetAsync(new AccessSubject(tenant.Id, user.Id), cancellationToken)));
        }

        var titleCursor = new KeysetCursor(middleTitle, null, middleTitleId).Encode();
        var authorCursor = new KeysetCursor(middleAuthorName, null, middleAuthorId).Encode();
        var addedCursor = new KeysetCursor(null, middleAdded.Ticks, middleAddedId).Encode();
        Task<int> List(BookVisibility visibility, BookPageRequest request) =>
            books.ListAsync(tenant.Id, visibility, request, cancellationToken).ContinueWith(t => t.Result.Items.Count, cancellationToken);

        var scenarios = new List<(string Name, double Budget, Func<int, Task<int>> Run)>
        {
            ("books by title, first page", ListBudget, _ => List(open, new BookPageRequest())),
            ("books by title, middle page", ListBudget, _ => List(open, new BookPageRequest(After: titleCursor))),
            ("books by author, first page", ListBudget, _ => List(open, new BookPageRequest(Sort: BookSort.Author))),
            ("books by author, middle page", ListBudget, _ => List(open, new BookPageRequest(Sort: BookSort.Author, After: authorCursor))),
            ("books newest first, first page", ListBudget, _ => List(open, new BookPageRequest(Sort: BookSort.Added))),
            ("books newest first, middle page", ListBudget, _ => List(open, new BookPageRequest(Sort: BookSort.Added, After: addedCursor))),
            ("one library by title, middle page", ListBudget, _ => List(open, new BookPageRequest(libraryId, After: titleCursor))),
            ("books of the most prolific author", ListBudget, _ => List(open, new BookPageRequest(TagId: prolificAuthor))),
            ("books of an author with 5-20 books", ListBudget, _ => List(open, new BookPageRequest(TagId: middleAuthor))),
            ("books of the biggest series, by number", ListBudget, _ => List(open, new BookPageRequest(Sort: BookSort.Number, TagId: bigSeries))),
            ("books of the biggest series, by title", ListBudget, _ => List(open, new BookPageRequest(TagId: bigSeries))),
            ("books of category Fiction (with subcategories)", ListBudget, _ => List(open, new BookPageRequest(TagId: fiction))),
            ("authors A-Z, first page", ListBudget, async _ => (await tags.ValuesAsync(tenant.Id, "author", libraryId, null, 100, cancellationToken))!.Items.Count),
            ("authors A-Z, middle page", ListBudget, async _ => (await tags.ValuesAsync(tenant.Id, "author", libraryId, middleValueCursor, 100, cancellationToken))!.Items.Count),
            ("book detail", DetailBudget, async i => await books.GetAsync(tenant.Id, open, sampleIds[i], cancellationToken) is null ? 0 : 1),
            // The other calls of the library's screens (stage 5): All fields, Tags, Go to, a tag's page, possible duplicates.
            ("fields", ListBudget, async _ => (await tags.FieldsAsync(tenant.Id, cancellationToken)).Count),
            ("plain tags A-Z, first page", ListBudget, async _ => (await tags.PlainTagsAsync(tenant.Id, libraryId, null, 100, cancellationToken, visibility: open)).Items.Count),
            ("authors from a letter (Go to)", ListBudget, async _ => (await tags.ValuesAsync(tenant.Id, "author", libraryId, null, 100, cancellationToken, "M", open))!.Items.Count),
            ("a tag's page: the most prolific author", DetailBudget, async _ => await tags.GetAsync(tenant.Id, prolificAuthor, libraryId, open, cancellationToken) is null ? 0 : 1),
            ("a tag's page: category Fiction", DetailBudget, async _ => await tags.GetAsync(tenant.Id, fiction, libraryId, open, cancellationToken) is null ? 0 : 1),
            ("possible duplicates", ListBudget, async i => (await books.DuplicatesAsync(tenant.Id, open, sampleIds[i], cancellationToken))?.Count ?? 0),
            // The folder view (stage 6 chunk 8b): a folder's folders by key, a folder with its path, a folder's books.
            ("folders: a folder of 1,000 folders, first page", ListBudget, async _ => (await folders.ChildrenAsync(tenant.Id, looseParent, null, 100, cancellationToken)).Items.Count),
            ("folders: a folder with the folders above it", DetailBudget, async _ => await folders.GetAsync(tenant.Id, looseFolder, cancellationToken) is null ? 0 : 1),
            ("books of a ZIP of 100k entries, by title", ListBudget, _ => List(open, new BookPageRequest(libraryId, FolderId: zipFolder))),
            ("books of a ZIP of 100k entries, by title, middle page", ListBudget, _ => List(open, new BookPageRequest(libraryId, After: titleCursor, FolderId: zipFolder))),
            ("books of a folder of loose files, by author", ListBudget, _ => List(open, new BookPageRequest(libraryId, BookSort.Author, FolderId: looseFolder))),
            // The Imported tags page (stage 6 chunk 9): 50k plain tags walked by books from the most (a deep page, and a status
            // none has: the walk's bound per request), a small field read whole, the coverage line.
            ("imported tags: plain tags by books, first page", ListBudget, async _ => (await imported.ListAsync(tenant.Id, libraryId, null, null, ImportedStatus.Review, null, 50, cancellationToken))!.Items.Count),
            ("imported tags: plain tags by books, deep page", ListBudget, async _ => (await imported.ListAsync(tenant.Id, libraryId, null, null, ImportedStatus.Review, importedCursor, 50, cancellationToken))!.Items.Count),
            ("imported tags: plain tags none reviewed (bounded walk)", ListBudget, async _ => (await imported.ListAsync(tenant.Id, libraryId, null, null, ImportedStatus.Reviewed, null, 50, cancellationToken))!.Items.Count),
            ("imported tags: languages (a small field)", ListBudget, async _ => (await imported.ListAsync(tenant.Id, libraryId, "language", null, ImportedStatus.Review, null, 50, cancellationToken))!.Items.Count),
            ("imported tags: categories (a small field)", ListBudget, async _ => (await imported.ListAsync(tenant.Id, libraryId, "category", null, ImportedStatus.Review, null, 50, cancellationToken))!.Items.Count),
            // The coverage line reads the field's 50k tags: kept between views, its first view on demand (OnDemandBudget).
            ("imported tags: the coverage of plain tags, computed", OnDemandBudget, async _ => (await imported.SummaryAsync(tenant.Id, libraryId, null, null, cancellationToken, fresh: true))!.Tags),
            ("imported tags: the coverage of plain tags, kept", ListBudget, async _ => (await imported.SummaryAsync(tenant.Id, libraryId, null, null, cancellationToken))!.Tags),
            ("imported tags: the fields", ListBudget, async _ => (await imported.FieldsAsync(tenant.Id, cancellationToken)).Count),
            // Search (agents/docs/features/search.md): each title word is on about 2 % of the books (the generator's word list),
            // a file name (the id) on one; author names reach books through the tag-name index.
            ("search: a word in many titles", SearchBudget, _ => List(open, new BookPageRequest(Query: "night"))),
            ("search: a word in many titles, middle page", SearchBudget, _ => List(open, new BookPageRequest(After: titleCursor, Query: "night"))),
            ("search: two common words", SearchBudget, _ => List(open, new BookPageRequest(Query: "night river"))),
            ("search: two letters (a prefix of many words)", SearchBudget, _ => List(open, new BookPageRequest(Query: "ri"))),
            ("search: a file name (one book)", SearchBudget, i => List(open, new BookPageRequest(Query: sampleIds[i].ToString(CultureInfo.InvariantCulture)))),
            ("search: the prolific author's last name", SearchBudget, _ => List(open, new BookPageRequest(Query: prolificName.Split(' ')[^1]))),
            ("search: an author with 5-20 books, by field", SearchBudget, _ => List(open, new BookPageRequest(Query: $"author:\"{middleName}\""))),
            ("search: an author by word and a title word", SearchBudget, _ => List(open, new BookPageRequest(Query: $"author:{middleName.Split(' ')[^1]} night"))),
            ("search: category Fiction, no PDF, by author", SearchBudget, _ => List(open, new BookPageRequest(Sort: BookSort.Author, Query: "category:Fiction -format:pdf"))),
            ("search: books outside any series, newest", SearchBudget, _ => List(open, new BookPageRequest(Sort: BookSort.Added, Query: "-series:"))),
            ("suggestions: two letters", SearchBudget, async _ => (await suggestions.SuggestAsync(tenant.Id, "ri", libraryId, open, cancellationToken)).Items.Count),
            ("suggestions: an author's first letters", SearchBudget, async _ => (await suggestions.SuggestAsync(tenant.Id, $"author:{middleName[..3]}", libraryId, open, cancellationToken)).Items.Count),
            // Facets (D-5.8): the stored counts for the library seen whole, else counted over at most 10,000 matches.
            ("facets: the library", SearchBudget, async _ => (await facets.ListAsync(tenant.Id, open, libraryId, null, cancellationToken)).Counted),
            ("facets: a word in many titles", SearchBudget, async _ => (await facets.ListAsync(tenant.Id, open, libraryId, "night", cancellationToken)).Counted),
            ("facets: two common words", SearchBudget, async _ => (await facets.ListAsync(tenant.Id, open, libraryId, "night river", cancellationToken)).Counted),
            ("facets: the prolific author's last name", SearchBudget, async _ => (await facets.ListAsync(tenant.Id, open, libraryId, prolificName.Split(' ')[^1], cancellationToken)).Counted),
            ("facets: an author with 5-20 books, by field", SearchBudget, async _ => (await facets.ListAsync(tenant.Id, open, libraryId, $"author:\"{middleName}\"", cancellationToken)).Counted),
            ("facets: category Fiction, no PDF", SearchBudget, async _ => (await facets.ListAsync(tenant.Id, open, libraryId, "category:Fiction -format:pdf", cancellationToken)).Counted),
            ("facets: books outside any series", SearchBudget, async _ => (await facets.ListAsync(tenant.Id, open, libraryId, "-series:", cancellationToken)).Counted),
        };
        foreach (var (name, visibility) in filtered)
        {
            scenarios.Add(($"{name} ({FilterShare(name)}), by title, first page", ListBudget, _ => List(visibility, new BookPageRequest())));
            scenarios.Add(($"{name} ({FilterShare(name)}), by title, middle page", ListBudget, _ => List(visibility, new BookPageRequest(After: titleCursor))));
            scenarios.Add(($"facets: {name} ({FilterShare(name)}), the library", SearchBudget,
                async _ => (await facets.ListAsync(tenant.Id, visibility, libraryId, null, cancellationToken)).Counted));
        }

        scenarios.RemoveAll(s => !s.Name.Contains(only, StringComparison.OrdinalIgnoreCase));
        using var capture = new CommandCapture();
        var results = new List<ScenarioResult>();
        foreach (var (name, budget, run) in scenarios)
        {
            for (var i = 0; i < 3; i++)
                await run(i);
            capture.Take();
            capture.Recording = true;
            var rows = await run(0);
            capture.Recording = false;
            var commands = capture.Take();

            var times = new List<double>();
            for (var i = 0; i < iterations; i++)
            {
                var watch = Stopwatch.StartNew();
                await run(i + 3 < sampleIds.Count ? i + 3 : i);
                times.Add(watch.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            var scans = new List<string>();
            var plans = new List<string>();
            foreach (var command in commands)
            {
                var (plan, found) = await PlanCheck.ExplainAsync(target.Provider, target.ConnectionString, command, cancellationToken);
                plans.Add(command.Sql + "\n--\n" + plan);
                scans.AddRange(found);
            }

            var result = new ScenarioResult(name, budget, times[times.Count / 2], times[(int)Math.Ceiling(times.Count * 0.95) - 1], times[^1], rows, scans, plans);
            results.Add(result);
            report($"{(result.WithinBudget ? "ok  " : "SLOW")} {(scans.Count > 0 ? "SCAN" : "    ")} p50 {result.P50Ms,7:0.0} ms  p95 {result.P95Ms,7:0.0} ms  (budget {budget:0}) {rows,4} rows  {name}");
            foreach (var scan in scans.Distinct())
                report($"        full scan: {scan}");
        }

        // The exit criterion of stage 6: how much of the imported links the first 500 tags hold (plain tags; categories).
        foreach (var field in new string?[] { null, "category" })
        {
            var summary = (await imported.SummaryAsync(tenant.Id, libraryId, field, null, cancellationToken))!;
            report($"imported {field ?? "plain tags"}: {summary.Tags:N0} tags, {summary.Links:N0} links, the first {summary.TopCount} hold {100.0 * summary.Top / Math.Max(1, summary.Links):0.0} %");
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var file = Path.Combine(workFolder, $"bench-{target.Provider}-{bookCount}-{stamp}");
        await File.WriteAllTextAsync(file + ".json", JsonSerializer.Serialize(new { target.Provider, Books = bookCount, Iterations = iterations, Results = results },
            new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        var plansText = new StringBuilder();
        foreach (var result in results)
            plansText.AppendLine($"### {result.Name}").AppendJoin("\n\n", result.Plans).AppendLine().AppendLine();
        await File.WriteAllTextAsync(file + "-plans.txt", plansText.ToString(), cancellationToken);
        report($"report: {file}.json, plans: {file}-plans.txt");

        var slow = results.Count(r => !r.WithinBudget);
        var scanning = results.Count(r => r.Scans.Count > 0);
        report($"{results.Count} queries, {slow} over budget, {scanning} with full scans of big tables");
        return scanning > 0 || target.IsPostgres && slow > 0 ? 1 : 0;
    }

    private static string FilterShare(string name) => name switch { "filter-0" => "0.1 %", "filter-1" => "1 %", _ => "10 %" };
}
