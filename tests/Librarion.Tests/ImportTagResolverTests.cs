using Librarion.Core.Data;
using Librarion.Core.Import;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Tests;

/// <summary>
/// New tags made by an import (<see cref="ImportTagResolver"/>): copied past EF on PostgreSQL with ids from the
/// sequence, through EF on SQLite; either way with their closure, a plain tag's category, and their mappings.
/// </summary>
[TestClass]
public sealed class ImportTagResolverTests
{
    public TestContext TestContext { get; set; } = null!;
    private CancellationToken Cancel => TestContext.CancellationToken;

    [TestMethod]
    public async Task NewTagsComeWithTheirClosureParentsAndMappings()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        await using var db = await library.DbAsync(Cancel);
        var resolver = await ImportTagResolver.CreateAsync(db, library.TenantId, Cancel);
        ImportTagResolver.Name[] names =
        [
            new(ImportTagSourceKind.Fb2, "author", "Stephen King"),
            new(ImportTagSourceKind.Calibre, "author", "stephen  KING"),
            new(ImportTagSourceKind.Fb2, "category", "Horror"),
            new(ImportTagSourceKind.Fb2, null, "Scary"),
            new(ImportTagSourceKind.Inpx, null, "Old"),
        ];

        await resolver.ResolveAsync(db, names, Cancel);

        var ids = names.Select(n => resolver.Get(n)!.Value).ToArray();
        Assert.AreEqual(ids[0], ids[1], "one tag per field and name, whatever the source");
        Assert.AreEqual(4, ids.Distinct().Count());
        CollectionAssert.AreEquivalent(ids.Distinct().ToList(), resolver.Created.Ids.ToList());
        db.ChangeTracker.Clear();
        var tags = await db.Tags.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, Cancel);
        var author = await db.Tags.SingleAsync(t => t.FieldKey == "author", Cancel);
        var category = await db.Tags.SingleAsync(t => t.FieldKey == "category", Cancel);
        Assert.AreEqual("Stephen King", tags[ids[0]].Name);
        Assert.AreEqual("King, Stephen", tags[ids[0]].SortName);
        Assert.AreEqual(author.Id, tags[ids[0]].FieldId);
        Assert.IsNotNull(tags[ids[0]].SoundKey, "match keys set with the name");
        Assert.AreEqual(category.Id, tags[ids[2]].FieldId);
        Assert.IsNull(tags[ids[3]].FieldId);
        Assert.IsFalse(tags[ids[3]].IsCategory || tags[ids[3]].IsHidden);

        var fb2 = await db.Tags.SingleAsync(t => t.Name == "Imported: FB2", Cancel);
        var inpx = await db.Tags.SingleAsync(t => t.Name == "Imported: INPX", Cancel);
        var closure = await db.TagClosure.Where(c => ids.Contains(c.DescendantId)).Select(c => new { c.AncestorId, c.DescendantId, c.Depth }).ToListAsync(Cancel);
        Assert.HasCount(8, closure, "itself and the one above, for each");
        foreach (var (id, above) in new[] { (ids[0], author.Id), (ids[2], category.Id), (ids[3], fb2.Id), (ids[4], inpx.Id) })
        {
            Assert.IsTrue(closure.Any(c => c.AncestorId == id && c.DescendantId == id && c.Depth == 0));
            Assert.IsTrue(closure.Any(c => c.AncestorId == above && c.DescendantId == id && c.Depth == 1));
        }

        var parents = await db.TagParents.Where(p => ids.Contains(p.ChildId)).Select(p => new { p.ChildId, p.ParentId }).ToListAsync(Cancel);
        CollectionAssert.AreEquivalent(new[] { (ids[3], fb2.Id), (ids[4], inpx.Id) }, parents.Select(p => (p.ChildId, p.ParentId)).ToList());

        var mappings = await db.ImportTagMappings.Select(m => new { m.Source, m.FieldId, m.Name, m.TagId, m.Reviewed, m.Ignored }).ToListAsync(Cancel);
        Assert.HasCount(3, mappings, "the category and the plain tags; people's names get none");
        Assert.IsTrue(mappings.Any(m => m.Source == ImportTagSourceKind.Fb2 && m.FieldId == category.Id && m.Name == "Horror" && m.TagId == ids[2]));
        Assert.IsTrue(mappings.All(m => !m.Reviewed && !m.Ignored));

        // A second run finds them, and a tag made through EF afterwards takes an id of its own (the sequence moved on).
        var again = await ImportTagResolver.CreateAsync(db, library.TenantId, Cancel);
        await again.ResolveAsync(db, names, Cancel);
        CollectionAssert.AreEqual(ids, names.Select(n => again.Get(n)!.Value).ToArray());
        Assert.AreEqual(0, again.Created.Ids.Count);
        var later = new Tag { TenantId = library.TenantId, Name = "Later", NormalizedName = "later", SortName = "Later" };
        db.Tags.Add(later);
        await db.SaveChangesAsync(Cancel);
        Assert.IsGreaterThan(ids.Max(), later.Id);
    }
}
