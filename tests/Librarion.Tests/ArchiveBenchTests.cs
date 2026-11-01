using Librarion.Benchmarks.Bench;
using Librarion.Benchmarks.Data;
using Librarion.Core.Data;
using Librarion.Tests.Infrastructure;

namespace Librarion.Tests;

/// <summary>The benchmark's <c>archive</c> command at a small size (agents/tools/benchmark/README.md): every phase runs, every book comes in.</summary>
[TestClass]
public sealed class ArchiveBenchTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ASmallArchiveIsListedIndexedSkippedAndRead()
    {
        using var catalog = new TestCatalog();
        var target = new BenchTarget(TestCatalog.Provider == DatabaseProvider.Postgres ? "postgres" : "sqlite", catalog.ConnectionString);

        var result = await ArchiveCommand.RunAsync(target, catalog.BasePath, entries: 500, bodyKb: 4, samples: 50, _ => { }, TestContext.CancellationToken);

        Assert.AreEqual(500, result.Books);
        Assert.AreEqual(0, result.Problems);
        Assert.IsGreaterThan(0, result.FirstByteP95Ms);
        Assert.IsGreaterThan(0, result.CoverP95Ms);
    }
}
