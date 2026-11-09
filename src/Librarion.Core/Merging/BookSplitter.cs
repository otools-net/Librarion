using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Reading;
using Librarion.Core.Tagging;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Merging;

/// <summary>
/// A book split (stage 6 chunk 13, the user's answer 2026-11-09,
/// agents/docs/features/merging.md#undo-bulk-merge-and-split-built-stage-6-chunk-13): chosen files of a book moved out
/// into a new book, for a wrong merge found later. A book read from its files' heads gives a new
/// book read from the files moved (the first that says something; else the file name); a book of a Calibre or INPX
/// import gives a copy of its metadata (the files don't carry the record). The book split keeps its own metadata; its
/// cover is looked for again among the files it kept. The two books are marked not duplicates (the user just said they
/// differ), and their duplicate groups follow.
/// </summary>
public sealed class BookSplitter(TenantDb tenantDb, IMetadataReader reader, TimeProvider time, ILogger<BookSplitter> logger)
{
    /// <summary>Answers the new book's id.</summary>
    public async Task<AccessResult<int>> SplitAsync(int tenantId, int bookId, IReadOnlyCollection<int> fileIds, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var book = await db.Books.FirstOrDefaultAsync(b => b.Id == bookId && b.MergedIntoId == null && b.MissingSince == null, cancellationToken);
        if (book is null)
            return AccessResult<int>.Missing();
        var ids = fileIds.Distinct().ToList();
        var files = await db.BookFiles.Where(f => f.BookId == bookId).OrderBy(f => f.Id).ToListAsync(cancellationToken);
        var moved = files.Where(f => ids.Contains(f.Id)).ToList();
        if (moved.Count == 0 || moved.Count != ids.Count || moved.Count == files.Count)
            return AccessResult<int>.Invalid("fileIds", "split.files");

        var copy = book.MetadataSource != MetadataSource.Heads;
        var metadata = copy ? null : await ReadAsync(db, ids, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var writer = new CatalogWriter(tenantId, book.LibraryId, await ImportTagResolver.CreateAsync(db, tenantId, cancellationToken), time);
        var newId = await writer.SplitAsync(db, book, moved, metadata, copy, cancellationToken);
        await writer.FlushCountsAsync(db, cancellationToken);
        await db.Books.Where(b => b.Id == bookId).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.Updated, time.GetUtcNow().UtcDateTime)
            .SetProperty(b => b.CoverState, b => b.CoverSource == CoverSource.Book ? CoverState.Unknown : b.CoverState), cancellationToken);
        await TagLinks.SortColumnsAsync(db, [bookId, newId], cancellationToken);
        db.NotDuplicates.Add(new NotDuplicate
        {
            TenantId = tenantId, BookId = Math.Min(bookId, newId), OtherId = Math.Max(bookId, newId), Created = time.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(cancellationToken);
        if (await DuplicateIndex.BuiltAsync(db, cancellationToken))
            await DuplicateIndex.RefreshAsync(db, [bookId, newId], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AccessResult<int>.Ok(newId);
    }

    /// <summary>What the first of the files that says something says, read from its head; null when none does (or can be read).</summary>
    private async Task<BookMetadata?> ReadAsync(LibrarionDbContext db, IReadOnlyCollection<int> fileIds, CancellationToken cancellationToken)
    {
        foreach (var stored in (await BookDownloads.StoredAsync(db.BookFiles.Where(f => fileIds.Contains(f.Id)), cancellationToken)).OrderBy(f => f.Target.FileId))
        {
            var target = stored.Target;
            try
            {
                var metadata = reader.Read(target.Format, () => BookDownloads.Open(target).Content?.Stream ?? throw new IOException("The file can't be opened."),
                    target.Size, cancellationToken);
                if (metadata is not null)
                    return metadata;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A broken file is still a book: named after its file.
                logger.LogDebug(error, "Metadata of {File} not read", target.Path);
            }
        }

        return null;
    }
}
