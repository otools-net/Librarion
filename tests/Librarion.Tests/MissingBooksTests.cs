using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Settings;
using Librarion.Core.Tasks;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Tests;

/// <summary>
/// Books whose files are gone (agents/docs/features/missing-books.md, as SFE's missing files): kept with their curation,
/// back when the file is found again where it was or moved, hidden meanwhile, and removed only when a user accepts.
/// </summary>
[TestClass]
public sealed class MissingBooksTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    [TestMethod]
    public async Task ABookWhoseFileComesBackIsTheSameBookWithItsCuration()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var path = library.Write("TestLibrary/book.fb2", "the book");
        await library.ScanAsync(Cancel);
        int bookId, fileId, tagId;
        await using (var db = await library.DbAsync(Cancel))
        {
            var file = await db.BookFiles.SingleAsync(Cancel);
            (bookId, fileId) = (file.BookId, file.Id);
            var tag = new Tag { TenantId = library.TenantId, Name = "Favourite", NormalizedName = "favourite", SortName = "favourite" };
            db.Tags.Add(tag);
            await db.SaveChangesAsync(Cancel);
            tagId = tag.Id;
            db.BookTags.Add(new BookTag { TenantId = library.TenantId, BookId = bookId, TagId = tagId, Source = TagSource.Manual });
            await db.SaveChangesAsync(Cancel);
        }

        File.Move(path, path + ".away");
        var gone = await library.ScanAsync(Cancel);
        Assert.AreEqual(1, gone.Missing);
        await using (var db = await library.DbAsync(Cancel))
        {
            var book = await db.Books.SingleAsync(Cancel);
            Assert.IsNotNull(book.MissingSince);
            Assert.IsNotNull((await db.BookFiles.SingleAsync(Cancel)).MissingSince);
            Assert.AreEqual(0, (await db.Libraries.SingleAsync(l => l.Id == library.LibraryId, Cancel)).BookCount);
        }

        File.Move(path + ".away", path);
        var back = await library.ScanAsync(Cancel);
        Assert.AreEqual(1, back.Returned);
        Assert.AreEqual(0, back.Added);
        await using (var db = await library.DbAsync(Cancel))
        {
            var file = await db.BookFiles.SingleAsync(Cancel);
            Assert.AreEqual((bookId, fileId), (file.BookId, file.Id), "the same book and file");
            Assert.IsNull(file.MissingSince);
            Assert.IsNull((await db.Books.SingleAsync(Cancel)).MissingSince);
            Assert.IsTrue(await db.BookTags.AnyAsync(l => l.BookId == bookId && l.TagId == tagId && l.Source == TagSource.Manual, Cancel), "its manual tag stayed");
            Assert.AreEqual(1, (await db.Libraries.SingleAsync(l => l.Id == library.LibraryId, Cancel)).BookCount);
        }
    }

    [TestMethod]
    public async Task AMovedFileTakesItsBookBack()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var path = library.Write("Old/the hobbit.fb2", "a hobbit");
        await library.ScanAsync(Cancel);
        int bookId, fileId;
        await using (var db = await library.DbAsync(Cancel))
            (bookId, fileId) = await db.BookFiles.Select(f => new ValueTuple<int, int>(f.BookId, f.Id)).SingleAsync(Cancel);

        Directory.CreateDirectory(Path.Combine(library.Root, "New"));
        File.Move(path, Path.Combine(library.Root, "New", "the hobbit.fb2"));
        var result = await library.ScanAsync(Cancel);

        Assert.AreEqual(0, result.Added, "not a new book");
        Assert.AreEqual(1, result.Returned);
        await using var check = await library.DbAsync(Cancel);
        var file = await check.BookFiles.SingleAsync(Cancel);
        Assert.AreEqual("New/the hobbit.fb2", file.Path);
        Assert.AreEqual((bookId, fileId), (file.BookId, file.Id), "the old rows, at the new place");
        Assert.IsNull(file.MissingSince);
        Assert.AreEqual(1, await check.Books.CountAsync(Cancel));
        Assert.AreEqual(1, (await check.Libraries.SingleAsync(l => l.Id == library.LibraryId, Cancel)).BookCount);
    }

    [TestMethod]
    public async Task ARenamedArchiveKeepsItsBooksAndOneThatComesBackFindsThemAgain()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var zip = library.Zip("fb2-0001.zip", ("1.fb2", "one"), ("2.fb2", "two"));
        await library.ScanAsync(Cancel);
        List<int> books;
        await using (var db = await library.DbAsync(Cancel))
            books = await db.Books.OrderBy(b => b.Id).Select(b => b.Id).ToListAsync(Cancel);

        File.Move(zip, Path.Combine(library.Root, "renamed.zip"));
        var renamed = await library.ScanAsync(Cancel);
        Assert.AreEqual(0, renamed.Added);
        Assert.AreEqual(2, renamed.Returned, "both entries relinked by size and CRC");
        await using (var db = await library.DbAsync(Cancel))
        {
            CollectionAssert.AreEqual(books, await db.Books.Where(b => b.MissingSince == null).OrderBy(b => b.Id).Select(b => b.Id).ToListAsync(Cancel));
            Assert.AreEqual("renamed.zip", (await db.Archives.SingleAsync(Cancel)).Path, "the old archive's row, left empty, went");
        }

        // Taken away and put back unchanged: its entries are present again without being read.
        var away = Path.Combine(library.Root, "..", "renamed.zip");
        File.Move(Path.Combine(library.Root, "renamed.zip"), away);
        Assert.AreEqual(2, (await library.ScanAsync(Cancel)).Missing);
        File.Move(away, Path.Combine(library.Root, "renamed.zip"));
        var back = await library.ScanAsync(Cancel);
        Assert.AreEqual(2, back.Returned);
        Assert.AreEqual(1, back.ArchivesSkipped);
        await using (var db = await library.DbAsync(Cancel))
            Assert.AreEqual(0, await db.BookFiles.CountAsync(f => f.MissingSince != null, Cancel));
    }

    [TestMethod]
    public async Task TwoPossibleNewPlacesRelinkNothing()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var path = library.Write("a/book.fb2", "same");
        await library.ScanAsync(Cancel);
        File.Delete(path);
        library.Write("b/book.fb2", "same");
        library.Write("c/book.fb2", "same");

        var result = await library.ScanAsync(Cancel);

        Assert.AreEqual(2, result.Added, "no single candidate: both are new books");
        Assert.AreEqual(0, result.Returned);
        Assert.AreEqual(1, result.Missing);
    }

    [TestMethod]
    public async Task MissingBooksAreHiddenAndRemovedOnlyWhenAUserAccepts()
    {
        await using var library = await ScannedLibrary.CreateAsync(Cancel);
        var old = library.Write("old.fb2", "old");
        var recent = library.Write("recent.fb2", "recent");
        var chosen = library.Write("chosen.fb2", "chosen");
        library.Write("kept.fb2", "kept");
        await library.ScanAsync(Cancel);
        File.Delete(old);
        File.Delete(recent);
        File.Delete(chosen);
        await library.ScanAsync(Cancel);
        await using (var db = await library.DbAsync(Cancel))
        {
            // "old" went missing long ago.
            var oldBook = (await db.BookFiles.SingleAsync(f => f.Path == "old.fb2", Cancel)).BookId;
            await db.Books.Where(b => b.Id == oldBook).ExecuteUpdateAsync(s => s.SetProperty(b => b.MissingSince, DateTime.UtcNow.AddDays(-40)), Cancel);
        }

        var (admin, _) = await library.AdminAsync(Cancel);
        var listed = await admin.GetAsync<Page<BookListItem>>($"/api/books?library={library.LibraryId}", Cancel);
        Assert.AreEqual("kept", listed.Items.Single().FileName?.Replace(".fb2", ""), "missing books are not listed");

        var missing = await admin.GetAsync<MissingBookPage>($"/api/libraries/{library.LibraryId}/missing", Cancel);
        Assert.AreEqual(3, missing.Total);
        Assert.AreEqual(1, missing.Ready, "missing longer than the 30 days of the default");
        Assert.AreEqual(30, missing.KeepDays);
        Assert.AreEqual("old", missing.Items[0].Name, "oldest first");
        Assert.IsTrue(missing.Items[0].Ready);
        var firstPage = await admin.GetAsync<MissingBookPage>($"/api/libraries/{library.LibraryId}/missing?limit=2", Cancel);
        var secondPage = await admin.GetAsync<MissingBookPage>($"/api/libraries/{library.LibraryId}/missing?limit=2&after={firstPage.Next}", Cancel);
        Assert.HasCount(3, firstPage.Items.Concat(secondPage.Items).Select(i => i.Id).Distinct().ToList(), "paged by key");

        // Nothing without a choice; then the ones past the retention; then one chosen book.
        using (var nothing = await admin.PostAsJsonAsync($"/api/libraries/{library.LibraryId}/missing/remove", new MissingRemovalInput(), ApiClients.Json, Cancel))
            Assert.AreEqual(HttpStatusCode.BadRequest, nothing.StatusCode);
        Assert.AreEqual(1, (await RemoveAsync(admin, library.LibraryId, new MissingRemovalInput(Ready: true))).Counts!.Removed);
        var chosenBook = (await admin.GetAsync<MissingBookPage>($"/api/libraries/{library.LibraryId}/missing", Cancel)).Items.Single(i => i.Name == "chosen").Id;
        Assert.AreEqual(1, (await RemoveAsync(admin, library.LibraryId, new MissingRemovalInput([chosenBook]))).Counts!.Removed);

        await using (var db = await library.DbAsync(Cancel))
        {
            CollectionAssert.AreEquivalent(new[] { "kept", "recent" }, await db.Books.Select(b => b.SortTitle).ToListAsync(Cancel));
            Assert.AreEqual(1, (await db.Libraries.SingleAsync(l => l.Id == library.LibraryId, Cancel)).BookCount, "removing missing books moves no count");
        }

        // A shorter retention offers the recent one too; still nothing goes until it is accepted.
        await library.Service<SettingsService>().SaveAsync(new SettingOwner(SettingLevel.Library, library.LibraryId), library.TenantId,
            new SettingChanges(new Dictionary<string, JsonElement?> { [SettingCatalog.KeepMissingDays] = JsonSerializer.SerializeToElement(0) }), Cancel);
        Assert.AreEqual(1, (await admin.GetAsync<MissingBookPage>($"/api/libraries/{library.LibraryId}/missing", Cancel)).Ready);
        await library.ScanAsync(Cancel);
        await using (var db = await library.DbAsync(Cancel))
            Assert.AreEqual(2, await db.Books.CountAsync(Cancel), "a scan never removes");
    }

    private async Task<TaskRunInfo> RemoveAsync(HttpClient admin, int libraryId, MissingRemovalInput input)
    {
        using var response = await admin.PostAsJsonAsync($"/api/libraries/{libraryId}/missing/remove", input, ApiClients.Json, Cancel);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync(Cancel));
        var started = (await response.Content.ReadFromJsonAsync<TaskStarted>(ApiClients.Json, Cancel))!;
        for (var i = 0; i < 300; i++)
        {
            var info = await admin.GetAsync<TaskRunInfo>($"/api/tasks/{started.RunId}", Cancel);
            if (info.Outcome != TaskOutcome.Running)
            {
                Assert.AreEqual(TaskOutcome.Succeeded, info.Outcome, info.Summary);
                return info;
            }

            await Task.Delay(50, Cancel);
        }

        Assert.Fail("the removal didn't end");
        return null!;
    }
}
