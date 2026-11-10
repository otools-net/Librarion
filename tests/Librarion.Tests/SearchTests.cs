using System.IO.Compression;
using System.Net;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Search;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>
/// The search language over scanned books, on both providers (agents/docs/features/search.md): words in titles and
/// in names of authors and other tags, phrases, prefixes, fields by name or word, `field:` alone, negation, formats,
/// file names, other scripts, hidden tags; and what the search box suggests.
/// </summary>
[TestClass]
public sealed class SearchTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string author, string? series = null, int number = 0, string genre = "sf_fantasy") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info><genre>{genre}</genre><author><first-name>{author.Split(' ')[0]}</first-name><last-name>{author.Split(' ')[^1]}</last-name></author><book-title>{title}</book-title>{(series is null ? "" : $"<sequence name=\"{series}\" number=\"{number}\"/>")}<lang>en</lang></title-info></description><body><p>{title}</p></body></FictionBook>
        """;

    [TestMethod]
    public void TheLanguageReadsFieldsPhrasesPrefixesAndNegation()
    {
        static bool IsField(string key) => key is "author" or "series";
        var terms = SearchQuery.Parse("hobb \"the dark tower\" -author:\"Stephen King\" series: tag:fav* format:EPUB Re:Zero -", IsField).Terms;

        Assert.AreEqual(new SearchTerm(SearchTermKind.Text, false, null, "hobb", false, true), terms[0], "a bare word may still be typed");
        Assert.AreEqual(new SearchTerm(SearchTermKind.Text, false, null, "the dark tower", true, false), terms[1]);
        Assert.AreEqual(new SearchTerm(SearchTermKind.Field, true, "author", "Stephen King", true, false), terms[2]);
        Assert.AreEqual(new SearchTerm(SearchTermKind.Field, false, "series", "", false, false), terms[3], "a field alone: any value");
        Assert.AreEqual(new SearchTerm(SearchTermKind.Tag, false, null, "fav", false, true), terms[4]);
        Assert.AreEqual(new SearchTerm(SearchTermKind.Format, false, null, "EPUB", false, false), terms[5]);
        Assert.AreEqual(new SearchTerm(SearchTermKind.Text, false, null, "Re:Zero", false, true), terms[6], "no field called re: a title");
        Assert.HasCount(7, terms, "a lone - is nothing");
        Assert.IsTrue(SearchQuery.Parse("author:\"Steph", IsField).Terms.Single().Prefix, "an open quote is still being typed");
        Assert.HasCount(SearchQuery.MaxTerms, SearchQuery.Parse(string.Join(' ', Enumerable.Range(0, 50).Select(i => $"w{i}")), IsField).Terms);
    }

    [TestMethod]
    public void TextFoldsCaseAccentsAndPunctuation()
    {
        Assert.AreEqual("cafe society l ete", SearchText.Fold("Café  Society — L'Été"));
        Assert.AreEqual("солнце и елка", SearchText.Fold("Солнце и Ёлка"), "Cyrillic is kept, its case folded, ё read as е");
        Assert.AreEqual("hobbit 123456", SearchText.ForBook("The.Hobbit", "123456").Replace("the ", ""));
        Assert.AreEqual("dune", SearchText.ForBook("Dune", "dune"), "the file name once when the same");
        CollectionAssert.AreEqual(new[] { "josé", "saramago" }, SearchText.Spelled("José Saramago").ToList());
    }

    [TestMethod]
    public async Task EveryKindOfTermFindsItsBooks()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("hobbit.fb2", Fb2("The Hobbit", "John Tolkien", "Middle-earth", 1));
        shelf.Write("rings.fb2", Fb2("The Lord of the Rings", "John Tolkien", "Middle-earth", 2));
        shelf.Write("dune.fb2", Fb2("Dune", "Frank Herbert", genre: "sf"));
        shelf.Zip("lib/books.zip", CompressionLevel.Optimal,
            ("solaris.fb2", Fb2("Солярис", "Станислав Лем")),
            ("cafe.fb2", Fb2("Café Society", "José Saramago", genre: "prose_classic")),
            ("123456.fb2", Fb2("", "Nobody Known")));
        await shelf.ScanAsync(Cancel);

        // Dune is archived: a hidden tag.
        await using (var db = await shelf.DbAsync(Cancel))
        {
            var dune = await db.Books.SingleAsync(b => b.Title == "Dune", Cancel);
            var archived = await db.Tags.SingleAsync(t => t.BuiltIn == BuiltInTag.Archived, Cancel);
            db.BookTags.Add(new BookTag { TenantId = shelf.TenantId, BookId = dune.Id, TagId = archived.Id, Source = TagSource.Manual });
            await db.SaveChangesAsync(Cancel);
        }

        async Task<string[]> Find(string q)
        {
            var page = await shelf.Admin.GetAsync<Page<BookListItem>>($"/api/books?library={shelf.LibraryId}&limit=50&q={Uri.EscapeDataString(q)}", Cancel);
            return page.Items.Select(b => b.Title ?? b.FileName!).ToArray();
        }

        CollectionAssert.AreEqual(new[] { "The Hobbit" }, await Find("hobbit"));
        CollectionAssert.AreEqual(new[] { "The Hobbit" }, await Find("HOBB"), "a prefix, case aside");
        CollectionAssert.AreEqual(new[] { "The Hobbit", "The Lord of the Rings" }, await Find("tolkien"), "an author's name");
        CollectionAssert.AreEqual(new[] { "The Lord of the Rings" }, await Find("tolkien rings"), "every word, title or name");
        CollectionAssert.AreEqual(new[] { "The Lord of the Rings" }, await Find("\"lord of the\""));
        CollectionAssert.AreEqual(Array.Empty<string>(), await Find("\"of the lord\""), "a phrase keeps its order");
        CollectionAssert.AreEqual(new[] { "The Hobbit", "The Lord of the Rings" }, await Find("author:\"John Tolkien\""));
        CollectionAssert.AreEqual(new[] { "The Hobbit", "The Lord of the Rings" }, await Find("author:tolkien"), "a field's values by word");
        CollectionAssert.AreEqual(new[] { "Café Society", "The Hobbit", "The Lord of the Rings" }, await Find("author:jo*"), "by prefix");
        CollectionAssert.AreEqual(new[] { "The Hobbit", "The Lord of the Rings" }, await Find("series:"));
        CollectionAssert.AreEqual(new[] { "123456", "Café Society", "Солярис" }, await Find("-series:"));
        CollectionAssert.AreEqual(new[] { "123456", "Café Society", "Солярис" }, await Find("-tolkien"));
        CollectionAssert.AreEqual(new[] { "Café Society" }, await Find("cafe saramago"), "accents aside in titles");
        CollectionAssert.AreEqual(new[] { "Солярис" }, await Find("солярис лем"));
        CollectionAssert.AreEqual(new[] { "123456" }, await Find("123456"), "a book without a title by its file name");
        CollectionAssert.AreEqual(new[] { "Café Society" }, await Find("category:\"Classic prose\""));
        CollectionAssert.AreEqual(Array.Empty<string>(), await Find("format:epub"));
        Assert.HasCount(5, await Find("format:fb2"));
        CollectionAssert.AreEqual(Array.Empty<string>(), await Find("dune"), "archived: hidden from searches");
        CollectionAssert.AreEqual(new[] { "Dune" }, await Find("dune tag:Archived"), "unless the search names the tag");
        CollectionAssert.AreEqual(new[] { "The Hobbit" }, await Find("the:hobbit"), "an unknown key is text");

        // Within a series by its numbers too.
        int series;
        await using (var db = await shelf.DbAsync(Cancel))
            series = await db.Tags.Where(t => t.Name == "Middle-earth").Select(t => t.Id).SingleAsync(Cancel);
        var numbered = await shelf.Admin.GetAsync<Page<BookListItem>>($"/api/books?library={shelf.LibraryId}&tag={series}&sort=number&q=rings", Cancel);
        CollectionAssert.AreEqual(new[] { "The Lord of the Rings" }, numbered.Items.Select(b => b.Title).ToList());
    }

    [TestMethod]
    public async Task FacetsCountTheMatchesAsTheirTermsFindThem()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("hobbit.fb2", Fb2("The Hobbit", "John Tolkien", "Middle-earth", 1));
        shelf.Write("rings.fb2", Fb2("The Lord of the Rings", "John Tolkien", "Middle-earth", 2));
        shelf.Write("dune.fb2", Fb2("Dune", "Frank Herbert", genre: "sf"));
        shelf.Write("cafe.fb2", Fb2("Café Society", "José Saramago", genre: "prose_classic"));
        // Two categories under one parent: counted once under it.
        shelf.Write("both.fb2", Fb2("Tolkien and Saramago", "Anne Editor", genre: "sf_fantasy</genre><genre>prose_classic"));
        await shelf.ScanAsync(Cancel);

        await using (var db = await shelf.DbAsync(Cancel))
        {
            var category = await db.Tags.SingleAsync(t => t.FieldKey == "category", Cancel);
            var children = await db.Tags.Where(t => t.FieldId == category.Id && (t.Name == "Fantasy" || t.Name == "Classic prose")).ToListAsync(Cancel);
            var parent = new Tag { TenantId = shelf.TenantId, Name = "Fiction", NormalizedName = "fiction", SortName = "Fiction", FieldId = category.Id };
            db.Tags.Add(parent);
            await db.SaveChangesAsync(Cancel);
            db.TagClosure.AddRange(
                new TagClosure { TenantId = shelf.TenantId, AncestorId = parent.Id, DescendantId = parent.Id, Depth = 0 },
                new TagClosure { TenantId = shelf.TenantId, AncestorId = category.Id, DescendantId = parent.Id, Depth = 1 });
            foreach (var child in children)
            {
                db.TagParents.Add(new TagParent { TenantId = shelf.TenantId, ChildId = child.Id, ParentId = parent.Id });
                db.TagClosure.Add(new TagClosure { TenantId = shelf.TenantId, AncestorId = parent.Id, DescendantId = child.Id, Depth = 1 });
            }

            await db.SaveChangesAsync(Cancel);
        }

        async Task<FacetList> Facets(string? q) =>
            await shelf.Admin.GetAsync<FacetList>($"/api/search/facets?library={shelf.LibraryId}&q={Uri.EscapeDataString(q ?? "")}", Cancel);
        async Task<int> Count(string q) =>
            (await shelf.Admin.GetAsync<Page<BookListItem>>($"/api/books?library={shelf.LibraryId}&limit=200&q={Uri.EscapeDataString(q)}", Cancel)).Items.Count;
        static FacetValue Value(FacetList list, string field, string name) => list.Fields.Single(f => f.Field == field).Values.Single(v => v.Name == name);

        var tolkien = await Facets("tolkien");
        Assert.AreEqual(3, tolkien.Counted, "two by him, one with his name in the title");
        Assert.IsFalse(tolkien.Capped);
        Assert.AreEqual(2, Value(tolkien, "author", "John Tolkien").Count);
        Assert.AreEqual("author:\"John Tolkien\"", Value(tolkien, "author", "John Tolkien").Term);
        Assert.AreEqual(3, Value(tolkien, "format", "fb2").Count);
        Assert.AreEqual(3, Value(tolkien, "category", "Fantasy").Count);
        Assert.AreEqual("category", tolkien.Fields[0].Field, "categories first");
        foreach (var value in tolkien.Fields.SelectMany(f => f.Values))
            Assert.AreEqual(value.Count, await Count($"tolkien {value.Term}"), value.Term);

        var fiction = await Facets("category:Fiction");
        Assert.AreEqual(4, fiction.Counted);
        Assert.AreEqual(4, Value(fiction, "category", "Fiction").Count, "the book in both counted once");
        foreach (var value in fiction.Fields.SelectMany(f => f.Values))
            Assert.AreEqual(value.Count, await Count($"category:Fiction {value.Term}"), value.Term);

        Assert.AreEqual(0, (await Facets("nothing-like-this")).Counted);
        var others = await Facets("-tolkien");
        Assert.AreEqual(2, others.Counted, "only terms leaving books out");
        Assert.AreEqual(1, Value(others, "author", "Frank Herbert").Count);

        // The library seen whole: the stored counts (no formats there).
        var library = await Facets(null);
        Assert.AreEqual(5, library.Counted);
        Assert.AreEqual(2, Value(library, "author", "John Tolkien").Count);
        Assert.AreEqual(2, Value(library, "series", "Middle-earth").Count);
        Assert.IsFalse(library.Fields.Any(f => f.Field == "format"));

        // Behind a content filter: only Fantasy (read from its tag, unordered) or all but it (the title order walked).
        var facets = shelf.Factory.Services.GetRequiredService<SearchFacets>();
        var fantasy = Value(tolkien, "category", "Fantasy").Id!.Value;
        BookVisibility Filter(FilterProfile profile) => new(new Dictionary<int, IReadOnlyList<FilterProfile>> { [shelf.LibraryId] = [profile] });
        var allowed = await facets.ListAsync(shelf.TenantId, Filter(new FilterProfile([fantasy], [])), shelf.LibraryId, null, Cancel);
        Assert.AreEqual(3, allowed.Counted);
        Assert.AreEqual(2, Value(allowed, "author", "John Tolkien").Count);
        var denied = await facets.ListAsync(shelf.TenantId, Filter(new FilterProfile([], [fantasy])), shelf.LibraryId, null, Cancel);
        Assert.AreEqual(2, denied.Counted);
        Assert.IsFalse(denied.Fields.Any(f => f.Values.Any(v => v.Name == "John Tolkien")));

        // Past the cap: the first matches counted, marked.
        shelf.Factory.Services.GetRequiredService<SearchFacets>().Cap = 1;
        var capped = await Facets("tolkien");
        Assert.AreEqual(1, capped.Counted);
        Assert.IsTrue(capped.Capped);
        Assert.IsFalse((await Facets("dune")).Capped, "one match: all of them");

        Assert.AreEqual(HttpStatusCode.NotFound, (await shelf.Admin.GetAsync($"/api/search/facets?library={shelf.LibraryId + 100}", Cancel)).StatusCode);
    }

    [TestMethod]
    public async Task TheSearchBoxSuggestsFieldsThenNamesByUse()
    {
        await using var shelf = await TestLibrary.CreateAsync(Cancel);
        shelf.Write("hobbit.fb2", Fb2("The Hobbit", "John Tolkien"));
        shelf.Write("rings.fb2", Fb2("The Lord of the Rings", "John Tolkien"));
        shelf.Write("cafe.fb2", Fb2("Café Society", "José Saramago"));
        await shelf.ScanAsync(Cancel);

        async Task<SuggestionList> Suggest(string q) =>
            await shelf.Admin.GetAsync<SuggestionList>($"/api/search/suggestions?library={shelf.LibraryId}&q={Uri.EscapeDataString(q)}", Cancel);

        var tolk = await Suggest("the tolk");
        Assert.AreEqual(4, tolk.From, "the last term is replaced");
        var tolkien = tolk.Items.Single();
        Assert.AreEqual("author:\"John Tolkien\"", tolkien.Insert);
        Assert.AreEqual(2, tolkien.BookCount);
        var fields = await Suggest("aut");
        Assert.AreEqual(new SearchSuggestion(SuggestionKind.Field, "author:", "author", null, null), fields.Items[0]);
        var authors = await Suggest("author:jo");
        CollectionAssert.AreEqual(new[] { "John Tolkien", "José Saramago" }, authors.Items.Select(i => i.Name).ToList(), "most books first");
        Assert.AreEqual("-author:\"John Tolkien\"", (await Suggest("-tolk")).Items.Single().Insert, "a negated term stays negated");
        Assert.IsEmpty((await Suggest("t")).Items, "one letter is too little");
        Assert.IsEmpty((await Suggest("hobbit ")).Items, "nothing being typed");
    }
}
