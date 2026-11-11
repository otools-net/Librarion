using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Librarion.Benchmarks.Data;
using Librarion.Benchmarks.Generation;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Benchmarks.Bench;

/// <summary>The whole OPDS feeds over HTTP, the first Basic sign-in of each account, and the server's memory.</summary>
public sealed record OpdsResult(IReadOnlyList<ScenarioResult> Feeds, double ColdP50Ms, double ColdP95Ms, double WarmP50Ms, double MemoryStartedMb, double MemoryIdleMb,
    double MemoryBudgetMb)
{
    public bool WithinBudget => Feeds.All(f => f.WithinBudget) && MemoryIdleMb <= MemoryBudgetMb;
}

/// <summary>
/// <c>opds</c> (stage 7 chunk 14, agents/tools/benchmark/README.md): starts the real server (<c>Librarion.Api</c>) on a
/// generated catalog and asks it as a reader app: each feed kind's page, timed end to end (query, entries, the Atom
/// written; budget 150 ms p95); Basic signed in cold (the password hashed: each <c>reader-N</c> once) and cached;
/// the server's memory once started and idle after the run (budget 500 MB). The plans of the same queries are
/// checked by <c>run</c> (its <c>opds:</c> scenarios).
/// </summary>
public static class OpdsCommand
{
    private const double FeedBudget = 150;
    private const double MemoryBudget = 500;

    public static async Task<OpdsResult> RunAsync(BenchTarget target, string workFolder, string apiPath, int port, int iterations, Action<string> report,
        CancellationToken cancellationToken)
    {
        // What the addresses need, found once in the database (slow queries allowed here).
        int libraryId, prolificAuthor, bigSeries, shelfId;
        string titleCursor, addedCursor, shelfCursor;
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
            var shelf = await db.Shelves.Where(s => s.Name == GenerateCommand.ShelfName).Select(s => new { s.Id }).FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("No bench shelf: generate the catalog again (stage 7 adds the readers and their shelf).");
            shelfId = shelf.Id;
            var middle = await db.ShelfBooks.Where(s => s.ShelfId == shelf.Id).OrderBy(s => s.Position).ThenBy(s => s.BookId).Skip(5000)
                .Select(s => new { s.Position, s.BookId }).FirstAsync(cancellationToken);
            shelfCursor = new KeysetCursor(null, middle.Position, middle.BookId).Encode();
        }

        var api = Path.GetFullPath(apiPath);
        if (!File.Exists(api))
            throw new FileNotFoundException($"{api}: build it first (dotnet build src/Librarion.Api -c Release)");
        var data = Path.Combine(workFolder, "opds-server");
        Directory.CreateDirectory(data);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(api)!, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[]
                 {
                     api, "--urls", $"http://127.0.0.1:{port}", "--environment", "Production",
                     $"--{DatabaseOptions.ProviderKey}={target.Provider}", $"--ConnectionStrings:{DatabaseOptions.ConnectionStringName}={target.ConnectionString}",
                     $"--Librarion:Data:Directory={Path.Combine(data, "data")}", $"--Librarion:Cache:Directory={Path.Combine(data, "cache")}",
                     // The benchmark asks far more than a reader app may (600 a minute).
                     $"--{SecurityOptions.SectionName}:OpdsRequestsPerMinute=1000000", "--Logging:LogLevel:Default=Warning",
                 })
            start.ArgumentList.Add(argument);
        using var server = Process.Start(start)!;
        var log = new StringBuilder();
        server.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        server.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        try
        {
            var baseAddress = new Uri($"http://127.0.0.1:{port}/");
            using var anonymous = new HttpClient { BaseAddress = baseAddress };
            var started = Stopwatch.StartNew();
            while (true)
            {
                if (server.HasExited)
                    throw new InvalidOperationException("The server stopped:\n" + log);
                try
                {
                    using var probe = await anonymous.GetAsync("opds", cancellationToken);
                    if (probe.StatusCode == HttpStatusCode.Unauthorized)
                        break;
                    if (probe.StatusCode == HttpStatusCode.ServiceUnavailable)
                        throw new InvalidOperationException("The server wants setup: generate the catalog again (stage 7 adds its administrator).");
                }
                catch (HttpRequestException) when (started.Elapsed < TimeSpan.FromMinutes(3))
                {
                }
                await Task.Delay(250, cancellationToken);
            }
            server.Refresh();
            var memoryStarted = server.WorkingSet64 / 1048576.0;
            report($"server up in {started.Elapsed.TotalSeconds:0.0} s, working set {memoryStarted:0} MB");

            HttpClient Reader(string name)
            {
                var client = new HttpClient { BaseAddress = baseAddress };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{name}:{GenerateCommand.Password}")));
                return client;
            }

            async Task<(double Ms, int Bytes)> GetAsync(HttpClient client, string url)
            {
                var watch = Stopwatch.StartNew();
                using var response = await client.GetAsync(url, cancellationToken);
                var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var ms = watch.Elapsed.TotalMilliseconds;
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException($"{url}: {(int)response.StatusCode} {Encoding.UTF8.GetString(body)}");
                return (ms, body.Length);
            }

            // The first sign-in of each account hashes its password; each account once (reader-0 stays for the feeds).
            var cold = new List<double>();
            for (var i = 1; i < GenerateCommand.Readers; i++)
            {
                using var fresh = Reader($"reader-{i}");
                cold.Add((await GetAsync(fresh, $"opds/l/{libraryId}")).Ms);
            }

            using var reader = Reader("reader-0");
            var l = $"opds/l/{libraryId}";
            string After(string cursor) => "?after=" + Uri.EscapeDataString(cursor);
            var feeds = new (string Name, string Url)[]
            {
                ("root (the only library's catalog)", "opds"),
                ("a library's catalog", l),
                ("recent, first page", $"{l}/recent"),
                ("recent, deep page", $"{l}/recent{After(addedCursor)}"),
                ("by title, first page", $"{l}/titles"),
                ("by title, deep page", $"{l}/titles{After(titleCursor)}"),
                ("authors: first letters (kept)", $"{l}/fields/author"),
                ("authors from a letter", $"{l}/fields/author?from=M"),
                ("the most prolific author's books", $"{l}/tags/{prolificAuthor}"),
                ("the biggest series, by number", $"{l}/tags/{bigSeries}/books"),
                ("plain tags: first letters (kept)", $"{l}/tags"),
                ("search a word in many titles", $"{l}/search?q=night"),
                ("my shelves", $"{l}/shelves"),
                ("a shelf of 10,000 books, first page", $"{l}/shelves/{shelfId}"),
                ("a shelf of 10,000 books, middle page", $"{l}/shelves/{shelfId}{After(shelfCursor)}"),
            };
            var results = new List<ScenarioResult>();
            foreach (var (name, url) in feeds)
            {
                for (var i = 0; i < 3; i++)
                    await GetAsync(reader, url);
                var times = new List<double>();
                var bytes = 0;
                for (var i = 0; i < iterations; i++)
                {
                    var (ms, size) = await GetAsync(reader, url);
                    times.Add(ms);
                    bytes = size;
                }
                times.Sort();
                var result = new ScenarioResult("opds http: " + name, FeedBudget, times[times.Count / 2], times[(int)Math.Ceiling(times.Count * 0.95) - 1], times[^1],
                    bytes, [], []);
                results.Add(result);
                report($"{(result.WithinBudget ? "ok  " : "SLOW")} p50 {result.P50Ms,7:0.0} ms  p95 {result.P95Ms,7:0.0} ms  (budget {FeedBudget:0}) {bytes / 1024.0,6:0.0} KB  {name}");
            }

            cold.Sort();
            var coldP50 = cold[cold.Count / 2];
            var coldP95 = cold[(int)Math.Ceiling(cold.Count * 0.95) - 1];
            var warm = results[1].P50Ms;
            report($"Basic: first sign-in p50 {coldP50:0.0} ms, p95 {coldP95:0.0} ms ({cold.Count} accounts); the same page signed in from the cache p50 {warm:0.0} ms");

            // Idle: no requests for a while (the server's own timers and caches settle), then its working set.
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            server.Refresh();
            var memoryIdle = server.WorkingSet64 / 1048576.0;
            report($"memory: {memoryStarted:0} MB once started, {memoryIdle:0} MB idle after the run (budget {MemoryBudget:0}), private {server.PrivateMemorySize64 / 1048576.0:0} MB");

            var outcome = new OpdsResult(results, coldP50, coldP95, warm, memoryStarted, memoryIdle, MemoryBudget);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var file = Path.Combine(workFolder, $"opds-{target.Provider}-{stamp}.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(outcome, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            report($"report: {file}");
            return outcome;
        }
        finally
        {
            if (!server.HasExited)
                server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync(CancellationToken.None);
        }
    }
}
