using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Tests.Infrastructure;

/// <summary>A row in every catalog, tag and task table for one tenant: for the isolation and cascade tests.</summary>
public static class CatalogRows
{
    /// <returns>The book's id.</returns>
    public static async Task<int> AddAsync(LibrarionDbContext db, int tenantId, CancellationToken cancellationToken)
    {
        var library = await db.Libraries.FirstAsync(l => l.TenantId == tenantId, cancellationToken);
        var authorField = await db.Tags.FirstAsync(t => t.TenantId == tenantId && t.BuiltIn == BuiltInTag.Author, cancellationToken);
        var group = await db.Groups.FirstAsync(g => g.TenantId == tenantId, cancellationToken);
        var now = DateTime.UtcNow;

        var author = new Tag { TenantId = tenantId, Name = "Stephen King", NormalizedName = TagNames.Normalize("Stephen King"), SortName = "King, Stephen", FieldId = authorField.Id };
        var horror = new Tag { TenantId = tenantId, Name = "Horror", NormalizedName = "horror", SortName = "Horror" };
        var fiction = new Tag { TenantId = tenantId, Name = "Fiction", NormalizedName = "fiction", SortName = "Fiction" };
        author.Aliases.Add(new TagAlias { TenantId = tenantId, Name = "Steven King", NormalizedName = "steven king" });
        db.Tags.AddRange(author, horror, fiction);
        db.TagParents.Add(new TagParent { TenantId = tenantId, Child = horror, Parent = fiction });
        db.TagClosure.AddRange(
            new TagClosure { TenantId = tenantId, Ancestor = author, Descendant = author },
            new TagClosure { TenantId = tenantId, AncestorId = authorField.Id, Descendant = author, Depth = 1 },
            new TagClosure { TenantId = tenantId, Ancestor = horror, Descendant = horror },
            new TagClosure { TenantId = tenantId, Ancestor = fiction, Descendant = fiction },
            new TagClosure { TenantId = tenantId, Ancestor = fiction, Descendant = horror, Depth = 1 });

        var root = new LibraryRoot { TenantId = tenantId, LibraryId = library.Id, Path = $"/books/{tenantId}", Created = now };
        var archive = new Archive { TenantId = tenantId, LibraryId = library.Id, Root = root, Path = "books.zip", PathKey = 1, Size = 100, Modified = now };
        var book = new Book
        {
            TenantId = tenantId, LibraryId = library.Id, Title = "It", SortTitle = "it", SearchText = "it", SortAuthor = "King, Stephen", Added = now, Updated = now,
            Files = [new BookFile { TenantId = tenantId, LibraryId = library.Id, Archive = archive, Path = "it.fb2", PathKey = 2, Format = "fb2", Size = 10, Crc32 = 0xFFFFFFFF }],
            Identifiers = [new BookIdentifier { TenantId = tenantId, Scheme = "isbn", Value = "9780450411434" }],
        };
        book.Tags.Add(new BookTag { TenantId = tenantId, Tag = author, Ordinal = 0 });
        book.Tags.Add(new BookTag { TenantId = tenantId, Tag = horror });
        db.Books.Add(book);
        db.GroupTagFilters.Add(new GroupTagFilter { GroupId = group.Id, Tag = fiction });
        db.ImportTagMappings.Add(new ImportTagMapping { TenantId = tenantId, Source = ImportTagSourceKind.Epub, NormalizedName = "horror stories", Tag = horror });
        db.TaskRuns.Add(new TaskRun { TenantId = tenantId, LibraryId = library.Id, Task = BackgroundTaskKind.LibraryScan, Started = now, Finished = now, Outcome = TaskOutcome.Succeeded });
        db.TaskSchedules.Add(new TaskSchedule { TenantId = tenantId, LibraryId = library.Id, Kind = ScheduleKind.Weekly, Days = 127, Start = now, Updated = now });
        await db.SaveChangesAsync(cancellationToken);

        db.TagCounts.Add(new TagCount { TenantId = tenantId, LibraryId = library.Id, TagId = author.Id, BookCount = 1 });
        library.BookCount = 1;
        await db.SaveChangesAsync(cancellationToken);
        return book.Id;
    }
}
