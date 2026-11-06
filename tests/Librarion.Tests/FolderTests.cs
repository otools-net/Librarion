using Librarion.Core.Data;
using Librarion.Core.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Librarion.Tests;

/// <summary>
/// The folders of a library (agents/docs/features/folders.md): every file in one, a root's own folder, directories, a
/// ZIP and the directories inside it, made once, followed by moved files and removed when nothing is left in them.
/// </summary>
[TestClass]
public sealed class FolderTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    [TestMethod]
    public async Task EveryFileIsInItsFolderAndAZipIsAFolder()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        library.Write("top.fb2", "top");
        library.Write("Authors/King/It.fb2", "it");
        library.Write("Authors/King/Carrie.fb2", "carrie");
        library.Zip("lib/a.zip", ("1.fb2", "one"), ("sub/2.fb2", "two"));
        await library.ScanAsync(Cancel);

        await using var db = await library.DbAsync(Cancel);
        var folders = await db.Folders.ToDictionaryAsync(f => f.Path, Cancel);
        CollectionAssert.AreEquivalent(new[] { "", "Authors", "Authors/King", "lib", "lib/a.zip", "lib/a.zip/sub" }, folders.Keys.ToList());
        Assert.IsNull(folders[""].ParentId, "the root's own folder");
        Assert.AreEqual(folders["Authors"].Id, folders["Authors/King"].ParentId);
        Assert.AreEqual(folders["lib/a.zip"].Id, folders["lib/a.zip/sub"].ParentId);
        Assert.AreEqual("King", folders["Authors/King"].Name);
        var archive = await db.Archives.SingleAsync(Cancel);
        Assert.AreEqual(archive.Id, folders["lib/a.zip"].ArchiveId, "the ZIP's folder knows its archive");
        Assert.IsNull(folders["lib/a.zip/sub"].ArchiveId, "a directory inside it doesn't");

        var files = await db.BookFiles.ToDictionaryAsync(f => f.Path, f => f.FolderId, Cancel);
        Assert.AreEqual(folders[""].Id, files["top.fb2"]);
        Assert.AreEqual(folders["Authors/King"].Id, files["Authors/King/It.fb2"]);
        Assert.AreEqual(folders["lib/a.zip"].Id, files["1.fb2"]);
        Assert.AreEqual(folders["lib/a.zip/sub"].Id, files["sub/2.fb2"]);

        library.Write("Authors/King/Misery.fb2", "misery");
        await library.ScanAsync(Cancel);
        await using var again = await library.DbAsync(Cancel);
        Assert.AreEqual(folders.Count, await again.Folders.CountAsync(Cancel), "a scan finds the folders it made before");
        Assert.AreEqual(folders["Authors/King"].Id, (await again.BookFiles.SingleAsync(f => f.Path == "Authors/King/Misery.fb2", Cancel)).FolderId);
    }

    [TestMethod]
    public async Task AMovedFileFollowsAndTheFolderItLeftGoesWhenEmpty()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var path = library.Write("Old/Deep/the hobbit.fb2", "a hobbit");
        library.Write("Old/kept.fb2", "kept");
        await library.ScanAsync(Cancel);

        Directory.CreateDirectory(Path.Combine(library.Root, "New"));
        File.Move(path, Path.Combine(library.Root, "New", "the hobbit.fb2"));
        Assert.AreEqual(1, (await library.ScanAsync(Cancel)).Returned);

        await using var db = await library.DbAsync(Cancel);
        var folders = await db.Folders.ToDictionaryAsync(f => f.Path, f => f.Id, Cancel);
        CollectionAssert.AreEquivalent(new[] { "", "Old", "New" }, folders.Keys.ToList(), "Old/Deep is empty and gone; Old still holds a book");
        Assert.AreEqual(folders["New"], (await db.BookFiles.SingleAsync(f => f.Path == "New/the hobbit.fb2", Cancel)).FolderId);
    }

    [TestMethod]
    public async Task RemovedBooksAndRootsTakeTheirEmptyFoldersWithThem()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var gone = library.Write("Gone/away.fb2", "away");
        library.Write("Stays/here.fb2", "here");
        await library.ScanAsync(Cancel);
        File.Delete(gone);
        await library.ScanAsync(Cancel);
        await using (var db = await library.DbAsync(Cancel))
            Assert.IsTrue(await db.Folders.AnyAsync(f => f.Path == "Gone", Cancel), "a missing book's folder stays: the book may come back");

        await library.Service<MissingBooks>().RemoveAsync(library.TenantId, library.LibraryId, new MissingRemoval(null, null), null, Cancel);
        int rootId;
        await using (var db = await library.DbAsync(Cancel))
        {
            CollectionAssert.AreEquivalent(new[] { "", "Stays" }, await db.Folders.Select(f => f.Path).ToListAsync(Cancel));
            rootId = await db.LibraryRoots.Select(r => r.Id).SingleAsync(Cancel);
        }

        await new LibraryScanner(library.Service<TenantDb>(), new NoMetadataReader(), TimeProvider.System, NullLogger<LibraryScanner>.Instance)
            .RemoveRootAsync(library.TenantId, rootId, null, Cancel);
        await using (var db = await library.DbAsync(Cancel))
            Assert.IsFalse(await db.Folders.AnyAsync(Cancel), "a root's folders go with it");
    }
}
