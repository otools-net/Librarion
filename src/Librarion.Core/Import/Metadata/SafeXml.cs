using System.Text;
using System.Xml;

namespace Librarion.Core.Import.Metadata;

/// <summary>
/// XML written by strangers (agents/docs/features/archives.md#hostile-and-broken-archives): no DTD processed, no
/// entity expanded, nothing resolved from outside, a cap on what is read. Code pages (FB2s are often windows-1251)
/// come from the XML declaration.
/// </summary>
internal static class SafeXml
{
    static SafeXml() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static XmlReader Open(Stream stream, long maxCharacters) => XmlReader.Create(stream, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        MaxCharactersInDocument = maxCharacters,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
    });

    /// <summary>The text of the element the reader is on (its children's too), spaces collapsed; the reader ends after it.</summary>
    public static string Text(XmlReader reader, int max = 64 * 1024)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return "";
        }

        var text = new StringBuilder();
        var depth = reader.Depth;
        while (reader.Read() && reader.Depth > depth)
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace && text.Length < max)
                text.Append(reader.Value).Append(' ');
        }

        if (reader.NodeType == XmlNodeType.EndElement)
            reader.Read();
        return Collapse(text.ToString(), max);
    }

    /// <summary>Paragraphs of an FB2 annotation or an HTML description: one per <c>p</c> or <c>br</c>, blank lines between.</summary>
    public static string Paragraphs(XmlReader reader, int max = 16 * 1024)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return "";
        }

        var paragraphs = new List<string>();
        var current = new StringBuilder();
        var depth = reader.Depth;
        var length = 0;
        while (reader.Read() && reader.Depth > depth)
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                current.Append(reader.Value).Append(' ');
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "p" or "div" or "li" or "v" or "subtitle"
                     || reader.NodeType == XmlNodeType.Element && reader.LocalName == "br")
                Flush();
        }

        Flush();
        if (reader.NodeType == XmlNodeType.EndElement)
            reader.Read();
        var all = string.Join("\n\n", paragraphs);
        return all.Length > max ? all[..max] : all;

        void Flush()
        {
            var text = Collapse(current.ToString(), max);
            current.Clear();
            if (text.Length > 0 && length < max)
            {
                paragraphs.Add(text);
                length += text.Length + 2;
            }
        }
    }

    public static string Collapse(string text, int max = int.MaxValue)
    {
        var builder = new StringBuilder(Math.Min(text.Length, max));
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
                builder.Append(' ');
            space = false;
            builder.Append(c);
            if (builder.Length >= max)
                break;
        }

        return builder.ToString();
    }
}

/// <summary>A stream that ends after <paramref name="max"/> bytes: a head read never reads a whole huge book.</summary>
internal sealed class HeadStream(Stream inner, long max) : Stream
{
    private long position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var left = max - position;
        if (left <= 0)
            return 0;
        var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, left)]);
        position += read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
