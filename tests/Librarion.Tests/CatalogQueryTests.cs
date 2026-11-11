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
    [DataRow(BookSort.Title)]
    [DataRow(BookSort.Author)]
    [DataRow(BookSort.Added)]
    public async Task ASearchWalksEveryMatchOnceInOrderWhereverItStarts(BookSort sort)
    {
        // A title word on a few hundred books: under √(page × books) for big pages (the matches first, then sorted),
        // over it for small ones (the sort index walked, each book tested).
        var visibility = Everything();
        List<(int Id, string Text)> all;
        await using (var db = await _factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(_target.TenantId, Cancel))
        {
            var ordered = sort switch
            {
                BookSort.Author => db.Books.OrderBy(b => b.SortAuthor).ThenBy(b => b.Id),
                BookSort.Added => db.Books.OrderByDescending(b => b.Added).ThenByDescending(b => b.Id),
                _ => db.Books.OrderBy(b => b.SortTitle).ThenBy(b => b.Id),
            };
            all = (await ordered.Where(b => b.MergedIntoId == null && b.MissingSince == null).Select(b => new { b.Id, b.SearchText }).ToListAsync(Cancel))
                .Select(b => (b.Id, b.SearchText)).ToList();
        }

        var word = all.SelectMany(b => b.Text.Split(' ').Distinct()).Where(w => w.Length > 3).GroupBy(w => w)
            .Select(g => (Word: g.Key, Count: g.Count())).OrderBy(g => Math.Abs(g.Count - 250)).First();
        if (word.Count <= Math.Sqrt(5 * Books) || word.Count >= Math.Sqrt(200 * Books))
            Assert.Inconclusive($"no word between the two plans' sizes (closest: {word})");
        var expected = all.Where(b => b.Text.Split(' ').Any(w => w.StartsWith(word.Word, StringComparison.Ordinal))).Select(b => b.Id).ToList();

        foreach (var limit in new[] { 5, 200 })
        {
            var found = new List<int>();
            string? after = null;
            do
            {
                var page = await Queries.ListAsync(_target.TenantId, visibility, new BookPageRequest(null, sort, after, limit, Query: $"title:{word.Word}*"), Cancel);
                found.AddRange(page.Items.Select(b => b.Id));
                after = page.Next;
            }
            while (after is not null);

            CollectionAssert.AreEqual(expected, found, $"pages of {limit}");
        }
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
        var expected = await db.Tags.Where(t => t.FieldId == authorField && db.TagCounts.Any(c => c.TagId == t.Id && c.LibraryId == _target.LibraryId && c.BookCount > 0))
            .OrderBy(t => t.SortName).ThenBy(t => t.Id).Select(t => t.Id).ToListAsync(Cancel);
        CollectionAssert.AreEqual(expected, values.Select(v => v.Id).ToList());

        var busiest = values.MaxBy(v => v.BookCount)!;
        var books = await WalkAsync(Everything(), BookSort.Title, tag: busiest.Id, limit: 25);
        Assert.HasCount(busiest.BookCount!.Value, books);
        Assert.IsTrue(books.All(b => b.Authors.Contains(busiest.Name)));
        Assert.IsNull(await queries.ValuesAsync(_target.TenantId, "no-such-field", null, null, 10, Cancel));
    }

    [TestMethod]
    public async Task PlainTagsWithoutBooksInTheLibraryAreSkippedInShortPagesThatStillWalkToTheEnd()
    {
        var queries = _factory.Services.GetRequiredService<TagQueries>();
        await using var db = await _factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(_target.TenantId, Cancel);
        // More tags without a book than one request reads, side by side in the order.
        db.Tags.AddRange(Enumerable.Range(0, TagQueries.MaxScanned + 100).Select(i => new Tag
        {
            TenantId = _target.TenantId, Name = $"Zzzz unused {i:0000}", NormalizedName = $"zzzz unused {i:0000}", SortName = $"Zzzz unused {i:0000}",
        }));
        await db.SaveChangesAsync(Cancel);

        var walked = new List<FieldValue>();
        var shortPage = false;
        string? after = null;
        do
        {
            var page = await queries.PlainTagsAsync(_target.TenantId, _target.LibraryId, after, 50, Cancel);
            shortPage |= page.Next is not null && page.Items.Count < 50;
            walked.AddRange(page.Items);
            after = page.Next;
        }
        while (after is not null);

        var expected = await db.Tags.Where(t => t.FieldId == null && t.FieldKey == null)
            .Join(db.TagCounts.Where(c => c.LibraryId == _target.LibraryId && c.BookCount > 0), t => t.Id, c => c.TagId, (t, c) => new { t.Id, t.SortName, c.BookCount })
            .OrderBy(t => t.SortName).ThenBy(t => t.Id).ToListAsync(Cancel);
        CollectionAssert.AreEqual(expected.Select(t => t.Id).ToList(), walked.Select(v => v.Id).ToList());
        CollectionAssert.AreEqual(expected.Select(t => (int?)t.BookCount).ToList(), walked.Select(v => v.BookCount).ToList());
        Assert.IsTrue(shortPage, "The unused tags were read over more than one request");

        var everything = await queries.PlainTagsAsync(_target.TenantId, null, null, 10, Cancel, from: "zzzz unused 0050");
        Assert.AreEqual("Zzzz unused 0050", everything.Items[0].Name, "Without a library every tag is listed; a jump starts at its first letter upper case");
        Assert.IsNull(everything.Items[0].BookCount);
    }

    [TestMethod]
    public async Task ATagShowsItsFieldParentsChildrenAndCountAndADeniedOneIsLeftOut()
    {
        var queries = _factory.Services.GetRequiredService<TagQueries>();
        await using var db = await _factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(_target.TenantId, Cancel);
        var link = await db.TagParents.OrderBy(p => p.ParentId).ThenBy(p => p.ChildId).FirstAsync(Cancel);
        var children = await db.TagParents.Where(p => p.ParentId == link.ParentId).Select(p => p.ChildId).ToListAsync(Cancel);
        // A tag's page counts its books with those of the tags under it (stage 6, 4a).
        var count = await db.TagCounts.Where(c => c.TagId == link.ParentId && c.LibraryId == _target.LibraryId).Select(c => c.TotalCount).FirstOrDefaultAsync(Cancel);

        var parent = (await queries.GetAsync(_target.TenantId, link.ParentId, _target.LibraryId, Everything(), Cancel))!;
        Assert.AreEqual("category", parent.Field);
        Assert.AreEqual(count, parent.BookCount);
        CollectionAssert.AreEquivalent(children.Take(TagQueries.MaxChildren).ToList(), parent.Children.Select(c => c.Id).ToList());
        var child = (await queries.GetAsync(_target.TenantId, link.ChildId, null, null, Cancel))!;
        Assert.IsNull(child.BookCount);
        CollectionAssert.Contains(child.Parents.Select(p => p.Id).ToList(), link.ParentId);
        Assert.IsNull(await queries.GetAsync(_target.TenantId, _target.Fields[BuiltInTag.Category], null, null, Cancel), "A field is a list, not a tag page");

        // Denied by the only group admitting the library: the tag and those under it are gone from its pages.
        var denying = new BookVisibility(new Dictionary<int, IReadOnlyList<FilterProfile>> { [_target.LibraryId] = [new FilterProfile([], [link.ParentId])] });
        Assert.IsNull(await queries.GetAsync(_target.TenantId, link.ParentId, _target.LibraryId, denying, Cancel));
        Assert.IsNull(await queries.GetAsync(_target.TenantId, link.ChildId, _target.LibraryId, denying, Cancel));
        var listed = new List<int>();
        string? after = null;
        do
        {
            var page = (await queries.ValuesAsync(_target.TenantId, "category", _target.LibraryId, after, 200, Cancel, visibility: denying))!;
            listed.AddRange(page.Items.Select(v => v.Id));
            after = page.Next;
        }
        while (after is not null);
        Assert.IsNotEmpty(listed);
        Assert.IsFalse(listed.Intersect([link.ParentId, .. children]).Any());
        // A second group that doesn't deny it admits it again.
        var both = new BookVisibility(new Dictionary<int, IReadOnlyList<FilterProfile>>
        {
            [_target.LibraryId] = [new FilterProfile([], [link.ParentId]), new FilterProfile([link.ChildId], [])],
        });
        Assert.IsNotNull(await queries.GetAsync(_target.TenantId, link.ParentId, _target.LibraryId, both, Cancel));
    }

    [TestMethod]
    public async Task ASeriesPageWalksItsBooksByNumber()
    {
        await using var db = await _factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(_target.TenantId, Cancel);
        var seriesField = _target.Fields[BuiltInTag.Series];
        var series = await db.TagCounts.Where(c => db.Tags.Any(t => t.Id == c.TagId && t.FieldId == seriesField))
            .OrderByDescending(c => c.BookCount).Select(c => c.TagId).FirstAsync(Cancel);
        var expected = await db.BookTags.Where(l => l.TagId == series && l.Number != null)
            .OrderBy(l => l.Number).ThenBy(l => l.BookId).Select(l => l.BookId).ToListAsync(Cancel);

        var walked = await WalkAsync(Everything(), BookSort.Number, tag: series, limit: 3);

        Assert.IsGreaterThan(3, expected.Count, "A series of several pages");
        CollectionAssert.AreEqual(expected, walked.Select(b => b.Id).ToList());
    }

    [TestMethod]
    public async Task TheRoutesAnswerWhatTheCallerMayBrowse()
    {
        // The generated catalog has its server administrator already (the API is set up): signed in, over to its tenant.
        using var admin = await ApiClients.SignInAsync(_factory, "admin", GenerateCommand.Password, Cancel);
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
        Assert.IsTrue(authors.Items.All(a => a.BookCount > 0));
        var fields = await admin.GetAsync<List<FieldInfo>>("/api/fields", Cancel);
        Assert.AreEqual("author", fields[0].Key);
        Assert.IsTrue(fields.Take(9).All(f => f.BuiltIn));
        var tags = await admin.GetAsync<Page<FieldValue>>($"/api/tags?library={_target.LibraryId}&limit=5", Cancel);
        Assert.IsNotEmpty(tags.Items);
        var tag = await admin.GetAsync<TagDetail>($"/api/tags/{authors.Items[0].Id}?library={_target.LibraryId}", Cancel);
        Assert.AreEqual(authors.Items[0].BookCount, tag.BookCount);
        Assert.AreEqual("author", tag.Field);
        foreach (var address in new[] { "/api/tags?library=999999", "/api/fields/author/values?library=999999", $"/api/tags/{tag.Id}?library=999999", "/api/tags/999999999" })
        {
            using var missing = await admin.GetAsync(address, Cancel);
            Assert.AreEqual(System.Net.HttpStatusCode.NotFound, missing.StatusCode, address);
        }

        var filtered = await ApiClients.SignInAsync(_factory, "filter-0", GenerateCommand.Password, Cancel);
        var narrow = await filtered.GetAsync<Page<BookListItem>>("/api/books?limit=200", Cancel);
        Assert.IsLessThan(Books / 50, narrow.Items.Count, "0.1 % of the books, give or take");
        using var hidden = await filtered.GetAsync($"/api/books/{page.Items.First(b => !narrow.Items.Any(n => n.Id == b.Id)).Id}", Cancel);
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, hidden.StatusCode);
    }
}
