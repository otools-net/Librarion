using System.Collections.Concurrent;
using System.Globalization;
using Librarion.Core.Catalog;
using Librarion.Core.Covers.Generated;
using Librarion.Core.Data;
using Librarion.Core.Reading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Librarion.Core.Covers;

/// <summary>A cover ready to serve: its cached file and the tag of its content.</summary>
public sealed record CoverFile(string Path, string ETag);

/// <summary>
/// Every book's cover (agents/docs/features/book-display.md#cover): the one inside the book, extracted on first view
/// and cached at every size, else the generated one assigned at import, drawn on first view and cached. A book whose
/// cover can't be read (none, broken, refused) is marked <see cref="CoverState.Failed"/> and gets its generated cover
/// from then on; a re-import tries again. A book imported from Calibre with a cover has Calibre's <c>cover.jpg</c>, next
/// to its files (agents/docs/features/imports.md). The same cover asked for twice at once is made once.
/// </summary>
public sealed class CoverService(TenantDb tenantDb, CoverCache cache, CoverUploads uploads, CoverOptions options, ILogger<CoverService> logger)
{
    /// <summary>Formats tried first when a book has several files: the richest covers first.</summary>
    private static readonly string[] Preference = ["epub", "kepub", "fb2", "azw3", "mobi", "azw", "cbz"];

    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> making = new();
    private readonly SemaphoreSlim slots = new(Math.Max(1, options.MaxParallel));

    /// <summary>The cover of a book the account may see; null when it doesn't exist or isn't visible.</summary>
    public async Task<CoverFile?> GetAsync(int tenantId, BookVisibility visibility, int bookId, CoverSize size, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var visible = await BookQueries.VisibleAsync(db, visibility, null, cancellationToken);
        var book = await visible.Where(b => b.Id == bookId)
            .Select(b => new
            {
                b.Title, b.CoverSource, b.CoverState, b.GeneratedTemplate, b.GeneratedVariant, b.SeriesNumber, b.WorkKey, b.CoverFile,
                Series = db.Tags.Where(t => t.Id == b.SeriesId).Select(t => t.Name).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (book is null)
            return null;

        if (book.CoverSource == CoverSource.Uploaded && book.CoverFile is { } upload && await UploadedAsync(upload, size, cancellationToken) is { } uploaded)
            return uploaded;

        var files = await BookDownloads.StoredAsync(db.BookFiles.Where(f => f.BookId == bookId).OrderBy(f => f.Id), cancellationToken);
        if (book.CoverSource == CoverSource.Book && book.CoverState != CoverState.Failed)
        {
            var source = files.Where(f => CoverExtractor.HasCovers(f.Target.Format))
                .OrderBy(f => Array.IndexOf(Preference, f.Target.Format)).Select(f => f.Target).FirstOrDefault();
            if (source is not null && await ExtractedAsync(db, bookId, source, book.CoverState, size, cancellationToken) is { } extracted)
                return extracted;
        }

        if (book.CoverSource == CoverSource.Calibre && book.CoverState != CoverState.Failed
            && files.Select(f => f.Target).FirstOrDefault(f => f.ArchivePath is null) is { } loose
            && await CalibreAsync(db, bookId, loose, book.CoverState, size, cancellationToken) is { } calibre)
            return calibre;

        var text = await TextAsync(db, bookId, book.Title, book.Series, book.SeriesNumber, files.Count > 0 ? files[0].Target.Name : null, cancellationToken);
        var (template, variant) = book.GeneratedTemplate is { } t && book.GeneratedVariant is { } v ? (t, v) : GeneratedCovers.Assign(book.WorkKey ?? bookId);
        return await GeneratedAsync(template, variant, text, size, cancellationToken);
    }

    /// <summary>
    /// The book drawn in another template and palette (Change cover style's previews, stage 6 chunk 10), cached as any
    /// generated cover; null when the book isn't visible or the style doesn't exist.
    /// </summary>
    public async Task<CoverFile?> PreviewAsync(int tenantId, BookVisibility visibility, int bookId, int template, int variant, CoverSize size,
        CancellationToken cancellationToken = default)
    {
        if (template < 0 || template >= GeneratedCovers.Templates.Count || variant < 0 || variant >= GeneratedCovers.Palettes.Count)
            return null;
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var visible = await BookQueries.VisibleAsync(db, visibility, null, cancellationToken);
        var book = await visible.Where(b => b.Id == bookId)
            .Select(b => new { b.Title, b.SeriesNumber, Series = db.Tags.Where(t => t.Id == b.SeriesId).Select(t => t.Name).FirstOrDefault() })
            .FirstOrDefaultAsync(cancellationToken);
        if (book is null)
            return null;
        var files = await BookDownloads.StoredAsync(db.BookFiles.Where(f => f.BookId == bookId).OrderBy(f => f.Id), cancellationToken);
        var text = await TextAsync(db, bookId, book.Title, book.Series, book.SeriesNumber, files.Count > 0 ? files[0].Target.Name : null, cancellationToken);
        return await GeneratedAsync(template, variant, text, size, cancellationToken);
    }

    /// <summary>
    /// A template and palette drawn with a sample text (Cover styles' previews, stage 6 chunk 10b), at
    /// <see cref="CoverSize.M"/>: made in memory, not cached (the text comes from the caller's language), at most
    /// <see cref="CoverOptions.MaxParallel"/> at a time. Null for a style that doesn't exist.
    /// </summary>
    public async Task<byte[]?> SampleAsync(int template, int variant, CoverText text, CancellationToken cancellationToken = default)
    {
        if (template < 0 || template >= GeneratedCovers.Templates.Count || variant < 0 || variant >= GeneratedCovers.Palettes.Count)
            return null;
        await slots.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => GeneratedCovers.Render(template, variant, text, CoverSize.M), cancellationToken);
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>What a generated cover of the book says: its title (else its file's), first authors, series with number.</summary>
    private static async Task<CoverText> TextAsync(LibrarionDbContext db, int bookId, string? title, string? series, double? seriesNumber, string? fileName,
        CancellationToken cancellationToken)
    {
        var authors = await db.BookTags.Where(l => l.BookId == bookId && l.Tag!.Field!.FieldKey == "author")
            .OrderBy(l => l.Ordinal).ThenBy(l => l.Tag!.SortName).Select(l => l.Tag!.Name).Take(3).ToListAsync(cancellationToken);
        var shown = title ?? (fileName is null ? "" : FileTitles.FromPath(fileName));
        var line = series is null ? null : seriesNumber is { } number ? $"{series} #{number.ToString("0.##", CultureInfo.InvariantCulture)}" : series;
        return new CoverText(shown, authors.Count > 0 ? string.Join(", ", authors) : null, line);
    }

    private async Task<CoverFile> GeneratedAsync(int template, int variant, CoverText text, CoverSize size, CancellationToken cancellationToken)
    {
        var key = CoverCache.Key($"g:{template}:{variant}:{text.Title}\n{text.Authors}\n{text.Series}");
        if (!cache.Has(key, size))
        {
            await OnceAsync($"{key}-{size}", () =>
            {
                cache.Write(key, size, GeneratedCovers.Render(template, variant, text, size));
                return true;
            }, cancellationToken);
        }

        return new CoverFile(cache.PathOf(key, size), $"\"{key}-{size}\"");
    }

    /// <summary>An uploaded cover at <paramref name="size"/> (every size made at once from the stored one); null when its file is gone.</summary>
    private async Task<CoverFile?> UploadedAsync(string upload, CoverSize size, CancellationToken cancellationToken)
    {
        var key = CoverCache.Key($"u:{CoverUploads.HashOf(upload)}");
        if (cache.Has(key, size))
            return new CoverFile(cache.PathOf(key, size), $"\"{key}-{size}\"");
        var path = uploads.FullPath(upload);
        var made = await OnceAsync(key, () =>
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("An uploaded cover is missing: {Path}", path);
                return false;
            }

            if (CoverImages.Thumbnails(File.ReadAllBytes(path)) is not { } thumbnails)
                return false;
            foreach (var (each, picture) in thumbnails)
                cache.Write(key, each, picture);
            return true;
        }, cancellationToken);
        return made && cache.Has(key, size) ? new CoverFile(cache.PathOf(key, size), $"\"{key}-{size}\"") : null;
    }

    /// <summary>The book's own cover at <paramref name="size"/>, extracted the first time (every size at once); null when it has none.</summary>
    private async Task<CoverFile?> ExtractedAsync(LibrarionDbContext db, int bookId, BookFileTarget source, CoverState state, CoverSize size, CancellationToken cancellationToken)
    {
        var key = CoverCache.Key($"x:{source.FileId}:{source.Crc}:{source.Size}:{source.Modified?.Ticks}");
        if (cache.Has(key, size))
            return new CoverFile(cache.PathOf(key, size), $"\"{key}-{size}\"");

        bool? found = null;
        var made = await OnceAsync(key, () =>
        {
            var (content, problem) = BookDownloads.Open(source);
            if (content is null)
            {
                // The file is not there now (an unplugged disk, a rebuilt archive): not a verdict on the book.
                logger.LogDebug("Cover of book {Book}: its file can't be opened ({Problem})", bookId, problem);
                return false;
            }

            var opened = false;
            var bytes = CoverExtractor.Extract(source.Format, () =>
            {
                if (opened)
                    return BookDownloads.Open(source).Content?.Stream ?? throw new IOException("The book's file is gone.");
                opened = true;
                return content.Stream;
            }, source.Size, cancellationToken);
            if (!opened)
                content.Stream.Dispose();
            var thumbnails = bytes is null ? null : CoverImages.Thumbnails(bytes);
            found = thumbnails is not null;
            if (thumbnails is null)
                return false;
            foreach (var (each, picture) in thumbnails)
                cache.Write(key, each, picture);
            return true;
        }, cancellationToken);

        // Only the request that looked records what it found; a file that couldn't be opened records nothing.
        if (found is { } present && state != (present ? CoverState.Present : CoverState.Failed))
            await db.Books.Where(b => b.Id == bookId).ExecuteUpdateAsync(s => s.SetProperty(b => b.CoverState, present ? CoverState.Present : CoverState.Failed), cancellationToken);
        return made && cache.Has(key, size) ? new CoverFile(cache.PathOf(key, size), $"\"{key}-{size}\"") : null;
    }

    /// <summary>Calibre's <c>cover.jpg</c> in the folder of the book's file, at <paramref name="size"/> (every size made at once).</summary>
    private async Task<CoverFile?> CalibreAsync(LibrarionDbContext db, int bookId, BookFileTarget file, CoverState state, CoverSize size, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(file.RootPath, file.Path)));
        var info = folder is null ? null : new FileInfo(Path.Combine(folder, "cover.jpg"));
        if (info is null || !info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !Import.LibraryRoots.IsWithin(info.FullName, file.RootPath))
        {
            // Not there now: told apart from a broken picture only by looking again later (a re-import resets the state).
            logger.LogDebug("Cover of book {Book}: no cover.jpg next to its file", bookId);
            return null;
        }

        var key = CoverCache.Key($"c:{info.FullName}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        if (cache.Has(key, size))
            return new CoverFile(cache.PathOf(key, size), $"\"{key}-{size}\"");

        bool? found = null;
        var made = await OnceAsync(key, () =>
        {
            var thumbnails = info.Length <= CoverExtractor.MaxCoverBytes ? CoverImages.Thumbnails(File.ReadAllBytes(info.FullName)) : null;
            found = thumbnails is not null;
            if (thumbnails is null)
                return false;
            foreach (var (each, picture) in thumbnails)
                cache.Write(key, each, picture);
            return true;
        }, cancellationToken);

        if (found is { } present && state != (present ? CoverState.Present : CoverState.Failed))
            await db.Books.Where(b => b.Id == bookId).ExecuteUpdateAsync(s => s.SetProperty(b => b.CoverState, present ? CoverState.Present : CoverState.Failed), cancellationToken);
        return made && cache.Has(key, size) ? new CoverFile(cache.PathOf(key, size), $"\"{key}-{size}\"") : null;
    }

    /// <summary>Runs <paramref name="make"/> once for concurrent askers of the same key, at most <see cref="CoverOptions.MaxParallel"/> at a time.</summary>
    private async Task<bool> OnceAsync(string key, Func<bool> make, CancellationToken cancellationToken)
    {
        var lazy = making.GetOrAdd(key, _ => new Lazy<Task<bool>>(async () =>
        {
            // Not the asker's token: others may be waiting on this one.
            await slots.WaitAsync(CancellationToken.None);
            try
            {
                return await Task.Run(make, CancellationToken.None);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(error, "A cover could not be made");
                return false;
            }
            finally
            {
                slots.Release();
            }
        }));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            if (lazy.Value.IsCompleted)
                making.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(key, lazy));
        }
    }
}
