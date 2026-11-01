namespace Librarion.Core.Import.Metadata;

/// <summary>
/// The metadata readers by format (agents/docs/features/metadata-tags.md): FB2, EPUB / KEPUB, CBZ, MOBI / AZW / AZW3,
/// PDF. DJVU, TXT, RTF, DOC, DOCX and CBR are kept by their file name.
/// </summary>
public sealed class MetadataReaders : IMetadataReader
{
    public BookMetadata? Read(string format, Func<Stream> open, long size, CancellationToken cancellationToken)
    {
        switch (format)
        {
            case "fb2":
                using (var stream = open())
                    return Fb2Reader.Read(stream);
            case "epub" or "kepub":
                using (var zip = InnerZip.Open(open, size, cancellationToken))
                    return zip is null ? null : EpubReader.Read(zip);
            case "cbz":
                using (var zip = InnerZip.Open(open, size, cancellationToken))
                    return zip is null ? null : ComicInfoReader.Read(zip);
            case "mobi" or "azw" or "azw3":
                using (var stream = open())
                    return MobiReader.Read(stream);
            case "pdf":
                if (size > PdfReader.MaxSize)
                    return null;
                using (var stream = open())
                    return PdfReader.Read(stream, size);
            default:
                return null;
        }
    }
}
