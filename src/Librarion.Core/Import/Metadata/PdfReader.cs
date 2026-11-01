using System.Text;
using System.Xml;
using Librarion.Core.Data;

namespace Librarion.Core.Import.Metadata;

/// <summary>
/// A PDF's document information (title, author, subject, keywords), found the way the trailer points at it, else its
/// XMP packet when that is stored as plain text. Read only for PDFs up to <see cref="MaxSize"/> (the dictionary is
/// near the end, but where exactly takes the whole cross-reference to know); bigger ones keep their file name. An
/// information dictionary inside a compressed object stream is not unpacked: the XMP packet usually says the same.
/// </summary>
public static class PdfReader
{
    public const int MaxSize = 32 * 1024 * 1024;

    public static BookMetadata? Read(Stream stream, long size)
    {
        if (size > MaxSize)
            return null;
        var bytes = new byte[size];
        var length = stream.ReadAtLeast(bytes, (int)size, throwOnEndOfStream: false);
        var data = bytes.AsSpan(0, length);
        if (!data.StartsWith("%PDF"u8) && data[..Math.Min(1024, length)].IndexOf("%PDF"u8) < 0)
            return null;

        var info = InfoDictionary(data);
        string? title = info?.GetValueOrDefault("Title"), author = info?.GetValueOrDefault("Author");
        string? subject = info?.GetValueOrDefault("Subject"), keywords = info?.GetValueOrDefault("Keywords");
        if (title is null || author is null)
        {
            var xmp = Xmp(data);
            title ??= xmp.Title;
            author ??= xmp.Creators;
            keywords ??= xmp.Subjects;
        }

        var values = new List<FieldValue>();
        if (author is not null)
        {
            var ordinal = 0;
            foreach (var name in author.Split([";", " & ", " and "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(20))
                values.Add(new FieldValue("author", name, ordinal++));
        }

        if (keywords is not null)
            values.AddRange(keywords.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(30)
                .Where(k => k.Length <= 100).Select(k => new FieldValue("category", k)));

        title = title is { Length: > 0 } t && !LooksGenerated(t) ? (t.Length > Book.MaxTitleLength ? t[..Book.MaxTitleLength] : t) : null;
        if (title is null && values.Count == 0 && subject is null)
            return null;
        return new BookMetadata(ImportTagSourceKind.Pdf, title, subject, values);
    }

    /// <summary>Titles that are the producing program's leftovers ("Microsoft Word - draft.doc", "untitled").</summary>
    private static bool LooksGenerated(string title) =>
        title.StartsWith("Microsoft Word - ", StringComparison.OrdinalIgnoreCase) || title.Equals("untitled", StringComparison.OrdinalIgnoreCase)
        || title.EndsWith(".doc", StringComparison.OrdinalIgnoreCase) || title.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);

    /// <summary>The last <c>/Info n g R</c> (trailer or cross-reference stream) and that object's dictionary, when it is plain.</summary>
    private static Dictionary<string, string>? InfoDictionary(ReadOnlySpan<byte> data)
    {
        var at = data.LastIndexOf("/Info"u8);
        if (at < 0)
            return null;
        var position = at + 5;
        if (Integer(data, ref position) is not { } number || Integer(data, ref position) is not { } generation)
            return null;
        Skip(data, ref position);
        if (position >= data.Length || data[position] != 'R')
            return null;

        var header = Encoding.ASCII.GetBytes($"{number} {generation} obj");
        var start = -1;
        for (var from = 0; ;)
        {
            var found = data[from..].IndexOf(header);
            if (found < 0)
                break;
            var candidate = from + found;
            // Not the tail of another number ("12 0 obj" inside "112 0 obj").
            if (candidate == 0 || !char.IsAsciiDigit((char)data[candidate - 1]))
                start = candidate;
            from = candidate + header.Length;
        }

        if (start < 0)
            return null;
        position = start + header.Length;
        Skip(data, ref position);
        return position + 1 < data.Length && data[position] == '<' && data[position + 1] == '<' ? Dictionary(data, position + 2) : null;
    }

    private static Dictionary<string, string> Dictionary(ReadOnlySpan<byte> data, int position)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (position < data.Length)
        {
            Skip(data, ref position);
            if (position + 1 < data.Length && data[position] == '>' && data[position + 1] == '>')
                break;
            if (data[position] != '/')
                return result;
            var keyStart = ++position;
            while (position < data.Length && !IsDelimiter(data[position]))
                position++;
            var key = Encoding.ASCII.GetString(data[keyStart..position]);
            Skip(data, ref position);
            if (position >= data.Length)
                break;
            string? value = data[position] switch
            {
                (byte)'(' => Literal(data, ref position),
                (byte)'<' when position + 1 < data.Length && data[position + 1] != '<' => Hex(data, ref position),
                _ => null,
            };
            if (value is null)
            {
                // Something else (a number, a name, a reference, a date): skipped to the next key.
                while (position < data.Length && data[position] != '/' && data[position] != '>')
                    position++;
                continue;
            }

            if (SafeXml.Collapse(value, 4096) is { Length: > 0 } text)
                result[key] = text;
        }

        return result;
    }

    private static string Literal(ReadOnlySpan<byte> data, ref int position)
    {
        var bytes = new List<byte>();
        var depth = 0;
        position++;
        while (position < data.Length && bytes.Count < 16 * 1024)
        {
            var b = data[position++];
            if (b == '\\' && position < data.Length)
            {
                var next = data[position++];
                switch (next)
                {
                    case (byte)'n': bytes.Add((byte)'\n'); break;
                    case (byte)'r': bytes.Add((byte)'\r'); break;
                    case (byte)'t': bytes.Add((byte)'\t'); break;
                    case (byte)'b' or (byte)'f': break;
                    case (byte)'\r' or (byte)'\n': break;
                    case >= (byte)'0' and <= (byte)'7':
                        var octal = next - '0';
                        for (var i = 0; i < 2 && position < data.Length && data[position] is >= (byte)'0' and <= (byte)'7'; i++)
                            octal = octal * 8 + data[position++] - '0';
                        bytes.Add((byte)octal);
                        break;
                    default: bytes.Add(next); break;
                }
            }
            else if (b == '(')
            {
                depth++;
                bytes.Add(b);
            }
            else if (b == ')')
            {
                if (depth-- == 0)
                    break;
                bytes.Add(b);
            }
            else
                bytes.Add(b);
        }

        return Text([.. bytes]);
    }

    private static string Hex(ReadOnlySpan<byte> data, ref int position)
    {
        var bytes = new List<byte>();
        position++;
        int? high = null;
        while (position < data.Length && data[position] != '>' && bytes.Count < 16 * 1024)
        {
            var value = HexValue(data[position++]);
            if (value < 0)
                continue;
            if (high is { } h)
            {
                bytes.Add((byte)(h * 16 + value));
                high = null;
            }
            else
                high = value;
        }

        if (high is { } last)
            bytes.Add((byte)(last * 16));
        position++;
        return Text([.. bytes]);
    }

    /// <summary>UTF-16 with its byte-order mark, UTF-8 with its own (PDF 2.0), else PDFDocEncoding (Latin-1 near enough).</summary>
    private static string Text(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>The XMP packet's <c>dc:title</c>, <c>dc:creator</c> and <c>dc:subject</c>, when the packet is stored uncompressed.</summary>
    private static (string? Title, string? Creators, string? Subjects) Xmp(ReadOnlySpan<byte> data)
    {
        var start = data.IndexOf("<x:xmpmeta"u8);
        if (start < 0)
            return default;
        var end = data[start..].IndexOf("</x:xmpmeta>"u8);
        if (end < 0)
            return default;
        using var stream = new MemoryStream(data.Slice(start, end + 12).ToArray());
        string? title = null;
        var creators = new List<string>();
        var subjects = new List<string>();
        try
        {
            using var xml = SafeXml.Open(stream, 4 * 1024 * 1024);
            string? property = null;
            while (!xml.EOF)
            {
                if (xml.NodeType == XmlNodeType.Element && xml.NamespaceURI == "http://purl.org/dc/elements/1.1/")
                    property = xml.LocalName;
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "li" && property is "title" or "creator" or "subject")
                {
                    var text = SafeXml.Text(xml, 500);
                    if (text.Length == 0)
                        continue;
                    if (property == "title")
                        title ??= text;
                    else
                        (property == "creator" ? creators : subjects).Add(text);

                    continue;
                }

                xml.Read();
            }
        }
        catch (XmlException)
        {
            // A broken packet says nothing.
        }

        return (title, creators.Count > 0 ? string.Join("; ", creators) : null, subjects.Count > 0 ? string.Join(", ", subjects) : null);
    }

    private static long? Integer(ReadOnlySpan<byte> data, ref int position)
    {
        Skip(data, ref position);
        var start = position;
        while (position < data.Length && char.IsAsciiDigit((char)data[position]) && position - start < 10)
            position++;
        return position > start ? long.Parse(Encoding.ASCII.GetString(data[start..position]), System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static void Skip(ReadOnlySpan<byte> data, ref int position)
    {
        while (position < data.Length && data[position] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0 or 12)
            position++;
    }

    private static bool IsDelimiter(byte b) => b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)'/' or (byte)'(' or (byte)'<' or (byte)'[' or (byte)'>';

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
