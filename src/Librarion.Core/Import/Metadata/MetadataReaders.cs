using Librarion.Core.Formats;

namespace Librarion.Core.Import.Metadata;

/// <summary>
/// The metadata readers by format (agents/docs/features/metadata-tags.md): each <see cref="BookFormat"/> reads its own
/// (FB2, EPUB / KEPUB, CBZ, MOBI / AZW / AZW3, PDF); the others are kept by their file name.
/// </summary>
public sealed class MetadataReaders : IMetadataReader
{
    public BookMetadata? Read(string format, Func<Stream> open, long size, CancellationToken cancellationToken) =>
        BookFormats.Find(format)?.ReadMetadata(open, size, cancellationToken);
}
