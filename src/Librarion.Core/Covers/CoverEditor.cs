using Librarion.Core.Access;
using Librarion.Core.Covers.Generated;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Covers;

/// <summary>
/// A book's cover chosen on its page (stage 6 chunk 10, D-6.7, agents/docs/features/book-display.md#a-books-cover-chosen):
/// a picture uploaded, a generated style (template and palette) over whatever it had, or back to its own. What a user
/// chooses is marked (<see cref="BookEdits.Cover"/>) and imports leave it. An upload no book uses any more is deleted.
/// </summary>
public sealed class CoverEditor(TenantDb tenantDb, CoverUploads uploads, TimeProvider time)
{
    /// <summary>The picture as the book's cover; refused (<c>cover.invalid</c>) when it isn't a JPEG, PNG or WebP within the limits.</summary>
    public async Task<AccessResult<bool>> UploadAsync(int tenantId, int bookId, byte[] picture, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var book = await db.Books.FirstOrDefaultAsync(b => b.Id == bookId && b.MergedIntoId == null, cancellationToken);
        if (book is null)
            return AccessResult<bool>.Missing();
        if (uploads.Store(tenantId, picture) is not { } stored)
            return AccessResult<bool>.Invalid("cover", "cover.invalid");
        var old = book.CoverFile;
        book.CoverFile = stored;
        book.CoverSource = CoverSource.Uploaded;
        book.Edited |= BookEdits.Cover;
        book.Updated = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await ForgetAsync(db, old, cancellationToken);
        return AccessResult<bool>.Ok(true);
    }

    /// <summary>A generated style as the book's cover, over an upload or its own; refused (<c>cover.style</c>) for a style that doesn't exist.</summary>
    public async Task<AccessResult<bool>> StyleAsync(int tenantId, int bookId, int template, int variant, CancellationToken cancellationToken = default)
    {
        if (template < 0 || template >= GeneratedCovers.Templates.Count || variant < 0 || variant >= GeneratedCovers.Palettes.Count)
            return AccessResult<bool>.Invalid("template", "cover.style");
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var book = await db.Books.FirstOrDefaultAsync(b => b.Id == bookId && b.MergedIntoId == null, cancellationToken);
        if (book is null)
            return AccessResult<bool>.Missing();
        var old = book.CoverFile;
        book.CoverFile = null;
        book.GeneratedTemplate = (short)template;
        book.GeneratedVariant = (short)variant;
        book.CoverSource = CoverSource.Generated;
        book.Edited |= BookEdits.Cover;
        book.Updated = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await ForgetAsync(db, old, cancellationToken);
        return AccessResult<bool>.Ok(true);
    }

    /// <summary>
    /// Back to the book's own cover, as an import would give it: Calibre's for a book imported from Calibre (its generated
    /// one when that has none), the one in its files when a format has covers, else its generated style (kept as chosen).
    /// </summary>
    public async Task<AccessResult<bool>> OwnAsync(int tenantId, int bookId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var book = await db.Books.FirstOrDefaultAsync(b => b.Id == bookId && b.MergedIntoId == null, cancellationToken);
        if (book is null)
            return AccessResult<bool>.Missing();
        var formats = await db.BookFiles.Where(f => f.BookId == bookId).Select(f => f.Format).ToListAsync(cancellationToken);
        var old = book.CoverFile;
        book.CoverFile = null;
        book.CoverSource = book.MetadataSource == MetadataSource.Calibre ? CoverSource.Calibre
            : formats.Any(CoverExtractor.HasCovers) ? CoverSource.Book : CoverSource.Generated;
        book.CoverState = CoverState.Unknown;
        book.Edited &= ~BookEdits.Cover;
        book.Updated = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await ForgetAsync(db, old, cancellationToken);
        return AccessResult<bool>.Ok(true);
    }

    /// <summary>An upload left behind is deleted when no book of the tenant uses it (the same picture may cover several).</summary>
    private async Task ForgetAsync(LibrarionDbContext db, string? upload, CancellationToken cancellationToken)
    {
        if (upload is null || await db.Books.AnyAsync(b => b.CoverFile == upload, cancellationToken))
            return;
        uploads.Delete(upload);
    }
}
