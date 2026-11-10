using System.Net;
using Librarion.Core.Tagging;
using Librarion.Tests.Infrastructure;
using System.Net.Http.Json;

namespace Librarion.Tests;

/// <summary>
/// The shared tag picker's lookup over HTTP, on both providers (agents/docs/features/tags.md, stage 6 chunk 5): by name
/// prefix, other name, shorthand and words, scoped to a field, the typed name first and the most used next.
/// </summary>
[TestClass]
public sealed class TagPickerTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static string Fb2(string title, string[] authors, string? keywords = null) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FictionBook><description><title-info>{string.Concat(authors.Select(a => $"<author><first-name>{a.Split(' ')[0]}</first-name><last-name>{a.Split(' ')[^1]}</last-name></author>"))}<book-title>{title}</book-title>{(keywords is null ? "" : $"<keywords>{keywords}</keywords>")}<lang>en</lang></title-info></description><body><p>{title}</p></body></FictionBook>
        """;

    [TestMethod]
    public async Task ThePickerFindsTagsByNameOtherNameShorthandAndWords()
    {
        await using var shelf = await Shelf.CreateAsync(Cancel);
        shelf.Write("one.fb2", Fb2("One", ["Stephen King", "Stephen Baxter"], "Fantasy"));
        shelf.Write("two.fb2", Fb2("Two", ["Stephen King"], "Fantasy, Fantastic voyages"));
        await shelf.ScanAsync(Cancel);
        using (var made = await shelf.Admin.PostAsJsonAsync("/api/tags", new TagInput("Speculative fiction", Shorthand: "SF", Aliases: ["Weird tales"]), Cancel))
            Assert.AreEqual(HttpStatusCode.Created, made.StatusCode, await made.Content.ReadAsStringAsync(Cancel));

        // A field's values by prefix, the most used first; by a word of the name.
        var stephens = await PickAsync(shelf, "author", "steph");
        CollectionAssert.AreEqual(new[] { "Stephen King", "Stephen Baxter" }, stephens.Items.Select(t => t.Name).ToList());
        Assert.AreEqual(2, stephens.Items[0].Books);
        Assert.AreEqual("author", stephens.Items[0].Field);
        Assert.IsFalse(stephens.Exact);
        CollectionAssert.AreEqual(new[] { "Stephen King" }, (await PickAsync(shelf, "author", "king")).Items.Select(t => t.Name).ToList());

        // Plain tags: the typed name first (no Create then), a shorthand in either case, another name shown when found by it.
        var fantasy = await PickAsync(shelf, null, "fantas");
        CollectionAssert.AreEqual(new[] { "Fantasy", "Fantastic voyages" }, fantasy.Items.Select(t => t.Name).ToList());
        var exact = await PickAsync(shelf, null, "fantastic voyages");
        Assert.IsTrue(exact.Exact);
        Assert.AreEqual("Fantastic voyages", exact.Items[0].Name);
        Assert.AreEqual("Speculative fiction", (await PickAsync(shelf, null, "sf")).Items.Single().Name);
        var weird = (await PickAsync(shelf, null, "weird")).Items.Single();
        Assert.AreEqual(("Speculative fiction", "Weird tales"), (weird.Name, weird.Alias));
        Assert.IsNull((await PickAsync(shelf, null, "specul")).Items.Single().Alias);

        // Scoped: an author is no plain tag; nothing typed lists the first by sort name; an unknown field is a 404.
        Assert.AreEqual(0, (await PickAsync(shelf, null, "steph")).Items.Count);
        CollectionAssert.AreEqual(new[] { "Stephen Baxter", "Stephen King" }, (await PickAsync(shelf, "author", "")).Items.Select(t => t.Name).ToList());
        using var unknown = await shelf.Admin.GetAsync("/api/tags/pick?field=nope&q=x", Cancel);
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private async Task<PickedTags> PickAsync(Shelf shelf, string? field, string q) =>
        await shelf.Admin.GetAsync<PickedTags>($"/api/tags/pick?q={Uri.EscapeDataString(q)}{(field is null ? "" : $"&field={field}")}", Cancel);
}
