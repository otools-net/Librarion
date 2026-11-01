using System.Diagnostics;
using System.Text.Json;
using Librarion.Benchmarks.Data;
using Librarion.Benchmarks.Generation;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Imports;
using Librarion.Core.Imports.Calibre;
using Librarion.Core.Imports.Inpx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Benchmarks.Bench;

/// <param name="ImportSeconds">The first import into an empty library.</param>
/// <param name="ReImportSeconds">The same source again, unchanged: every record read and found the same.</param>
public sealed record ImportBenchResult(
    string Source, string Provider, int Records, double GenerateSeconds, double ImportSeconds, double ReImportSeconds, int Added, int Skipped, int Unchanged,
    int Problems, IReadOnlyList<string> Failures)
{
    public bool WithinBudget => Failures.Count == 0;
}

/// <summary>
/// <c>import</c>: the budgets of stage 4 (agents/docs/features/imports.md#scale): a generated Calibre library or INPX
/// catalog (kept between runs) imported by the real importer into a fresh database, then imported again unchanged.
/// Budgets are checked at the sizes they are set for (2M Calibre books, 1M INPX records).
/// </summary>
public static class ImportCommand
{
    public static readonly (int Records, double Import, double ReImport) CalibreBudget = (2_000_000, 30 * 60, 5 * 60);
    public static readonly (int Records, double Import, double ReImport) InpxBudget = (1_000_000, 15 * 60, 5 * 60);

    public static async Task<ImportBenchResult> RunAsync(BenchTarget target, string workFolder, string source, int records, Action<string> report,
        CancellationToken cancellationToken)
    {
        var shape = new CatalogShape(records, 1);
        var folder = Path.Combine(workFolder, $"import-{source}-{records}");
        var generate = Stopwatch.StartNew();
        var path = source switch
        {
            "calibre" => ImportSourceGenerator.Calibre(shape, folder, report, cancellationToken),
            "inpx" => ImportSourceGenerator.Inpx(shape, folder, report, cancellationToken),
            _ => throw new ArgumentException("--source is calibre or inpx"),
        };
        var generateSeconds = generate.Elapsed.TotalSeconds;
        report($"source: {path} ({generateSeconds:0} s to make; 0 when kept from before)");

        await BenchDatabase.RecreateAsync(target, cancellationToken);
        using var host = await BenchDatabase.OpenAsync(target, workFolder, cancellationToken);
        var services = host.Services;
        var tenant = (await services.GetRequiredService<AccessService>().CreateTenantAsync(new TenantInput("Import bench", LibraryName: "Imported"), cancellationToken)).Value!;
        int libraryId;
        await using (var db = await services.GetRequiredService<TenantDb>().ForTenantAsync(tenant.Id, cancellationToken))
            libraryId = (await db.Libraries.SingleAsync(cancellationToken)).Id;
        var (problem, root, _) = await services.GetRequiredService<LibraryRoots>().RootForAsync(tenant.Id, libraryId, folder, true, cancellationToken);
        if (root is null)
            throw new InvalidOperationException($"The source's folder isn't a root: {problem}");

        IImportSource Open() => source == "calibre" ? new CalibreSource(path, folder) : new InpxSource(path, folder);
        var importer = services.GetRequiredService<CatalogImporter>();
        var import = Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.FilesSeen % 100_000 < CatalogImporter.BatchSize)
                report($"  {p.FilesSeen:N0} records, {p.Added:N0} added ({import.Elapsed.TotalSeconds:0} s)");
        });
        var first = await importer.ImportAsync(tenant.Id, libraryId, root.Id, Open, progress, cancellationToken);
        var importSeconds = import.Elapsed.TotalSeconds;
        report($"import: {first.Added:N0} books in {importSeconds:0.0} s ({first.Added / Math.Max(importSeconds, 0.001):N0} a second), {first.Skipped:N0} skipped, "
            + $"{first.Problems.Count} problems");

        var again = Stopwatch.StartNew();
        var second = await importer.ImportAsync(tenant.Id, libraryId, root.Id, Open, null, cancellationToken);
        var reImportSeconds = again.Elapsed.TotalSeconds;
        report($"unchanged re-import: {reImportSeconds:0.0} s ({second.Unchanged:N0} unchanged, {second.Added} added, {second.Changed} changed)");

        var failures = new List<string>();
        var budget = source == "calibre" ? CalibreBudget : InpxBudget;
        if (records >= budget.Records)
        {
            if (importSeconds > budget.Import)
                failures.Add($"import: {importSeconds:0} s over {budget.Import} s");
            if (reImportSeconds > budget.ReImport)
                failures.Add($"unchanged re-import: {reImportSeconds:0} s over {budget.ReImport} s");
        }

        if (second.Added != 0 || second.Changed != 0)
            failures.Add($"the unchanged re-import wrote {second.Added} new and {second.Changed} changed books");
        foreach (var failure in failures)
            report("OVER BUDGET " + failure);

        var result = new ImportBenchResult(source, target.Provider, records, generateSeconds, importSeconds, reImportSeconds, first.Added, first.Skipped, second.Unchanged,
            first.Problems.Count, failures);
        var reportPath = Path.Combine(workFolder, $"import-{source}-{target.Provider}-{records}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        report($"report: {reportPath}");
        return result;
    }
}
