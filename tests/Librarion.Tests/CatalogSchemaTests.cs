using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>The catalog and tag tables (agents/docs/features/tags.md, metadata-tags.md).</summary>
[TestClass]
public sealed class CatalogSchemaTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    [TestMethod]
    public async Task ANewTenantHasTheBuiltInTagsAndFieldsEachItsOwnAncestor()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using (ApiClients.Create(factory)) { }
        var tenant = (await factory.Services.GetRequiredService<AccessService>().CreateTenantAsync(new TenantInput("TestLibrary"), Cancel)).Value!;

        await using var db = await factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(tenant.Id, Cancel);
        var tags = await db.Tags.ToListAsync(Cancel);
        var closure = await db.TagClosure.ToListAsync(Cancel);

        CollectionAssert.AreEquivalent(Enum.GetValues<BuiltInTag>(), tags.Select(t => t.BuiltIn!.Value).ToArray());
        CollectionAssert.AreEquivalent(BuiltInTags.Fields.Values.ToArray(), tags.Where(t => t.FieldKey != null).Select(t => t.FieldKey).ToArray());
        Assert.IsTrue(tags.Single(t => t.BuiltIn == BuiltInTag.Archived).IsHidden);
        Assert.IsTrue(tags.Where(t => t.FieldKey != null).All(t => t.IsCategory), "Fields are categories");
        Assert.HasCount(tags.Count, closure);
        Assert.IsTrue(closure.All(c => c.AncestorId == c.DescendantId && c.Depth == 0));
    }

    [TestMethod]
    public async Task AFieldValueIsFoundThroughItsFieldAndAChildThroughItsParent()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using (ApiClients.Create(factory)) { }
        var tenant = (await factory.Services.GetRequiredService<AccessService>().CreateTenantAsync(new TenantInput("TestLibrary"), Cancel)).Value!;
        await using var db = await factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(tenant.Id, Cancel);
        var bookId = await CatalogRows.AddAsync(db, tenant.Id, Cancel);
        var author = await db.Tags.SingleAsync(t => t.BuiltIn == BuiltInTag.Author, Cancel);
        var fiction = await db.Tags.SingleAsync(t => t.NormalizedName == "fiction", Cancel);

        // "Has a value of author:" and "has Fiction or a descendant": one join each through the closure.
        var byField = await db.Books.Where(b => db.BookTags.Any(l => l.BookId == b.Id
            && db.TagClosure.Any(c => c.DescendantId == l.TagId && c.AncestorId == author.Id))).Select(b => b.Id).ToListAsync(Cancel);
        var byParent = await db.Books.Where(b => db.BookTags.Any(l => l.BookId == b.Id
            && db.TagClosure.Any(c => c.DescendantId == l.TagId && c.AncestorId == fiction.Id))).Select(b => b.Id).ToListAsync(Cancel);

        CollectionAssert.AreEqual(new[] { bookId }, byField);
        CollectionAssert.AreEqual(new[] { bookId }, byParent);
        Assert.AreEqual(0xFFFFFFFFL, (await db.BookFiles.SingleAsync(Cancel)).Crc32, "A CRC-32 keeps its full unsigned range");
    }

    [TestMethod]
    public void NamesNormalizeAsTheImporterComparesThem()
    {
        Assert.AreEqual("stephen king", TagNames.Normalize("  Stephen \t KING "));
        Assert.AreEqual("fi", TagNames.Normalize("ﬁ"), "NFKC: the ligature becomes two letters");
        Assert.AreEqual("King, Stephen", TagNames.PersonSortName("Stephen King"));
        Assert.AreEqual("King, Stephen", TagNames.PersonSortName("King, Stephen"));
        Assert.AreEqual("Homer", TagNames.PersonSortName("Homer"));
    }
}
