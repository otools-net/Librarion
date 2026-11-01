using Librarion.Benchmarks.Data;
using Librarion.Benchmarks.Generation;
using Librarion.Core.Data;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>The benchmark's catalog generator at a small size (agents/tools/benchmark/README.md): same catalog for a seed, real shapes.</summary>
[TestClass]
public sealed class CatalogGeneratorTests
{
    private const int Books = 3000;

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    [TestMethod]
    public void TheSameSeedPlansTheSameBooks()
    {
        var target = new CatalogTarget(1, 1, 100, Enum.GetValues<BuiltInTag>().ToDictionary(t => t, t => (int)t + 1));
        var first = new CatalogGenerator(new CatalogShape(Books, 5), target);
        var again = new CatalogGenerator(new CatalogShape(Books, 5), target);
        var other = new CatalogGenerator(new CatalogShape(Books, 6), target);

        var ids = Enumerable.Range(1, 200).ToList();
        CollectionAssert.AreEqual(ids.Select(id => Describe(first.Plan(id))).ToList(), ids.Select(id => Describe(again.Plan(id))).ToList());
        CollectionAssert.AreNotEqual(ids.Select(id => Describe(first.Plan(id))).ToList(), ids.Select(id => Describe(other.Plan(id))).ToList());
    }

    private static string Describe(BookPlan book) =>
        $"{book.Title}|{string.Join(',', book.Authors)}|{book.Series}|{string.Join(',', book.Categories)}|{book.Files[0].Crc32}|{book.Isbn}";

    [TestMethod]
    public async Task AGeneratedCatalogHasTheShapesTheBenchmarkNeeds()
    {
        using var catalog = new TestCatalog();
        var target = new BenchTarget(TestCatalog.Provider == DatabaseProvider.Postgres ? "postgres" : "sqlite", catalog.ConnectionString);
        var shape = new CatalogShape(Books, 3) { EntriesPerArchive = 500 };
        var ids = await GenerateCommand.RunAsync(target, shape, catalog.BasePath, _ => { }, Cancel);

        using var host = await BenchDatabase.OpenAsync(target, catalog.BasePath, Cancel);
        await using var db = await host.Services.GetRequiredService<TenantDb>().ForTenantAsync(ids.TenantId, Cancel);
        Assert.AreEqual(Books, await db.Books.CountAsync(Cancel));
        Assert.AreEqual(Books, (await db.Libraries.SingleAsync(Cancel)).BookCount);
        Assert.IsGreaterThan(Books, await db.BookFiles.CountAsync(Cancel), "Some books have two files");
        Assert.AreEqual(shape.Archives, await db.Archives.CountAsync(Cancel));
        Assert.IsTrue(await db.Books.AnyAsync(b => b.Title == null, Cancel), "Some books have no title: the file name shows");

        // Zipf: a few authors with many books, many with one.
        var authorField = ids.Fields[BuiltInTag.Author];
        var perAuthor = await db.BookTags.Where(l => l.Tag!.FieldId == authorField).GroupBy(l => l.TagId).Select(g => g.Count()).ToListAsync(Cancel);
        Assert.IsGreaterThan(30, perAuthor.Max(), "A prolific author");
        Assert.IsGreaterThan(perAuthor.Count / 10, perAuthor.Count(n => n == 1), "Many authors with one book");

        // Counts: the field counts each book with an author once, whatever the number of its authors.
        var withAuthor = await db.Books.CountAsync(b => db.BookTags.Any(l => l.BookId == b.Id && l.Tag!.FieldId == authorField), Cancel);
        Assert.AreEqual(withAuthor, await db.TagCounts.Where(c => c.TagId == authorField).Select(c => c.BookCount).SingleAsync(Cancel));

        // Planted duplicates of each kind.
        Assert.IsGreaterThanOrEqualTo(Books / 50, await db.Books.GroupBy(b => b.WorkKey).CountAsync(g => g.Count() > 1, Cancel), "Same work");
        Assert.IsGreaterThanOrEqualTo(Books / 100, await db.BookFiles.GroupBy(f => new { f.Size, f.Crc32 }).CountAsync(g => g.Count() > 1, Cancel), "Same file");
        Assert.IsGreaterThan(0, await db.BookIdentifiers.Where(i => i.Scheme == "isbn").GroupBy(i => i.Value).CountAsync(g => g.Count() > 1, Cancel), "Same ISBN");

        // The narrow allow-lists: one filtered group each, its tag on about its share of the books.
        var filters = await db.GroupTagFilters.Include(f => f.Group).OrderBy(f => f.Group!.Name).ToListAsync(Cancel);
        Assert.HasCount(CatalogShape.FilterShares.Length, filters);
        var tenPercent = await db.BookTags.CountAsync(l => l.TagId == filters[2].TagId, Cancel);
        Assert.IsTrue(tenPercent is > Books / 10 * 2 / 3 and < Books / 10 * 4 / 3, $"10 % filter on {tenPercent} books");
    }
}
