using Librarion.Core.Catalog;
using Librarion.Core.Containers;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Reading;

/// <summary>A book file found for a download, before it is opened.</summary>
/// <param name="Name">The file name as shown (the last part of its path or entry name).</param>
public sealed record BookFileTarget(int LibraryId, int FileId, string Name, string Format, long Size, uint? Crc, DateTime? Modified,
    string RootPath, string? ArchivePath, long? ArchiveSize, string Path, long? Locator, int? Method, long? Packed);

/// <summary>A stored file with its book and its SHA-256 when one was computed (agents/docs/features/merging.md).</summary>
public sealed record StoredBookFile(int BookId, byte[]? ContentHash, BookFileTarget Target);

public enum BookFileProblem
{
    /// <summary>The file, its archive or its root is no longer where the last scan found it.</summary>
    Gone,
    /// <summary>The file or its archive changed since the last scan (a rebuilt archive): the next scan fixes it.</summary>
    Changed,
    /// <summary>The archive can't give the entry (broken, encrypted, an unknown method).</summary>
    Unreadable,
}

/// <summary>
/// An opened book file. <paramref name="Seekable"/>: a loose file or a stored entry (served with Range); a compressed
/// entry is a stream read once, held to its size and checked against its CRC at the end.
/// </summary>
public sealed record BookFileContent(Stream Stream, long Length, bool Seekable);

/// <summary>
/// Book files served as they lie (agents/docs/features/archives.md): a loose file from its root, an archive entry from
/// its ZIP with one seek to its stored locator. Never a temp file, never the archive unpacked.
/// </summary>
public sealed class BookDownloads(TenantDb tenantDb)
{
    /// <summary>The file of a book the account may see; null when the book is not visible or has no such file.</summary>
    public async Task<BookFileTarget?> FindAsync(int tenantId, BookVisibility visibility, int bookId, int fileId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var visible = await BookQueries.VisibleAsync(db, visibility, null, cancellationToken);
        var found = await StoredAsync(db.BookFiles.Where(f => f.Id == fileId && f.BookId == bookId && visible.Any(b => b.Id == bookId)).Take(1), cancellationToken);
        // A merged book's file is its kept book's now (stage 6 chunk 12): the old address still downloads it.
        if (found.Count == 0 && await Merging.BookMerger.CurrentAsync(db, bookId, cancellationToken) is { } current && current != bookId)
            found = await StoredAsync(db.BookFiles.Where(f => f.Id == fileId && f.BookId == current && visible.Any(b => b.Id == current)).Take(1), cancellationToken);
        return found.Count == 0 ? null : found[0].Target;
    }

    /// <summary>The files as targets to open, with their book and content hash (when computed).</summary>
    public static async Task<List<StoredBookFile>> StoredAsync(IQueryable<BookFile> files, CancellationToken cancellationToken)
    {
        var rows = await files
            .Select(f => new
            {
                f.Id, f.BookId, f.LibraryId, f.Path, f.Format, f.Size, f.Crc32, f.Modified, f.Locator, f.Method, f.PackedSize, f.ContentHash,
                RootPath = f.Archive != null ? f.Archive.Root!.Path : f.Root!.Path,
                ArchivePath = f.Archive != null ? f.Archive.Path : null,
                ArchiveSize = f.Archive != null ? (long?)f.Archive.Size : null,
            })
            .ToListAsync(cancellationToken);
        return rows.Select(f => new StoredBookFile(f.BookId, f.ContentHash, new BookFileTarget(
                f.LibraryId, f.Id, NameOf(f.Path), f.Format, f.Size, f.Crc32 is { } crc ? (uint)crc : null, f.Modified,
                f.RootPath, f.ArchivePath, f.ArchiveSize, f.Path, f.Locator, f.Method, f.PackedSize)))
            .ToList();
    }

    /// <summary>Opens the file for reading; the caller disposes the stream (it owns the file handle).</summary>
    public static (BookFileContent? Content, BookFileProblem? Problem) Open(BookFileTarget target)
    {
        if (Inside(target.RootPath, target.ArchivePath ?? target.Path) is not { } path)
            return (null, BookFileProblem.Gone);
        FileStream file;
        try
        {
            var info = new FileInfo(path);
            // A link put in place of a book since the scan is not followed (the scanner doesn't follow links either).
            if (!info.Exists || info.LinkTarget is not null)
                return (null, BookFileProblem.Gone);
            file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read | FileShare.Delete, BufferSize = 64 * 1024,
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, BookFileProblem.Gone);
        }

        if (target.ArchivePath is null)
        {
            if (file.Length != target.Size)
            {
                file.Dispose();
                return (null, BookFileProblem.Changed);
            }
            return (new BookFileContent(file, file.Length, Seekable: true), null);
        }

        try
        {
            // Another size: entries moved, the stored locators are stale. A touched archive (same size) is opened: its
            // local headers are checked, and the CRC at the end catches what they don't.
            if (file.Length != target.ArchiveSize || target is not { Locator: { } locator, Method: { } method, Packed: { } packed, Crc: { } crc })
            {
                file.Dispose();
                return (null, BookFileProblem.Changed);
            }

            var directory = ZipDirectory.Locate(file);
            var entry = new ArchiveEntry(target.Path, ArchiveEntryKind.File, target.Size, packed, null, false, locator, method, crc);
            if (directory.StoredRange(file, entry) is { } range)
                return (new BookFileContent(new SliceStream(file, range.Start, range.Length), range.Length, Seekable: true), null);
            return (new BookFileContent(directory.OpenEntry(file, entry), target.Size, Seekable: false), null);
        }
        catch (ArchiveException)
        {
            file.Dispose();
            return (null, BookFileProblem.Changed);
        }
        catch (Exception error) when (error is IOException or NotSupportedException)
        {
            file.Dispose();
            return (null, BookFileProblem.Unreadable);
        }
    }

    private static string NameOf(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    /// <summary>The full path of <paramref name="relative"/> under the root; null when it would leave it.</summary>
    private static string? Inside(string root, string relative)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        var prefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? root : root + System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? full : null;
    }
}
