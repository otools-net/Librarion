using System.Buffers.Binary;
using System.Xml;
using Librarion.Core.Containers;
using Librarion.Core.Formats;
using Librarion.Core.Import.Metadata;

namespace Librarion.Core.Covers;

/// <summary>
/// The cover image inside a book, as bytes still encoded (agents/docs/features/book-display.md#cover), for the
/// formats that carry one (<see cref="BookFormat.ExtractCover"/> calls these): FB2's
/// <c>&lt;coverpage&gt;</c> → its <c>&lt;binary&gt;</c> (the whole file is read once, the binaries are at its end),
/// EPUB's cover item from the OPF manifest (EPUB 3 <c>cover-image</c>, EPUB 2 <c>&lt;meta name="cover"&gt;</c>, else an
/// image item named cover), CBZ's first image in natural name order, MOBI / AZW3's EXTH 201 image record. PDF, DJVU
/// and the rest have none (no page renderer on the server). Null when the book has none or it can't be read.
/// </summary>
public static class CoverExtractor
{
    /// <summary>A cover larger than this is not a cover (and not decoded).</summary>
    public const int MaxCoverBytes = 16 * 1024 * 1024;

    /// <summary>An FB2 is read to its end for its binaries: past this it is not looked at.</summary>
    public const long MaxFb2Size = 128L * 1024 * 1024;

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"];

    /// <summary>Whether books of this format can carry a cover of their own.</summary>
    public static bool HasCovers(string format) => BookFormats.Find(format)?.HasCovers == true;

    /// <summary>The format's cover (<see cref="BookFormat.ExtractCover"/>); null for none, and for a book that can't be read.</summary>
    public static byte[]? Extract(string format, Func<Stream> open, long size, CancellationToken cancellationToken = default)
    {
        try
        {
            return BookFormats.Find(format)?.ExtractCover(open, size, cancellationToken);
        }
        catch (Exception error) when (error is IOException or XmlException or InvalidDataException or FormatException or NotSupportedException)
        {
            return null;
        }
    }

    internal static byte[]? Fb2(Stream stream, long size)
    {
        using var xml = SafeXml.Open(stream, Math.Max(size, 1024) * 2);
        string? coverId = null;
        var inCoverPage = false;
        // Each branch moves the reader itself: Skip() already stands on the next node.
        xml.Read();
        while (!xml.EOF)
        {
            if (xml.NodeType != XmlNodeType.Element)
            {
                if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "coverpage")
                    inCoverPage = false;
                xml.Read();
                continue;
            }

            switch (xml.LocalName)
            {
                case "coverpage":
                    inCoverPage = !xml.IsEmptyElement;
                    xml.Read();
                    break;
                case "image" when inCoverPage && coverId is null:
                    coverId = Href(xml)?.TrimStart('#');
                    xml.Read();
                    break;
                case "body" when coverId is null:
                    // The description is over without a cover page: nothing to look for in the text.
                    return null;
                case "body":
                    xml.Skip();
                    break;
                case "binary" when string.Equals(xml.GetAttribute("id"), coverId, StringComparison.Ordinal):
                    return Base64(xml);
                case "binary":
                    xml.Skip();
                    break;
                default:
                    xml.Read();
                    break;
            }
        }

        return null;
    }

    /// <summary>The <c>href</c> of an FB2 image, in whatever namespace prefix the file used (<c>l:</c>, <c>xlink:</c>…).</summary>
    private static string? Href(XmlReader xml)
    {
        for (var i = 0; i < xml.AttributeCount; i++)
        {
            xml.MoveToAttribute(i);
            if (xml.LocalName == "href")
            {
                var value = xml.Value;
                xml.MoveToElement();
                return value;
            }
        }

        xml.MoveToElement();
        return null;
    }

    private static byte[]? Base64(XmlReader xml)
    {
        using var data = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = xml.ReadElementContentAsBase64(buffer, 0, buffer.Length)) > 0)
        {
            if (data.Length + read > MaxCoverBytes)
                return null;
            data.Write(buffer, 0, read);
        }

        return data.Length > 0 ? data.ToArray() : null;
    }

    internal static byte[]? Epub(InnerZip zip)
    {
        if (zip.Find("META-INF/container.xml") is not { } container)
            return null;
        string? opfPath = null;
        using (var xml = SafeXml.Open(zip.Open(container, 1024 * 1024), 1024 * 1024))
            while (xml.Read())
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "rootfile" && xml.GetAttribute("full-path") is { Length: > 0 } path)
                {
                    opfPath = path;
                    break;
                }

        if (opfPath is null || zip.Find(opfPath) is not { } opf)
            return null;

        string? metaCover = null, coverImage = null, namedCover = null;
        var items = new Dictionary<string, (string Href, string? Type)>(StringComparer.Ordinal);
        using (var xml = SafeXml.Open(zip.Open(opf, 4 * 1024 * 1024), 4 * 1024 * 1024))
        {
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element)
                    continue;
                if (xml.LocalName == "meta" && xml.GetAttribute("name") == "cover" && xml.GetAttribute("content") is { Length: > 0 } content)
                    metaCover ??= content;
                else if (xml.LocalName == "item" && xml.GetAttribute("id") is { } id && xml.GetAttribute("href") is { Length: > 0 } href)
                {
                    var type = xml.GetAttribute("media-type");
                    items.TryAdd(id, (href, type));
                    var image = type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
                    if (image && xml.GetAttribute("properties")?.Split(' ').Contains("cover-image") == true)
                        coverImage ??= href;
                    else if (image && (id.Contains("cover", StringComparison.OrdinalIgnoreCase) || href.Contains("cover", StringComparison.OrdinalIgnoreCase)))
                        namedCover ??= href;
                }
            }
        }

        var chosen = coverImage
            ?? (metaCover is not null && items.TryGetValue(metaCover, out var item) && item.Type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != false ? item.Href : null)
            ?? namedCover;
        if (chosen is null)
            return null;
        var folder = opfPath.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";
        var target = Resolve(folder, Uri.UnescapeDataString(chosen.Split('#')[0]));
        return target is not null && zip.Find(target) is { } entry ? Read(zip, entry) : null;
    }

    /// <summary>A manifest href against the OPF's folder (<c>../</c> resolved; null when it leaves the book).</summary>
    private static string? Resolve(string folder, string href)
    {
        var parts = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var part in href.Split('/'))
        {
            if (part is "" or ".")
                continue;
            if (part == "..")
            {
                if (parts.Count == 0)
                    return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else
                parts.Add(part);
        }

        return string.Join('/', parts);
    }

    internal static byte[]? Comic(InnerZip zip)
    {
        var first = zip.Entries
            .Where(e => ImageExtensions.Any(x => e.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase)) && !e.Name.Contains("__MACOSX", StringComparison.Ordinal))
            .Order(Comparer<ArchiveEntry>.Create((a, b) => NaturalCompare(a.Name, b.Name)))
            .FirstOrDefault();
        return first is null ? null : Read(zip, first);
    }

    private static byte[]? Read(InnerZip zip, ArchiveEntry entry)
    {
        if (entry.Size is <= 0 or > MaxCoverBytes)
            return null;
        using var data = zip.Open(entry, MaxCoverBytes);
        var bytes = new byte[entry.Size];
        data.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Names compared with their digit runs as numbers: <c>page2</c> before <c>page10</c>.</summary>
    internal static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var x = a[si..i].TrimStart('0');
                var y = b[sj..j].TrimStart('0');
                var order = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
                if (order != 0)
                    return order;
                continue;
            }

            var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
            if (c != 0)
                return c;
            i++;
            j++;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>
    /// MOBI / AZW3: record 0's MOBI header gives the first image record (offset 0x6C), EXTH 201 the cover's offset from
    /// it; the PalmDB record table says where that record lies. The file is read forward to it (an entry can't seek).
    /// </summary>
    internal static byte[]? Mobi(Stream stream)
    {
        var head = new byte[MobiReader.MaxHead];
        var length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var data = head.AsSpan(0, length);
        if (length < 86 || !data.Slice(60, 8).SequenceEqual("BOOKMOBI"u8))
            return null;
        var records = BinaryPrimitives.ReadUInt16BigEndian(data[76..]);
        if (records == 0 || 78 + records * 8 > length)
            return null;
        long RecordOffset(int index) => BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(78 + index * 8));
        var record0 = (int)RecordOffset(0);
        var mobi = record0 + 16;
        if (mobi + 0x74 > length || !data.Slice(mobi, 4).SequenceEqual("MOBI"u8))
            return null;
        var headerLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(mobi + 4)..]);
        var firstImage = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(mobi + 0x6C)..]);
        var flags = BinaryPrimitives.ReadUInt32BigEndian(data[(mobi + 0x70)..]);
        var exth = mobi + headerLength;
        if ((flags & 0x40) == 0 || exth + 12 > length || !data.Slice(exth, 4).SequenceEqual("EXTH"u8))
            return null;

        int? coverOffset = null;
        var count = BinaryPrimitives.ReadUInt32BigEndian(data[(exth + 8)..]);
        var at = exth + 12;
        for (var i = 0; i < count && at + 8 <= length; i++)
        {
            var type = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(at + 4)..]);
            if (size < 8 || at + size > length)
                break;
            if (type == 201 && size >= 12)
                coverOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(at + 8)..]);
            at += size;
        }

        if (coverOffset is not { } offset || firstImage <= 0 || firstImage + offset + 1 >= records)
            return null;
        var index = firstImage + offset;
        long start = RecordOffset(index), end = RecordOffset(index + 1);
        if (start < 0 || end <= start || end - start > MaxCoverBytes)
            return null;

        var cover = new byte[end - start];
        if (end <= length)
        {
            data.Slice((int)start, cover.Length).CopyTo(cover);
            return cover;
        }

        // Past the head: skip forward to the record, then read it.
        if (start < length)
            return null;
        var position = (long)length;
        var skip = new byte[64 * 1024];
        while (position < start)
        {
            var read = stream.Read(skip, 0, (int)Math.Min(skip.Length, start - position));
            if (read == 0)
                return null;
            position += read;
        }

        return stream.ReadAtLeast(cover, cover.Length, throwOnEndOfStream: false) == cover.Length ? cover : null;
    }
}
