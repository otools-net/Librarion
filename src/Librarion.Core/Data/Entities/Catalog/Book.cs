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

    /// <summary>
    /// The title and file name as searched (<see cref="Search.SearchText.ForBook"/>): folded words, indexed by the
    /// provider's full-text index (agents/docs/features/search.md). Never null.
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

    /// <summary>
    /// The first author's sort name ("King, Stephen"), kept by imports and edits; empty without an author (not null:
    /// the providers sort nulls at opposite ends, and keyset paging needs one order).
    /// </summary>
    public string SortAuthor { get; set; } = string.Empty;

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

    /// <summary>
    /// Set when none of its files is where the scans found them any more (agents/docs/features/missing-books.md): the
    /// book is kept with its metadata and curation, hidden from lists, until a file comes back or a user accepts its
    /// removal. Never removed on its own.
    /// </summary>
    public DateTime? MissingSince { get; set; }

    /// <summary>
    /// Where the book's metadata comes from (agents/docs/features/imports.md): its file's head, or an import from another
    /// source. A scan re-reads only <see cref="MetadataSource.Heads"/> books; an imported book changes by importing again.
    /// </summary>
    public MetadataSource MetadataSource { get; set; }

    /// <summary>A hash of the record the last import wrote: the same record again is skipped (an unchanged re-import).</summary>
    public long? ImportHash { get; set; }

    /// <summary>Blocking key for "same work" duplicates: a hash of the normalized title (else file stem) and first author.</summary>
    public long? WorkKey { get; set; }

    /// <summary>The columns a user edited (stage 6, D-6.3): a re-read or re-import keeps them.</summary>
    public BookEdits Edited { get; set; }

    public List<BookFile> Files { get; set; } = [];
    public List<BookTag> Tags { get; set; } = [];
    public List<BookIdentifier> Identifiers { get; set; } = [];
    public List<BookProperty> Properties { get; set; } = [];
}

/// <summary>Who owns a book's metadata.</summary>
public enum MetadataSource : byte
{
    /// <summary>Read from the book file's head by the scans.</summary>
    Heads,
    /// <summary>Imported from a Calibre library's <c>metadata.db</c>.</summary>
    Calibre,
    /// <summary>Imported from an INPX catalog.</summary>
    Inpx,
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

    /// <summary>The root a loose file was found under; null for an archive entry (its archive has the root).</summary>
    public int? RootId { get; set; }
    public LibraryRoot? Root { get; set; }

    /// <summary>The container archive; null for a loose file.</summary>
    public int? ArchiveId { get; set; }
    public Archive? Archive { get; set; }

    /// <summary>Path relative to <see cref="Root"/> (<c>/</c> separators), or the entry's name inside <see cref="Archive"/>.</summary>
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

    /// <summary>Set when a scan didn't find the file where it was: kept to be found again (same place, or moved).</summary>
    public DateTime? MissingSince { get; set; }
}

/// <summary>A ZIP container of books in a library, indexed in place (agents/docs/features/archives.md).</summary>
public sealed class Archive
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int LibraryId { get; set; }
    public Library? Library { get; set; }
    public int RootId { get; set; }
    public LibraryRoot? Root { get; set; }

    /// <summary>Relative to <see cref="Root"/>, <c>/</c> separators.</summary>
    public string Path { get; set; } = string.Empty;
    public long PathKey { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }

    /// <summary>Hash of the central directory: same size, time and fingerprint = unchanged, nothing read.</summary>
    public long? Fingerprint { get; set; }

    public int EntryCount { get; set; }
    public ArchiveScanState ScanState { get; set; }

    /// <summary>Set when a scan didn't find the archive: its entries are missing too, kept until they come back or are removed.</summary>
    public DateTime? MissingSince { get; set; }
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

/// <summary>
/// A value of a book that is neither a tag nor a column: an imported custom column of a kind that doesn't make tags
/// (Calibre's long text, yes/no, numbers, dates), shown on the book's page (agents/docs/features/imports.md). Written by
/// imports only, replaced with the book's other imported metadata.
/// </summary>
public sealed class BookProperty
{
    public const int MaxNameLength = 100;
    public const int MaxValueLength = 4000;

    public int BookId { get; set; }
    public Book? Book { get; set; }
    public int TenantId { get; set; }

    /// <summary>The column's name as the source shows it.</summary>
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// A folder a library's books are found under (several per library). Book paths are relative to it, so a library moved
/// to another disk only changes this row. A root missing at scan time is not a deletion: its books stay.
/// </summary>
public sealed class LibraryRoot
{
    public const int MaxPathLength = 1000;

    public int Id { get; set; }
    public int TenantId { get; set; }
    public int LibraryId { get; set; }
    public Library? Library { get; set; }

    /// <summary>Absolute and full (<see cref="System.IO.Path.GetFullPath(string)"/>), without a trailing separator.</summary>
    public string Path { get; set; } = string.Empty;

    public DateTime Created { get; set; }
}

/// <summary>A book's columns edited by a user, which imports leave as they are.</summary>
[Flags]
public enum BookEdits : byte
{
    None = 0,
    /// <summary>The title, with its sort key and search text.</summary>
    Title = 1,
    Description = 2,
    Rating = 4,
}
