using Librarion.Api.Endpoints;
using Librarion.Benchmarks.Data;
using Librarion.Benchmarks.Generation;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Tests.Infrastructure;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>
/// Book and field-value lists by key over a small generated catalog, on both providers: every page walk sees each
/// row once in order, content filters never leak, the HTTP routes answer only what the caller may browse.
/// </summary>
[TestClass]
public sealed class CatalogQueryTests
{
    private const int Books = 2500;

    private static TestCatalog _catalog = null!;
    private static WebApplicationFactory<Program> _factory = null!;
    private static CatalogTarget _target = null!;

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        _catalog = new TestCatalog();
        var target = new BenchTarget(TestCatalog.Provider == DatabaseProvider.Postgres ? "postgres" : "sqlite", _catalog.ConnectionString);
        _target = await GenerateCommand.RunAsync(target, new CatalogShape(Books, 11) { EntriesPerArchive = 400 }, _catalog.BasePath, _ => { }, context.CancellationToken);
        _factory = ApiClients.CreateFactory(_catalog);
        // Started here, once: WebApplicationFactory isn't thread-safe on first use, and the tests run in parallel
        // (two at once each built a server, one for Services, another for the clients).
        using (ApiClients.Create(_factory)) { }
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        await _factory.DisposeAsync();
        _catalog.Dispose();
    }

    private BookQueries Queries => _factory.Services.GetRequiredService<BookQueries>();

    private static BookVisibility Everything() =>
        new(new Dictionary<int, IReadOnlyList<FilterProfile>> { [_target.LibraryId] = [new FilterProfile([], [])] });

    private async Task<List<BookListItem>> WalkAsync(BookVisibility visibility, BookSort sort, int? tag = null, int limit = 170)
    {
        var all = new List<BookListItem>();
        string? after = null;
        do
        {
            var page = await Queries.ListAsync(_target.TenantId, visibility, new BookPageRequest(null, sort, after, limit, tag), Cancel);
            Assert.IsLessThanOrEqualTo(limit, page.Items.Count);
            all.AddRange(page.Items);
            after = page.Next;
        }
        while (after is not null);

        return all;
    }

    [TestMethod]
    [DataRow(BookSort.Title)]
    [DataRow(BookSort.Author)]
    [DataRow(BookSort.Added)]
    public async Task WalkingThePagesSeesEveryBookOnceInOrder(BookSort sort)
    {
        var walked = await WalkAsync(Everything(), sort);

        await using var db = await _factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(_target.TenantId, Cancel);
        var books = db.Books.Where(b => b.MergedIntoId == null);
        var expected = await (sort switch
        {
            BookSort.Author => books.OrderBy(b => b.SortAuthor).ThenBy(b => b.Id),
            BookSort.Added => books.OrderByDescending(b => b.Added).ThenByDescending(b => b.Id),
            _ => books.OrderBy(b => b.SortTitle).ThenBy(b => b.Id),
        }).Select(b => b.Id).ToListAsync(Cancel);

        CollectionAssert.AreEqual(expected, walked.Select(b => b.Id).ToList());
    }

    [TestMethod]
    public async Task AListedBookHasItsAuthorsInOrderAndATitleOrItsFileName()
    {
        var walked = await WalkAsync(Everything(), BookSort.Title, limit: 200);

        Assert.IsTrue(walked.All(b => b.Title is not null || !string.IsNullOrEmpty(b.FileName)), "Every book has something to show");
        var planner = new CatalogGenerator(new CatalogShape(Books, 11) { EntriesPerArchive = 400 }, _target);
        foreach (var book in walked.Take(50))
            CollectionAssert.AreEqual(planner.Plan(book.Id).Authors.Select(Names.Author).ToList(), book.Authors.ToList());
    }

    [TestMethod]
    public async Task AFilteredGroupSeesOnlyItsBooks()
    {
        var accounts = _factory.Services.GetRequiredService<AccessService>();
        var user = (await accounts.ListUsersAsync(_target.TenantId, "filter-2", null, cancellationToken: Cancel)).Single();
        var visibility = await _factory.Services.GetRequiredService<BookVisibilities>().GetAsync(new AccessSubject(_target.TenantId, user.Id), Cancel);

        var walked = await WalkAsync(visibility, BookSort.Title);

        await using var db = await _factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(_target.TenantId, Cancel);
        var filterTag = await db.GroupTagFilters.Where(f => f.Group!.Name == "filter-2").Select(f => f.TagId).SingleAsync(Cancel);
        var tagged = await db.BookTags.Where(l => l.TagId == filterTag).Select(l => l.BookId).OrderBy(id => id).ToListAsync(Cancel);
        CollectionAssert.AreEquivalent(tagged, walked.Select(b => b.Id).ToList());
        Assert.IsNotNull(await Queries.GetAsync(_target.TenantId, visibility, tagged[0], Cancel));
        var hidden = await db.Books.Where(b => !tagged.Contains(b.Id)).Select(b => b.Id).FirstAsync(Cancel);
        Assert.IsNull(await Queries.GetAsync(_target.TenantId, visibility, hidden, Cancel), "A filtered-out book is not found");
    }

    [TestMethod]
    public async Task AFieldValuesPageListsItsBooksAndTheValuesWalkInOrderWithTheirCounts()
    {
        var queries = _factory.Services.GetRequiredService<TagQueries>();
        var values = new List<FieldValue>();
        string? after = null;
        do
        {
            var page = (await queries.ValuesAsync(_target.TenantId, "author", _target.LibraryId, after, 200, Cancel))!;
            values.AddRange(page.Items);
            after = page.Next;
        }
        while (after is not null);

        await using var db = await _factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(_target.TenantId, Cancel);
        var authorField = _target.Fields[BuiltInTag.Author];
        var expected = await db.Tags.Where(t => t.FieldId == authorField).OrderBy(t => t.SortName).ThenBy(t => t.Id).Select(t => t.Id).ToListAsync(Cancel);
        CollectionAssert.AreEqual(expected, values.Select(v => v.Id).ToList());

        var busiest = values.MaxBy(v => v.BookCount)!;
        var books = await WalkAsync(Everything(), BookSort.Title, tag: busiest.Id, limit: 25);
        Assert.HasCount(busiest.BookCount!.Value, books);
        Assert.IsTrue(books.All(b => b.Authors.Contains(busiest.Name)));
        Assert.IsNull(await queries.ValuesAsync(_target.TenantId, "no-such-field", null, null, 10, Cancel));
    }

    [TestMethod]
    public async Task TheRoutesAnswerWhatTheCallerMayBrowse()
    {
        var (admin, _) = await ApiClients.SetUpAsync(_factory, Cancel);
        // The setup made a tenant of its own and works there: over to the generated one.
        using (var switched = await admin.PostAsJsonAsync("/api/session/tenant", new TenantChoice(_target.TenantId), ApiClients.Json, Cancel))
            switched.EnsureSuccessStatusCode();
        var page = await admin.GetAsync<Page<BookListItem>>("/api/books?sort=author&limit=10", Cancel);
        Assert.HasCount(10, page.Items);
        var second = await admin.GetAsync<Page<BookListItem>>($"/api/books?sort=author&limit=10&after={page.Next}", Cancel);
        Assert.IsFalse(second.Items.Select(b => b.Id).Intersect(page.Items.Select(b => b.Id)).Any());
        var book = await admin.GetAsync<BookDetail>($"/api/books/{page.Items[0].Id}", Cancel);
        Assert.IsNotEmpty(book.Files);
        var authors = await admin.GetAsync<Page<FieldValue>>($"/api/fields/author/values?library={_target.LibraryId}&limit=5", Cancel);
        Assert.HasCount(5, authors.Items);

        var filtered = await ApiClients.SignInAsync(_factory, "filter-0", GenerateCommand.Password, Cancel);
        var narrow = await filtered.GetAsync<Page<BookListItem>>("/api/books?limit=200", Cancel);
        Assert.IsLessThan(Books / 50, narrow.Items.Count, "0.1 % of the books, give or take");
        using var hidden = await filtered.GetAsync($"/api/books/{page.Items.First(b => !narrow.Items.Any(n => n.Id == b.Id)).Id}", Cancel);
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, hidden.StatusCode);
    }
}
