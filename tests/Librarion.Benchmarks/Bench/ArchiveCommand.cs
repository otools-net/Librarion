using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Librarion.Benchmarks.Data;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Containers;
using Librarion.Core.Covers;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Reading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Benchmarks.Bench;

/// <param name="IndexSeconds">The first scan: every entry's head read through the real metadata readers and written.</param>
/// <param name="SkipMs">The second scan of the unchanged library (the archive skipped by size and time).</param>
/// <param name="FirstByteP95Ms">From a book file's row to its first byte: the archive opened, the entry found by its locator.</param>
public sealed record ArchiveBenchResult(
    string Disk, int Entries, long ZipBytes, double GenerateSeconds, double ListMs, double IndexSeconds, double SkipMs,
    double FirstByteP50Ms, double FirstByteP95Ms, double CoverP50Ms, double CoverP95Ms, int Books, int Problems, IReadOnlyList<string> Failures)
{
    public bool WithinBudget => Failures.Count == 0;
}

/// <summary>
/// <c>archive</c>: the archive budgets of stage 3 (agents/docs/features/archives.md#scale-budgets): a generated zip of
/// FB2 books (kept between runs: same entries and body size, same file), listed, indexed from the heads by the real
/// scanner into a fresh database, scanned again unchanged, then the first byte of random books and their generated
/// covers timed. The file cache of the system is warm after generation: list and index times are a warm disk's.
/// </summary>
public static class ArchiveCommand
{
    public const double ListBudgetMs = 2000;
    public const double IndexBudgetSeconds = 180;
    public const double SkipBudgetMs = 50;
    public const double FirstByteBudgetMs = 50;

    public static async Task<ArchiveBenchResult> RunAsync(BenchTarget target, string workFolder, int entries, int bodyKb, int samples,
        Action<string> report, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(workFolder, $"archive-{entries}-{bodyKb}k");
        var zip = Path.Combine(folder, "fb2-books.zip");
        var generate = Stopwatch.StartNew();
        if (!File.Exists(zip))
        {
            report($"writing {entries:N0} FB2 books into {zip}…");
            Directory.CreateDirectory(folder);
            var temp = zip + ".tmp";
            WriteZip(temp, entries, bodyKb, cancellationToken);
            File.Move(temp, zip, overwrite: true);
        }

        var generateSeconds = generate.Elapsed.TotalSeconds;
        var zipBytes = new FileInfo(zip).Length;
        report($"zip: {zipBytes / 1048576.0:N0} MB ({generateSeconds:0.0} s to make; 0 when kept from before)");

        var list = Stopwatch.StartNew();
        int listed;
        using (var stream = File.OpenRead(zip))
        {
            var directory = ZipDirectory.Locate(stream);
            listed = directory.Read(stream, cancellationToken).Count();
        }

        var listMs = list.Elapsed.TotalMilliseconds;
        report($"list: {listed:N0} entries in {listMs:0} ms");

        await BenchDatabase.RecreateAsync(target, cancellationToken);
        using var host = await BenchDatabase.OpenAsync(target, workFolder, cancellationToken);
        var services = host.Services;
        var tenant = (await services.GetRequiredService<AccessService>().CreateTenantAsync(new TenantInput("Archive bench", LibraryName: "Zip"), cancellationToken)).Value!;
        var tenantDb = services.GetRequiredService<TenantDb>();
        int libraryId;
        await using (var db = await tenantDb.ForTenantAsync(tenant.Id, cancellationToken))
        {
            libraryId = (await db.Libraries.SingleAsync(cancellationToken)).Id;
            db.LibraryRoots.Add(new LibraryRoot { TenantId = tenant.Id, LibraryId = libraryId, Path = folder, Created = DateTime.UtcNow });
            await db.SaveChangesAsync(cancellationToken);
        }

        var scanner = services.GetRequiredService<LibraryScanner>();
        var index = Stopwatch.StartNew();
        var first = await scanner.ScanAsync(tenant.Id, libraryId, null, cancellationToken);
        var indexSeconds = index.Elapsed.TotalSeconds;
        report($"index from heads: {first.Added:N0} books in {indexSeconds:0.0} s ({first.Added / Math.Max(indexSeconds, 0.001):N0} a second), {first.Problems.Count} problems");

        var skip = Stopwatch.StartNew();
        var second = await scanner.ScanAsync(tenant.Id, libraryId, null, cancellationToken);
        var skipMs = skip.Elapsed.TotalMilliseconds;
        report($"unchanged scan: {skipMs:0.0} ms ({second.ArchivesSkipped} archive skipped, {second.Added} added)");

        // Random books: their rows read first (as the route does), then the time to their first byte.
        List<StoredBookFile> files;
        await using (var db = await tenantDb.ForTenantAsync(tenant.Id, cancellationToken))
        {
            var ids = await db.BookFiles.OrderBy(f => f.Id).Select(f => f.Id).ToListAsync(cancellationToken);
            var random = new Random(7);
            var chosen = Enumerable.Range(0, Math.Min(samples, ids.Count)).Select(_ => ids[random.Next(ids.Count)]).Distinct().ToList();
            files = await BookDownloads.StoredAsync(db.BookFiles.Where(f => chosen.Contains(f.Id)), cancellationToken);
        }

        var firstBytes = new List<double>();
        foreach (var file in files)
        {
            var watch = Stopwatch.StartNew();
            var (content, problem) = BookDownloads.Open(file.Target);
            if (content is null)
                throw new InvalidOperationException($"File {file.Target.FileId} doesn't open: {problem}");
            using (content.Stream)
                if (content.Stream.ReadByte() < 0)
                    throw new InvalidOperationException($"File {file.Target.FileId} is empty");
            firstBytes.Add(watch.Elapsed.TotalMilliseconds);
        }

        var covers = services.GetRequiredService<CoverService>();
        var visibility = new BookVisibility(new Dictionary<int, IReadOnlyList<FilterProfile>> { [libraryId] = [new FilterProfile([], [])] });
        var coverTimes = new List<double>();
        foreach (var file in files.Take(100))
        {
            var watch = Stopwatch.StartNew();
            _ = await covers.GetAsync(tenant.Id, visibility, file.BookId, CoverSize.M, cancellationToken) ?? throw new InvalidOperationException($"Book {file.BookId} has no cover");
            coverTimes.Add(watch.Elapsed.TotalMilliseconds);
        }

        var failures = new List<string>();
        void Check(string what, double value, double budget, string unit)
        {
            if (value > budget)
                failures.Add($"{what}: {value:0.0} {unit} over {budget} {unit}");
        }

        var result = new ArchiveBenchResult(Path.GetPathRoot(folder) ?? "", entries, zipBytes, generateSeconds, listMs, indexSeconds, skipMs,
            Percentile(firstBytes, 0.5), Percentile(firstBytes, 0.95), Percentile(coverTimes, 0.5), Percentile(coverTimes, 0.95),
            first.Added, first.Problems.Count, failures);
        if (entries >= 100_000)
        {
            Check("list", listMs, ListBudgetMs, "ms");
            Check("index from heads", indexSeconds, IndexBudgetSeconds, "s");
        }

        Check("unchanged scan", skipMs, SkipBudgetMs, "ms");
        Check("first byte p95", result.FirstByteP95Ms, FirstByteBudgetMs, "ms");
        report($"first byte: p50 {result.FirstByteP50Ms:0.0} ms, p95 {result.FirstByteP95Ms:0.0} ms over {firstBytes.Count} books");
        report($"cover (none in the book: looked for, then generated), m: p50 {result.CoverP50Ms:0.0} ms, p95 {result.CoverP95Ms:0.0} ms");
        foreach (var failure in failures)
            report("OVER BUDGET " + failure);

        var path = Path.Combine(workFolder, $"archive-{target.Provider}-{entries}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        report($"report: {path}");
        return result;
    }

    private static double Percentile(List<double> values, double share)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.Order().ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(share * sorted.Count) - 1)];
    }

    private static readonly string[] Syllables = ["ka", "lo", "mir", "en", "dra", "vel", "so", "th", "ar", "ion", "ste", "qu", "ber", "na", "ri", "gol", "pe", "us", "tan", "wy"];
    private static readonly string[] Genres = ["sf_fantasy", "sf", "det_classic", "prose_classic", "love_contemporary", "adv_history", "child_tale", "sci_history", "poetry", "humor_prose"];

    /// <summary>A zip of FB2 books written in entry order, deterministic: a real description head, then a body of <paramref name="bodyKb"/> KB.</summary>
    public static void WriteZip(string path, int entries, int bodyKb, CancellationToken cancellationToken)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        var random = new Random(1);
        var text = new StringBuilder();
        for (var i = 0; i < entries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            text.Clear();
            string Word() => string.Concat(Enumerable.Range(0, random.Next(1, 4)).Select(_ => Syllables[random.Next(Syllables.Length)]));
            string Name() => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Word());
            var title = string.Join(' ', Enumerable.Range(0, random.Next(1, 6)).Select(_ => Name()));
            text.Append("""<?xml version="1.0" encoding="utf-8"?>""").Append('\n')
                .Append("""<FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink"><description><title-info>""");
            text.Append("<genre>").Append(Genres[random.Next(Genres.Length)]).Append("</genre>");
            for (var a = random.Next(1, 3); a > 0; a--)
                text.Append("<author><first-name>").Append(Name()).Append("</first-name><last-name>").Append(Name()).Append("</last-name></author>");
            text.Append("<book-title>").Append(title).Append("</book-title>");
            text.Append("<annotation><p>").Append(string.Join(' ', Enumerable.Range(0, 40).Select(_ => Word()))).Append("</p></annotation>");
            text.Append("<lang>en</lang>");
            if (random.Next(10) < 3)
                text.Append("<sequence name=\"").Append(Name()).Append("\" number=\"").Append(random.Next(1, 20)).Append("\"/>");
            var id = new byte[16];
            random.NextBytes(id);
            text.Append("</title-info><document-info><id>").Append(new Guid(id)).Append("</id></document-info></description><body><section>");
            while (text.Length < bodyKb * 1024)
                text.Append("<p>").Append(string.Join(' ', Enumerable.Range(0, 30).Select(_ => Word()))).Append(".</p>");
            text.Append("</section></body></FictionBook>");

            var entry = archive.CreateEntry($"{i:D6}.fb2", CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(text.ToString()));
        }
    }
}
