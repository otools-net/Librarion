using System.Buffers.Binary;
using System.Text;
using Librarion.Core.Data;

namespace Librarion.Core.Import.Metadata;

/// <summary>
/// MOBI / AZW / AZW3 metadata from the head (agents/docs/features/archives.md): the PalmDB header points at record 0,
/// whose MOBI header carries the full title and the EXTH block (author 100, publisher 101, description 103, ISBN 104,
/// subject 105, date 106, ASIN 113, updated title 503, language 524). Nothing past record 0 is read.
/// </summary>
public static class MobiReader
{
    public const int MaxHead = 512 * 1024;

    static MobiReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static BookMetadata? Read(Stream stream)
    {
        var head = new byte[MaxHead];
        var length = stream.ReadAtLeast(head, MaxHead, throwOnEndOfStream: false);
        var data = head.AsSpan(0, length);
        if (length < 86 || !data.Slice(60, 8).SequenceEqual("BOOKMOBI"u8) && !data.Slice(60, 8).SequenceEqual("TEXtREAd"u8))
            return null;
        var records = BinaryPrimitives.ReadUInt16BigEndian(data[76..]);
        if (records == 0)
            return null;
        var record0 = (int)BinaryPrimitives.ReadUInt32BigEndian(data[78..]);
        if (record0 < 78 || record0 + 16 + 24 > length)
            return null;
        var palmTitle = Encoding.Latin1.GetString(data[..32]).TrimEnd('\0');
        var mobi = record0 + 16;
        if (!data.Slice(mobi, 4).SequenceEqual("MOBI"u8))
            return new BookMetadata(ImportTagSourceKind.Mobi, Title: palmTitle.Length > 0 ? palmTitle.Replace('_', ' ') : null);

        var headerLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(mobi + 4)..]);
        var encoding = BinaryPrimitives.ReadUInt32BigEndian(data[(mobi + 12)..]) == 65001 ? Encoding.UTF8 : Encoding.GetEncoding(1252);
        string? title = null;
        if (mobi + 92 <= length)
        {
            var titleOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(mobi + 68)..]);
            var titleLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(mobi + 72)..]);
            if (titleLength is > 0 and < 4096 && record0 + titleOffset >= 0 && record0 + titleOffset + titleLength <= length)
                title = encoding.GetString(data.Slice(record0 + titleOffset, titleLength));
        }

        string? description = null, year = null;
        var values = new List<FieldValue>();
        var ids = new List<(string, string)>();
        var flags = mobi + 0x70 + 4 <= length ? BinaryPrimitives.ReadUInt32BigEndian(data[(mobi + 0x70)..]) : 0;
        var exth = mobi + headerLength;
        if ((flags & 0x40) != 0 && exth + 12 <= length && data.Slice(exth, 4).SequenceEqual("EXTH"u8))
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(data[(exth + 8)..]);
            var at = exth + 12;
            int authors = 0;
            for (var i = 0; i < count && at + 8 <= length; i++)
            {
                var type = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
                var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(at + 4)..]);
                if (size < 8 || at + size > length)
                    break;
                var text = SafeXml.Collapse(encoding.GetString(data.Slice(at + 8, size - 8)), 64 * 1024);
                at += size;
                if (text.Length == 0)
                    continue;
                switch (type)
                {
                    case 100:
                        foreach (var author in text.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            values.Add(new FieldValue("author", author, authors++));
                        break;
                    case 101: values.Add(new FieldValue("publisher", text)); break;
                    case 103: description = EpubReader.Html(text); break;
                    case 104:
                        if (Isbns.Normalize(text) is { } isbn)
                            ids.Add(("isbn", isbn));
                        break;
                    case 105: values.Add(new FieldValue("category", text)); break;
                    case 106: year ??= Fb2Reader.Year(text); break;
                    case 113: ids.Add(("asin", text)); break;
                    case 503: title = text; break;
                    case 524:
                        if (Languages.Normalize(text) is { } language)
                            values.Add(new FieldValue("language", language));
                        break;
                }
            }
        }

        if (year is not null)
            values.Add(new FieldValue("year", year));
        title = title?.Trim() is { Length: > 0 } t ? (t.Length > Book.MaxTitleLength ? t[..Book.MaxTitleLength] : t) : null;
        return new BookMetadata(ImportTagSourceKind.Mobi, title, description, values, null, ids);
    }
}
