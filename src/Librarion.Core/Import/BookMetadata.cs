using Librarion.Core.Data;

namespace Librarion.Core.Import;

/// <summary>
/// A field value as a book gives it (agents/docs/features/metadata-tags.md#import): the field's key, the raw value,
/// and the link's order and number. <paramref name="SortName"/> when the source says it ("King, Stephen").
/// </summary>
public sealed record FieldValue(string Field, string Value, int? Ordinal = null, double? Number = null, string? SortName = null);

/// <summary>What a metadata reader found in a book's head; every part may be missing.</summary>
/// <param name="Values">Field values (authors, series, categories…) in the book's order.</param>
/// <param name="Tags">Plain tags (FB2 keywords).</param>
public sealed record BookMetadata(
    ImportTagSourceKind Source,
    string? Title = null,
    string? Description = null,
    IReadOnlyList<FieldValue>? Values = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<(string Scheme, string Value)>? Identifiers = null)
{
    public IReadOnlyList<FieldValue> FieldValues => Values ?? [];
    public IReadOnlyList<string> PlainTags => Tags ?? [];
    public IReadOnlyList<(string Scheme, string Value)> Ids => Identifiers ?? [];
}

/// <summary>Reads a book's metadata from its head (agents/docs/features/archives.md#indexing-without-unpacking).</summary>
public interface IMetadataReader
{
    /// <summary>
    /// The metadata of a book of <paramref name="format"/>; <paramref name="open"/> gives its data from the start
    /// (decompressed, bounded to its size), read as little as the format needs. Null when the format has no reader or
    /// the book says nothing; a broken book throws, and is kept with its file name.
    /// </summary>
    BookMetadata? Read(string format, Func<Stream> open, long size, CancellationToken cancellationToken);
}

/// <summary>Until the readers come (stage 3.4): every book by its file name.</summary>
public sealed class NoMetadataReader : IMetadataReader
{
    public BookMetadata? Read(string format, Func<Stream> open, long size, CancellationToken cancellationToken) => null;
}
