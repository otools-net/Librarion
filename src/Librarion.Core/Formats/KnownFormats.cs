using Librarion.Core.Covers;
using Librarion.Core.Import;
using Librarion.Core.Import.Metadata;

namespace Librarion.Core.Formats;

/// <summary>FictionBook 2: the <c>&lt;description&gt;</c> head, the cover from its <c>&lt;binary&gt;</c> (read to the end).</summary>
public sealed class Fb2Format() : BookFormat("fb2", "application/x-fictionbook+xml", ".fb2")
{
    public override bool HasCovers => true;

    public override BookMetadata? ReadMetadata(Func<Stream> open, long size, CancellationToken cancellationToken)
    {
        using var stream = open();
        return Fb2Reader.Read(stream);
    }

    public override byte[]? ExtractCover(Func<Stream> open, long size, CancellationToken cancellationToken)
    {
        if (size > CoverExtractor.MaxFb2Size)
            return null;
        using var stream = open();
        return CoverExtractor.Fb2(stream, size);
    }
}

/// <summary>EPUB (and Kobo's KEPUB, the same container): the OPF package, the cover from its manifest.</summary>
public class EpubFormat(string key = "epub", params string[] extensions) : BookFormat(key, "application/epub+zip", extensions.Length > 0 ? extensions : [".epub"])
{
    public override bool HasCovers => true;

    public override BookMetadata? ReadMetadata(Func<Stream> open, long size, CancellationToken cancellationToken)
    {
        using var zip = InnerZip.Open(open, size, cancellationToken);
        return zip is null ? null : EpubReader.Read(zip);
    }

    public override byte[]? ExtractCover(Func<Stream> open, long size, CancellationToken cancellationToken)
    {
        using var zip = InnerZip.Open(open, size, cancellationToken);
        return zip is null ? null : CoverExtractor.Epub(zip);
    }
}

/// <summary>Comic book zip: <c>ComicInfo.xml</c>, the first page as the cover.</summary>
public sealed class CbzFormat() : BookFormat("cbz", "application/vnd.comicbook+zip", ".cbz")
{
    public override bool HasCovers => true;

    public override BookMetadata? ReadMetadata(Func<Stream> open, long size, CancellationToken cancellationToken)
    {
        using var zip = InnerZip.Open(open, size, cancellationToken);
        return zip is null ? null : ComicInfoReader.Read(zip);
    }

    public override byte[]? ExtractCover(Func<Stream> open, long size, CancellationToken cancellationToken)
    {
        using var zip = InnerZip.Open(open, size, cancellationToken);
        return zip is null ? null : CoverExtractor.Comic(zip);
    }
}

/// <summary>MOBI and Amazon's AZW / AZW3 (KF8), one PalmDB layout: the EXTH head, the cover from EXTH 201.</summary>
public sealed class MobiFormat(string key, string contentType, params string[] extensions) : BookFormat(key, contentType, extensions)
{
    public override bool HasCovers => true;

    public override BookMetadata? ReadMetadata(Func<Stream> open, long size, CancellationToken cancellationToken)
    {
        using var stream = open();
        return MobiReader.Read(stream);
    }

    public override byte[]? ExtractCover(Func<Stream> open, long size, CancellationToken cancellationToken)
    {
        using var stream = open();
        return CoverExtractor.Mobi(stream);
    }
}

/// <summary>PDF: the info dictionary or XMP when the file is small enough to hold; no cover (no page renderer on the server).</summary>
public sealed class PdfFormat() : BookFormat("pdf", "application/pdf", ".pdf")
{
    public override BookMetadata? ReadMetadata(Func<Stream> open, long size, CancellationToken cancellationToken)
    {
        if (size > PdfReader.MaxSize)
            return null;
        using var stream = open();
        return PdfReader.Read(stream, size);
    }
}
