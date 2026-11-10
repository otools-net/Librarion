using System.Net.Http.Json;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Search;
using Librarion.Core.Tagging;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>
/// Stage 6 chunk 15 (agents/docs/features/search.md#special-terms), on both providers: <c>special:untagged</c> and the
/// stored flag it reads (<see cref="Book.Untagged"/>) kept by every change of plain links; <c>special:duplicates</c> only
/// where the caller may merge books; <c>tag:"name (parent)"</c> for a tag told apart, and the terms facets and
/// suggestions give it.
/// </summary>
[TestClass]
public sealed class SpecialSearchTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string author, string? keywords = null) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info><genre>sf</genre><author><first-name>{author.Split(' ')[0]}</first-name><last-name>{author.Split(' ')[^1]}</last-name></author><book-title>{title}</book-title>{(keywords is null ? "" : $"<keywords>{keywords}</keywords>")}<lang>en</lang></title-info></description><body><p>{title}</p></body></FictionBook>
        """;

    private static async Task<string[]> FindAsync(TestLibrary shelf, string q, CancellationToken cancellationToken)
    {
        var page = await shelf.Admin.GetAsync<Page<BookListItem>>($"/api/books?library={shelf.LibraryId}&limit=50&q={Uri.EscapeDataString(q)}", cancellationToken);
        return page.Items.Select(b => b.Title ?? b.FileName!).ToArray();
    }

    private async Task PostAsync(TestLibrary shelf, string path, object body, CancellationToken cancellationToken)
    {
        using var response = await shelf.Admin.PostAsJsonAsync(path, body, cancellationToken);
        Assert.IsTrue(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}");
    }

    /// <summary>Every book's flag agrees with its links: untagged exactly when it has no plain tag.</summary>
    private async Task AgreesAsync(TestLibrary shelf, string when)
    {
        await using var db = await shelf.DbAsync(Cancel);
        var wrong = await db.Books.Where(b => b.Untagged == db.BookTags.Any(l => l.BookId == b.Id && l.Tag!.FieldId == null && l.Tag.FieldKey == null))
            .Select(b => b.Title).ToListAsync(Cancel);
        Assert.IsEmpty(wrong, $"{when}: the flag disagrees with the links of {string.Join(", ", wrong)}");
    }

    [TestMethod]
    public async Task UntaggedFollowsEveryChangeOfPlainLinks()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("dune.fb2", Fb2("Dune", "Frank Herbert", "space, desert"));
        shelf.Write("solaris.fb2", Fb2("Solaris", "Stanislaw Lem"));
        shelf.Write("ubik.fb2", Fb2("Ubik", "Philip Dick"));
        await shelf.ScanAsync(Cancel);
        await AgreesAsync(shelf, "scanned");
        CollectionAssert.AreEqual(new[] { "Solaris", "Ubik" }, await FindAsync(shelf, "special:untagged", Cancel), "authors, genres and languages don't count");
        CollectionAssert.AreEqual(new[] { "Dune" }, await FindAsync(shelf, "-special:untagged", Cancel));
        CollectionAssert.AreEqual(new[] { "Ubik" }, await FindAsync(shelf, "special:untagged author:dick", Cancel));
        CollectionAssert.AreEqual(Array.Empty<string>(), await FindAsync(shelf, "special:nothing", Cancel), "an unknown special finds nothing");

        var solaris = (await IdsAsync(shelf, "Solaris")).Single();

        // A selection tagged, then the tag taken off.
        await PostAsync(shelf, "/api/books/tags", new { books = new[] { solaris }, add = new[] { new { name = "Ocean" } } }, Cancel);
        await AgreesAsync(shelf, "a selection tagged");
        CollectionAssert.AreEqual(new[] { "Ubik" }, await FindAsync(shelf, "special:untagged", Cancel));
        int ocean;
        await using (var db = await shelf.DbAsync(Cancel))
            ocean = await db.Tags.Where(t => t.Name == "Ocean").Select(t => t.Id).SingleAsync(Cancel);
        await PostAsync(shelf, "/api/books/tags", new { books = new[] { solaris }, remove = new[] { ocean } }, Cancel);
        await AgreesAsync(shelf, "a tag taken off");
        CollectionAssert.AreEqual(new[] { "Solaris", "Ubik" }, await FindAsync(shelf, "special:untagged", Cancel));

        // Default tags given to the books there, then taken back.
        var defaults = shelf.Factory.Services.GetRequiredService<DefaultTags>();
        Assert.AreEqual(AccessStatus.Ok, (await defaults.AddAsync(shelf.TenantId, shelf.LibraryId, null, new BookTagEdit(Name: "Shelved"), existing: true, null, Cancel)).Status);
        await AgreesAsync(shelf, "a default tag given");
        CollectionAssert.AreEqual(Array.Empty<string>(), await FindAsync(shelf, "special:untagged", Cancel));
        int shelved;
        await using (var db = await shelf.DbAsync(Cancel))
            shelved = await db.Tags.Where(t => t.Name == "Shelved").Select(t => t.Id).SingleAsync(Cancel);
        Assert.AreEqual(AccessStatus.Ok, (await defaults.RemoveAsync(shelf.TenantId, shelf.LibraryId, null, shelved, fromBooks: true, null, Cancel)).Status);
        await AgreesAsync(shelf, "a default tag taken back");

        // A tag deleted: Dune keeps its other one, then has none.
        var tags = shelf.Factory.Services.GetRequiredService<TagService>();
        await using (var db = await shelf.DbAsync(Cancel))
        {
            var space = await db.Tags.Where(t => t.Name == "space").Select(t => t.Id).SingleAsync(Cancel);
            var desert = await db.Tags.Where(t => t.Name == "desert").Select(t => t.Id).SingleAsync(Cancel);
            await tags.DeleteAsync(shelf.TenantId, space, null, Cancel);
            await AgreesAsync(shelf, "one tag of two deleted");
            CollectionAssert.AreEqual(new[] { "Solaris", "Ubik" }, await FindAsync(shelf, "special:untagged", Cancel));
            await tags.DeleteAsync(shelf.TenantId, desert, null, Cancel);
        }

        await AgreesAsync(shelf, "its last tag deleted");
        CollectionAssert.AreEqual(new[] { "Dune", "Solaris", "Ubik" }, await FindAsync(shelf, "special:untagged", Cancel));

        // A book read again with keywords (a re-read: its imported links replaced).
        shelf.Write("ubik.fb2", Fb2("Ubik", "Philip Dick", "reality"));
        File.SetLastWriteTimeUtc(Path.Combine(shelf.Root, "ubik.fb2"), DateTime.UtcNow.AddMinutes(5));
        await shelf.ScanAsync(Cancel);
        await AgreesAsync(shelf, "a book read again");
        CollectionAssert.AreEqual(new[] { "Dune", "Solaris" }, await FindAsync(shelf, "special:untagged", Cancel));
    }

    [TestMethod]
    public async Task UntaggedPagesTheSameFromTheIndexAndByTheFlag()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        var titles = new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India" };
        foreach (var (title, i) in titles.Select((t, i) => (t, i)))
            shelf.Write($"{title}.fb2", Fb2(title, "Some Author", i == 4 ? "kept" : null));
        await shelf.ScanAsync(Cancel);

        // A page of 1 of 9 books: the threshold is 3. 8 untagged are walked by the flag, 1 tagged (-special:) read as a list.
        async Task<List<string>> AllAsync(string q)
        {
            var found = new List<string>();
            string? after = null;
            do
            {
                var page = await shelf.Admin.GetAsync<Page<BookListItem>>(
                    $"/api/books?library={shelf.LibraryId}&limit=1&q={Uri.EscapeDataString(q)}{(after is null ? "" : $"&after={Uri.EscapeDataString(after)}")}", Cancel);
                found.AddRange(page.Items.Select(b => b.Title!));
                after = page.Next;
            } while (after is not null && found.Count < 20);
            return found;
        }

        CollectionAssert.AreEqual(titles.Where(t => t != "Echo").ToList(), await AllAsync("special:untagged"), "many: the flag tested on each book walked");
        await PostAsync(shelf, "/api/books/tags", new { books = await IdsAsync(shelf, "Alpha", "Bravo", "Charlie", "Delta", "Foxtrot", "Golf"), add = new[] { new { name = "Read" } } }, Cancel);
        CollectionAssert.AreEqual(new[] { "Hotel", "India" }, await AllAsync("special:untagged"), "few: read from the index of the untagged books");
    }

    private async Task<int[]> IdsAsync(TestLibrary shelf, params string[] titles)
    {
        await using var db = await shelf.DbAsync(Cancel);
        return await db.Books.Where(b => titles.Contains(b.Title!)).Select(b => b.Id).ToArrayAsync(Cancel);
    }

    [TestMethod]
    public async Task DuplicatesAreFoundOnlyWhereTheCallerMayMerge()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("dune.fb2", Fb2("Dune", "Frank Herbert"));
        shelf.Write("dune-again.fb2", Fb2("Dune", "Frank Herbert") + " ");
        shelf.Write("ubik.fb2", Fb2("Ubik", "Philip Dick"));
        await shelf.ScanAsync(Cancel);

        CollectionAssert.AreEqual(new[] { "Dune", "Dune" }, await FindAsync(shelf, "special:duplicates", Cancel), "the same title and author: a stored group");
        CollectionAssert.AreEqual(new[] { "Ubik" }, await FindAsync(shelf, "-special:duplicates", Cancel));

        var queries = shelf.Factory.Services.GetRequiredService<BookQueries>();
        var open = new Dictionary<int, IReadOnlyList<FilterProfile>> { [shelf.LibraryId] = [new FilterProfile([], [])] };
        var request = new BookPageRequest(shelf.LibraryId, Query: "special:duplicates");
        Assert.IsEmpty((await queries.ListAsync(shelf.TenantId, new BookVisibility(open), request, Cancel)).Items, "no merge right: nothing");
        Assert.HasCount(2, (await queries.ListAsync(shelf.TenantId, new BookVisibility(open, new HashSet<int> { shelf.LibraryId }), request, Cancel)).Items);

        var suggestions = shelf.Factory.Services.GetRequiredService<SearchSuggestions>();
        CollectionAssert.AreEqual(new[] { "special:untagged", "special:duplicates" },
            (await suggestions.SuggestAsync(shelf.TenantId, "spec", shelf.LibraryId, new BookVisibility(open, new HashSet<int> { shelf.LibraryId }), Cancel)).Items
            .Where(i => i.Kind == SuggestionKind.Special).Select(i => i.Insert).ToList());
        CollectionAssert.AreEqual(new[] { "-special:untagged" },
            (await suggestions.SuggestAsync(shelf.TenantId, "-special:un", shelf.LibraryId, new BookVisibility(open), Cancel)).Items.Select(i => i.Insert).ToList());
        Assert.IsEmpty((await suggestions.SuggestAsync(shelf.TenantId, "special:dup", shelf.LibraryId, new BookVisibility(open), Cancel)).Items,
            "not offered without the right");
    }

    [TestMethod]
    public async Task ATagToldApartIsFoundByItsNameAndParent()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("bear.fb2", Fb2("Five Nights", "Scott Cawthon"));
        shelf.Write("singer.fb2", Fb2("Bohemian Rhapsody", "Freddie Mercury"));
        await shelf.ScanAsync(Cancel);

        var tags = shelf.Factory.Services.GetRequiredService<TagService>();
        var fnaf = (await tags.CreateAsync(shelf.TenantId, new TagInput("FNAF"), Cancel)).Value!.Id;
        var queen = (await tags.CreateAsync(shelf.TenantId, new TagInput("Queen"), Cancel)).Value!.Id;
        var bear = (await tags.CreateAsync(shelf.TenantId, new TagInput("Freddy", DisambiguationParentId: fnaf, ParentIds: [fnaf]), Cancel)).Value!.Id;
        var singer = (await tags.CreateAsync(shelf.TenantId, new TagInput("Freddy", DisambiguationParentId: queen, ParentIds: [queen]), Cancel)).Value!.Id;
        await PostAsync(shelf, "/api/books/tags", new { books = await IdsAsync(shelf, "Five Nights"), add = new[] { new { id = bear } } }, Cancel);
        await PostAsync(shelf, "/api/books/tags", new { books = await IdsAsync(shelf, "Bohemian Rhapsody"), add = new[] { new { id = singer } } }, Cancel);

        CollectionAssert.AreEqual(new[] { "Five Nights" }, await FindAsync(shelf, "tag:\"Freddy (FNAF)\"", Cancel));
        CollectionAssert.AreEqual(new[] { "Bohemian Rhapsody" }, await FindAsync(shelf, "tag:\"freddy (queen)\"", Cancel), "case aside");
        CollectionAssert.AreEqual(new[] { "Bohemian Rhapsody", "Five Nights" }, await FindAsync(shelf, "tag:Freddy", Cancel), "the name alone: both");
        CollectionAssert.AreEqual(new[] { "Bohemian Rhapsody" }, await FindAsync(shelf, "-tag:\"Freddy (FNAF)\"", Cancel));
        CollectionAssert.AreEqual(Array.Empty<string>(), await FindAsync(shelf, "tag:\"Freddy (Mercury)\"", Cancel), "another parent: none");

        var suggested = await shelf.Admin.GetAsync<SuggestionList>($"/api/search/suggestions?library={shelf.LibraryId}&q=tag:fredd", Cancel);
        CollectionAssert.AreEquivalent(new[] { "tag:\"Freddy (FNAF)\"", "tag:\"Freddy (Queen)\"" }, suggested.Items.Select(i => i.Insert).ToList());
        var facets = await shelf.Admin.GetAsync<FacetList>($"/api/search/facets?library={shelf.LibraryId}&q={Uri.EscapeDataString("tag:Freddy")}", Cancel);
        var plain = facets.Fields.Single(f => f.Field == "tag").Values;
        Assert.AreEqual("tag:\"Freddy (FNAF)\"", plain.Single(v => v.Id == bear).Term);
        Assert.AreEqual("Freddy (Queen)", plain.Single(v => v.Id == singer).Name);
    }
}
