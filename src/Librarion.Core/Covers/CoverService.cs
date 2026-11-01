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
/// from then on; a re-import tries again. The same cover asked for twice at once is made once.
/// </summary>
public sealed class CoverService(TenantDb tenantDb, CoverCache cache, CoverOptions options, ILogger<CoverService> logger)
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
                b.Title, b.CoverSource, b.CoverState, b.GeneratedTemplate, b.GeneratedVariant, b.SeriesNumber, b.WorkKey,
                Series = db.Tags.Where(t => t.Id == b.SeriesId).Select(t => t.Name).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (book is null)
            return null;

        var files = await BookDownloads.StoredAsync(db.BookFiles.Where(f => f.BookId == bookId).OrderBy(f => f.Id), cancellationToken);
        if (book.CoverSource == CoverSource.Book && book.CoverState != CoverState.Failed)
        {
            var source = files.Where(f => CoverExtractor.HasCovers(f.Target.Format))
                .OrderBy(f => Array.IndexOf(Preference, f.Target.Format)).Select(f => f.Target).FirstOrDefault();
            if (source is not null && await ExtractedAsync(db, bookId, source, book.CoverState, size, cancellationToken) is { } extracted)
                return extracted;
        }

        var authors = await db.BookTags.Where(l => l.BookId == bookId && l.Tag!.Field!.FieldKey == "author")
            .OrderBy(l => l.Ordinal).ThenBy(l => l.Tag!.SortName).Select(l => l.Tag!.Name).Take(3).ToListAsync(cancellationToken);
        var title = book.Title ?? (files.Count > 0 ? FileTitles.FromPath(files[0].Target.Name) : "");
        var series = book.Series is null ? null
            : book.SeriesNumber is { } number ? $"{book.Series} #{number.ToString("0.##", CultureInfo.InvariantCulture)}" : book.Series;
        var text = new CoverText(title, authors.Count > 0 ? string.Join(", ", authors) : null, series);
        var (template, variant) = book.GeneratedTemplate is { } t && book.GeneratedVariant is { } v ? (t, v) : GeneratedCovers.Assign(book.WorkKey ?? bookId);
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
