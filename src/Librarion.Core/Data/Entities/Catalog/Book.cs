namespace Librarion.Core.Data;

/// <summary>
/// A book as shown: the work, with its cover, title and fields (agents/docs/features/book-display.md); its copies and
/// formats are its <see cref="Files"/> (agents/docs/features/merging.md). Authors, series, categories… are tags under
/// fields (<see cref="BookTag"/>, agents/docs/features/metadata-tags.md); a few of them are copied here as sort keys.
/// </summary>
public sealed class Book
{
    public const int MaxTitleLength = 500;
    public const int MaxSortLength = 300;

    public int Id { get; set; }
    public int TenantId { get; set; }
    public int LibraryId { get; set; }
    public Library? Library { get; set; }

    /// <summary>The title from the metadata; null when it gave none (the file name is shown instead).</summary>
    public string? Title { get; set; }

    /// <summary>The title, else the file name, normalised for sorting; never null.</summary>
    public string SortTitle { get; set; } = string.Empty;

    /// <summary>The first author's sort name ("King, Stephen"), kept by imports and edits; null without an author.</summary>
    public string? SortAuthor { get; set; }

    /// <summary>The first series (a tag under the <c>series</c> field) and the book's number in it, for sorting.</summary>
    public int? SeriesId { get; set; }
    public double? SeriesNumber { get; set; }

    public string? Description { get; set; }

    /// <summary>0–10 (half stars), null = not rated.</summary>
    public byte? Rating { get; set; }

    public DateTime Added { get; set; }
    public DateTime Updated { get; set; }

    public CoverSource CoverSource { get; set; }

    /// <summary>For <see cref="CoverSource.Generated"/>: the template and palette assigned at import.</summary>
    public short? GeneratedTemplate { get; set; }
    public short? GeneratedVariant { get; set; }

    /// <summary>Whether the cover in the book could be read (extracted covers only).</summary>
    public CoverState CoverState { get; set; }

    /// <summary>Set when the book was merged into another: it then only redirects there.</summary>
    public int? MergedIntoId { get; set; }

    /// <summary>Blocking key for "same work" duplicates: a hash of the normalized title (else file stem) and first author.</summary>
    public long? WorkKey { get; set; }

    public List<BookFile> Files { get; set; } = [];
    public List<BookTag> Tags { get; set; } = [];
    public List<BookIdentifier> Identifiers { get; set; } = [];
}

public enum CoverSource
{
    Book,
    Calibre,
    Uploaded,
    Generated,
}

public enum CoverState
{
    Unknown,
    Present,
    Failed,
}

/// <summary>A copy or format of a book: a loose file of the library or an entry of one of its archives.</summary>
public sealed class BookFile
{
    public const int MaxPathLength = 2000;

    public int Id { get; set; }
    public int TenantId { get; set; }
    public int LibraryId { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }

    /// <summary>The container archive; null for a loose file.</summary>
    public int? ArchiveId { get; set; }
    public Archive? Archive { get; set; }

    /// <summary>Path relative to the library's root, or the entry's name inside <see cref="Archive"/>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>A 64-bit hash of <see cref="Path"/>: paths are too long to index themselves.</summary>
    public long PathKey { get; set; }

    /// <summary>Lower-case format: <c>fb2</c>, <c>epub</c>, <c>pdf</c>…</summary>
    public string Format { get; set; } = string.Empty;

    public long Size { get; set; }
    public long? Crc32 { get; set; }
    public DateTime? Modified { get; set; }

    /// <summary>Archive entries: offset of the local header, compression method and packed size, for a one-seek open.</summary>
    public long? Locator { get; set; }
    public short? Method { get; set; }
    public long? PackedSize { get; set; }

    /// <summary>SHA-256 of the bytes, computed only when size and CRC collide with another file.</summary>
    public byte[]? ContentHash { get; set; }
}

/// <summary>A ZIP container of books in a library, indexed in place (agents/docs/features/archives.md).</summary>
public sealed class Archive
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int LibraryId { get; set; }
    public Library? Library { get; set; }

    public string Path { get; set; } = string.Empty;
    public long PathKey { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }

    /// <summary>Hash of the central directory: same size, time and fingerprint = unchanged, nothing read.</summary>
    public long? Fingerprint { get; set; }

    public int EntryCount { get; set; }
    public ArchiveScanState ScanState { get; set; }
}

public enum ArchiveScanState
{
    Pending,
    Scanning,
    Done,
    Incomplete,
}

/// <summary>An identifier of a book (ISBN, UUID, FB2 document id, Calibre id): one row each, never a tag.</summary>
public sealed class BookIdentifier
{
    public const int MaxValueLength = 200;

    public int BookId { get; set; }
    public Book? Book { get; set; }
    public int TenantId { get; set; }

    /// <summary>Lower case: <c>isbn</c>, <c>uuid</c>, <c>fb2-id</c>, <c>calibre</c>…</summary>
    public string Scheme { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
