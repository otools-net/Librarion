using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Librarion.Benchmarks.Data;
using Librarion.Benchmarks.Generation;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Benchmarks.Bench;

/// <summary>The e-reader pages over HTTP, the sign-in through the form, and the server's memory.</summary>
public sealed record EReaderResult(IReadOnlyList<ScenarioResult> Pages, double SignInMs, double MemoryStartedMb, double MemoryIdleMb, double MemoryBudgetMb)
{
    public bool WithinBudget => Pages.All(p => p.WithinBudget) && MemoryIdleMb <= MemoryBudgetMb;
}

/// <summary>
/// <c>ereader</c> (stage 8 chunk 11, agents/tools/benchmark/README.md): starts the real server on a generated catalog
/// and asks it as a Kindle's browser would (its User-Agent, so the books' formats come in its order): signed in once
/// through <c>/e/signin</c> (the antiforgery token read from the form, the session cookie kept), then each kind of
/// <c>/e</c> page timed end to end (queries, the Razor page written; budget 100 ms p95, a search 300); the book page on
/// a different random book each time. The pages run the same Core queries as the app and OPDS, whose plans
/// <c>run</c> checks.
/// </summary>
public static partial class EReaderCommand
{
    private const double PageBudget = 100;
    private const double SearchBudget = 300;
    private const double MemoryBudget = 500;
    private const string KindleUserAgent =
        "Mozilla/5.0 (X11; U; Linux armv7l like Android; en-us) AppleWebKit/531.2+ (KHTML, like Gecko) Version/5.0 Safari/531.2+ Kindle/3.0+";

    public static async Task<EReaderResult> RunAsync(BenchTarget target, string workFolder, string apiPath, int port, int iterations, Action<string> report,
        CancellationToken cancellationToken)
    {
        // What the addresses need, found once in the database (slow queries allowed here).
        int libraryId, prolificAuthor, bigSeries;
        string titleCursor, addedCursor;
        List<int> books;
        using (var host = await BenchDatabase.OpenAsync(target, workFolder, cancellationToken, migrate: false))
        {
            var tenantDb = host.Services.GetRequiredService<TenantDb>();
            var tenant = (await host.Services.GetRequiredService<AccessService>().ListTenantsAsync(cancellationToken)).Single(t => t.Name == "Bench");
            await using var db = await tenantDb.ForTenantAsync(tenant.Id, cancellationToken);
            var library = await db.Libraries.SingleAsync(cancellationToken);
            libraryId = library.Id;
            var authorField = await db.Tags.Where(t => t.BuiltIn == BuiltInTag.Author).Select(t => t.Id).SingleAsync(cancellationToken);
            var seriesField = await db.Tags.Where(t => t.BuiltIn == BuiltInTag.Series).Select(t => t.Id).SingleAsync(cancellationToken);
            prolificAuthor = await db.TagCounts.Where(c => db.Tags.Any(t => t.Id == c.TagId && t.FieldId == authorField))
                .OrderByDescending(c => c.BookCount).Select(c => c.TagId).FirstAsync(cancellationToken);
            bigSeries = await db.TagCounts.Where(c => db.Tags.Any(t => t.Id == c.TagId && t.FieldId == seriesField))
                .OrderByDescending(c => c.BookCount).Select(c => c.TagId).FirstAsync(cancellationToken);
            var byTitle = await db.Books.OrderBy(b => b.SortTitle).ThenBy(b => b.Id).Skip(library.BookCount / 2).Select(b => new { b.SortTitle, b.Id }).FirstAsync(cancellationToken);
            titleCursor = new KeysetCursor(byTitle.SortTitle, null, byTitle.Id).Encode();
            var byAdded = await db.Books.OrderByDescending(b => b.Added).ThenByDescending(b => b.Id).Skip(library.BookCount / 2).Select(b => new { b.Added, b.Id }).FirstAsync(cancellationToken);
            addedCursor = new KeysetCursor(null, byAdded.Added.Ticks, byAdded.Id).Encode();
            var maxId = await db.Books.MaxAsync(b => b.Id, cancellationToken);
            var random = new Random(8);
            var wanted = Enumerable.Range(0, 3 * (iterations + 3)).Select(_ => random.Next(1, maxId + 1)).Distinct().ToList();
            books = await db.Books.Where(b => wanted.Contains(b.Id)).Select(b => b.Id).ToListAsync(cancellationToken);
            if (books.Count < iterations + 3)
                throw new InvalidOperationException($"Only {books.Count} random books found: too small a catalog.");
        }

        await using var server = await BenchServer.StartAsync(target, workFolder, "ereader-server", apiPath, port, cancellationToken);
        var memoryStarted = server.WorkingSetMb;
        report($"server up, working set {memoryStarted:0} MB");

        using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer(), UseCookies = true };
        using var browser = new HttpClient(handler) { BaseAddress = server.BaseAddress };
        browser.DefaultRequestHeaders.UserAgent.ParseAdd(KindleUserAgent);

        async Task<(double Ms, string Body)> GetAsync(string url)
        {
            var watch = Stopwatch.StartNew();
            using var response = await browser.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var ms = watch.Elapsed.TotalMilliseconds;
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException($"{url}: {(int)response.StatusCode} {response.Headers.Location} {body}");
            return (ms, body);
        }

        // Signed in as a person would: the form's token, the name and password, Stay signed in; a 302 on to the home page.
        var signInWatch = Stopwatch.StartNew();
        var (_, form) = await GetAsync("e/signin");
        var token = TokenField().Match(form);
        if (!token.Success)
            throw new InvalidOperationException("No antiforgery token in the sign-in form:\n" + form);
        using (var signIn = await browser.PostAsync("e/signin", new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   [WebUtility.HtmlDecode(token.Groups["name"].Value)] = WebUtility.HtmlDecode(token.Groups["value"].Value),
                   ["return"] = "/e/",
                   ["userName"] = "reader-0",
                   ["password"] = GenerateCommand.Password,
                   ["remember"] = "on",
               }), cancellationToken))
        {
            if (signIn.StatusCode != HttpStatusCode.Redirect)
                throw new InvalidOperationException($"Sign-in refused: {(int)signIn.StatusCode} {await signIn.Content.ReadAsStringAsync(cancellationToken)}");
        }
        var signInMs = signInWatch.Elapsed.TotalMilliseconds;
        report($"signed in through the form in {signInMs:0} ms (page, token, password hashed)");

        var l = $"e/l/{libraryId}";
        string After(string cursor) => "after=" + Uri.EscapeDataString(cursor);

        // Next pages as the page links them (a cursor of the page's own order).
        async Task<string> NextOfAsync(string url)
        {
            var (_, body) = await GetAsync(url);
            var next = NextLink().Match(body);
            if (!next.Success)
                throw new InvalidOperationException($"{url}: no Next link");
            return WebUtility.HtmlDecode(next.Groups["href"].Value).TrimStart('/');
        }

        var searchNext = await NextOfAsync($"{l}/search?q=night");
        var lettersNext = await NextOfAsync($"{l}/fields/author?from=M");
        var authorNext = await NextOfAsync($"{l}/tags/{prolificAuthor}/books");

        var pages = new (string Name, Func<int, string> Url, double Budget)[]
        {
            ("home (the only library's page)", _ => "e/", PageBudget),
            ("recent, first page", _ => $"{l}/recent", PageBudget),
            ("recent, deep page", _ => $"{l}/recent?{After(addedCursor)}", PageBudget),
            ("by title, first page", _ => $"{l}/titles", PageBudget),
            ("by title, deep page", _ => $"{l}/titles?{After(titleCursor)}", PageBudget),
            ("search a word in many titles", _ => $"{l}/search?q=night", SearchBudget),
            ("search a word, next page", _ => searchNext, SearchBudget),
            ("authors: first letters (kept)", _ => $"{l}/fields/author", PageBudget),
            ("authors from a letter", _ => $"{l}/fields/author?from=M", PageBudget),
            ("authors from a letter, next page", _ => lettersNext, PageBudget),
            ("plain tags: first letters (kept)", _ => $"{l}/tags", PageBudget),
            ("the most prolific author's books", _ => $"{l}/tags/{prolificAuthor}", PageBudget),
            ("the most prolific author's books, next page", _ => authorNext, PageBudget),
            ("the biggest series, by number", _ => $"{l}/tags/{bigSeries}/books", PageBudget),
            ("a book's page (random books)", i => $"{l}/books/{books[i]}", PageBudget),
        };
        var results = new List<ScenarioResult>();
        foreach (var (name, url, budget) in pages)
        {
            for (var i = 0; i < 3; i++)
                await GetAsync(url(i));
            var times = new List<double>();
            var bytes = 0;
            for (var i = 0; i < iterations; i++)
            {
                var (ms, body) = await GetAsync(url(3 + i));
                times.Add(ms);
                bytes = Encoding.UTF8.GetByteCount(body);
            }
            times.Sort();
            var result = new ScenarioResult("ereader http: " + name, budget, times[times.Count / 2], times[(int)Math.Ceiling(times.Count * 0.95) - 1], times[^1], bytes, [],
                []);
            results.Add(result);
            report($"{(result.WithinBudget ? "ok  " : "SLOW")} p50 {result.P50Ms,7:0.0} ms  p95 {result.P95Ms,7:0.0} ms  (budget {budget:0}) {bytes / 1024.0,6:0.0} KB  {name}");
        }

        // Idle: no requests for a while (the server's own timers and caches settle), then its working set.
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        var memoryIdle = server.WorkingSetMb;
        report($"memory: {memoryStarted:0} MB once started, {memoryIdle:0} MB idle after the run (budget {MemoryBudget:0}), private {server.PrivateMb:0} MB");

        var outcome = new EReaderResult(results, signInMs, memoryStarted, memoryIdle, MemoryBudget);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var file = Path.Combine(workFolder, $"ereader-{target.Provider}-{stamp}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(outcome, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        report($"report: {file}");
        return outcome;
    }

    /// <summary>The form's antiforgery field (FormToken.razor: a hidden input, its name then its value).</summary>
    [GeneratedRegex("""<input type="hidden" name="(?<name>[^"]*RequestVerificationToken[^"]*)" value="(?<value>[^"]+)"\s*/?>""")]
    private static partial Regex TokenField();

    /// <summary>The pager's Next (Pager.razor).</summary>
    [GeneratedRegex("""<a class="button next" href="(?<href>[^"]+)" rel="next"\s*/?>""")]
    private static partial Regex NextLink();
}
