using System.Globalization;
using System.Xml;
using Librarion.Core.Data;

namespace Librarion.Core.Import.Metadata;

/// <summary>
/// An FB2's <c>&lt;description&gt;</c>, read until it ends and never further (agents/docs/features/archives.md): the
/// covers and the text after it are not read at import. Title, authors (sort name from the parts), translators,
/// genres (named by <see cref="Fb2Genres"/>), series with numbers, language, year, publisher, annotation, keywords
/// (plain tags), the document id and the ISBN.
/// </summary>
public static class Fb2Reader
{
    /// <summary>Most descriptions are a few KB; one with a long annotation and embedded junk stays far under this.</summary>
    public const int MaxHead = 1024 * 1024;

    private enum Section { None, Title, SourceTitle, Document, Publish }

    public static BookMetadata? Read(Stream stream)
    {
        using var xml = SafeXml.Open(new HeadStream(stream, MaxHead), MaxHead);
        string? title = null, description = null, year = null;
        var values = new List<FieldValue>();
        var tags = new List<string>();
        var ids = new List<(string, string)>();
        var section = Section.None;
        int authors = 0, translators = 0, documentDepth = -1;
        var any = false;
        try
        {
            while (!xml.EOF)
            {
                if (xml.NodeType == XmlNodeType.EndElement)
                {
                    if (xml.LocalName == "description")
                        break;
                    if (xml.LocalName is "title-info" or "src-title-info" or "document-info" or "publish-info")
                        section = Section.None;
                    xml.Read();
                    continue;
                }

                if (xml.NodeType != XmlNodeType.Element)
                {
                    xml.Read();
                    continue;
                }

                any = true;
                switch (xml.LocalName, section)
                {
                    case ("body", _):
                        // No description before the text: nothing more to find.
                        return Result();
                    case ("title-info", _):
                        section = Section.Title;
                        break;
                    case ("src-title-info", _):
                        section = Section.SourceTitle;
                        break;
                    case ("document-info", _):
                        section = Section.Document;
                        documentDepth = xml.Depth;
                        break;
                    case ("publish-info", _):
                        section = Section.Publish;
                        break;
                    case ("genre", Section.Title):
                        if (SafeXml.Text(xml) is { Length: > 0 } genre)
                            values.Add(new FieldValue("category", Fb2Genres.Name(genre)));
                        continue;
                    case ("author", Section.Title):
                        if (Person(xml) is { } author)
                            values.Add(new FieldValue("author", author.Name, authors++, SortName: author.Sort));
                        continue;
                    case ("translator", Section.Title):
                        if (Person(xml) is { } translator)
                            values.Add(new FieldValue("translator", translator.Name, translators++, SortName: translator.Sort));
                        continue;
                    case ("book-title", Section.Title):
                        title = SafeXml.Text(xml, Book.MaxTitleLength);
                        continue;
                    case ("annotation", Section.Title):
                        description = SafeXml.Paragraphs(xml);
                        continue;
                    case ("keywords", Section.Title):
                        tags.AddRange(SafeXml.Text(xml).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        continue;
                    case ("date", Section.Title):
                        var value = xml.GetAttribute("value");
                        var text = SafeXml.Text(xml);
                        year ??= Year(value) ?? Year(text);
                        continue;
                    case ("lang", Section.Title):
                        if (Languages.Normalize(SafeXml.Text(xml)) is { } language)
                            values.Add(new FieldValue("language", language));
                        continue;
                    case ("sequence", Section.Title):
                        if (xml.GetAttribute("name")?.Trim() is { Length: > 0 } series)
                            values.Add(new FieldValue("series", series, Number: Number(xml.GetAttribute("number"))));
                        break;
                    case ("id", Section.Document) when xml.Depth == documentDepth + 1:
                        // The document's own id only: the file maker's <author> (and <publisher>) inside
                        // <document-info> may carry an <id> of the person, shared by every file they made.
                        if (SafeXml.Text(xml) is { Length: > 0 } id)
                            ids.Add(("fb2-id", id));
                        continue;
                    case ("publisher", Section.Publish):
                        if (SafeXml.Text(xml) is { Length: > 0 } publisher)
                            values.Add(new FieldValue("publisher", publisher));
                        continue;
                    case ("year", Section.Publish):
                        // Read whatever: the reader must move past the element even when the year is known.
                        var published = SafeXml.Text(xml);
                        year ??= Year(published);
                        continue;
                    case ("isbn", Section.Publish):
                        if (Isbns.Normalize(SafeXml.Text(xml)) is { } isbn)
                            ids.Add(("isbn", isbn));
                        continue;
                }

                xml.Read();
            }
        }
        catch (XmlException) when (title is not null)
        {
            // Broken after the title: what was read is kept.
        }

        return any ? Result() : null;

        BookMetadata Result()
        {
            if (year is not null)
                values.Add(new FieldValue("year", year));
            return new BookMetadata(ImportTagSourceKind.Fb2, title, description, values, tags, ids);
        }
    }

    /// <summary>"First Middle Last" and "Last, First Middle" from an FB2 author's parts; the nickname alone when that's all.</summary>
    private static (string Name, string Sort)? Person(XmlReader xml)
    {
        if (xml.IsEmptyElement)
        {
            xml.Read();
            return null;
        }

        string? first = null, middle = null, last = null, nickname = null;
        var depth = xml.Depth;
        xml.Read();
        while (!xml.EOF && xml.Depth > depth)
        {
            if (xml.NodeType != XmlNodeType.Element)
            {
                xml.Read();
                continue;
            }

            switch (xml.LocalName)
            {
                case "first-name": first = SafeXml.Text(xml, 100); break;
                case "middle-name": middle = SafeXml.Text(xml, 100); break;
                case "last-name": last = SafeXml.Text(xml, 100); break;
                case "nickname": nickname = SafeXml.Text(xml, 100); break;
                default: xml.Skip(); break;
            }
        }

        if (xml.NodeType == XmlNodeType.EndElement)
            xml.Read();
        var given = string.Join(' ', new[] { first, middle }.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (!string.IsNullOrWhiteSpace(last))
            return (given.Length > 0 ? $"{given} {last}" : last, given.Length > 0 ? $"{last}, {given}" : last);
        if (given.Length > 0)
            return (given, given);
        return string.IsNullOrWhiteSpace(nickname) ? null : (nickname, nickname);
    }

    internal static string? Year(string? text)
    {
        if (text is null)
            return null;
        for (var i = 0; i + 4 <= text.Length; i++)
        {
            if (char.IsAsciiDigit(text[i]) && char.IsAsciiDigit(text[i + 1]) && char.IsAsciiDigit(text[i + 2]) && char.IsAsciiDigit(text[i + 3])
                && (i + 4 == text.Length || !char.IsAsciiDigit(text[i + 4])) && (i == 0 || !char.IsAsciiDigit(text[i - 1])))
            {
                var year = int.Parse(text.AsSpan(i, 4), CultureInfo.InvariantCulture);
                return year is >= 1000 and <= 2999 ? text.Substring(i, 4) : null;
            }
        }

        return null;
    }

    internal static double? Number(string? text) =>
        double.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number is >= 0 and < 1e6
            ? number
            : null;
}

/// <summary>Book languages as ISO 639-1 where one exists (<c>rus</c>, <c>ru-RU</c> → <c>ru</c>), lower case.</summary>
public static class Languages
{
    private static readonly Dictionary<string, string> ThreeLetters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "en", ["rus"] = "ru", ["ukr"] = "uk", ["bel"] = "be", ["deu"] = "de", ["ger"] = "de", ["fra"] = "fr", ["fre"] = "fr",
        ["spa"] = "es", ["ita"] = "it", ["por"] = "pt", ["pol"] = "pl", ["ces"] = "cs", ["cze"] = "cs", ["nld"] = "nl", ["dut"] = "nl",
        ["swe"] = "sv", ["nor"] = "no", ["dan"] = "da", ["fin"] = "fi", ["hun"] = "hu", ["ron"] = "ro", ["rum"] = "ro", ["bul"] = "bg",
        ["srp"] = "sr", ["hrv"] = "hr", ["slk"] = "sk", ["slo"] = "sk", ["slv"] = "sl", ["ell"] = "el", ["gre"] = "el", ["tur"] = "tr",
        ["heb"] = "he", ["ara"] = "ar", ["fas"] = "fa", ["per"] = "fa", ["hin"] = "hi", ["zho"] = "zh", ["chi"] = "zh", ["jpn"] = "ja",
        ["kor"] = "ko", ["vie"] = "vi", ["tha"] = "th", ["ind"] = "id", ["lat"] = "la", ["lit"] = "lt", ["lav"] = "lv", ["est"] = "et",
        ["kaz"] = "kk", ["cat"] = "ca", ["eus"] = "eu", ["baq"] = "eu", ["glg"] = "gl", ["epo"] = "eo",
    };

    public static string? Normalize(string? text)
    {
        var code = text?.Trim().ToLowerInvariant().Split(['-', '_'], 2)[0];
        if (string.IsNullOrEmpty(code) || code.Length > 8 || !code.All(char.IsAsciiLetterLower))
            return null;
        return code.Length == 3 && ThreeLetters.TryGetValue(code, out var two) ? two : code;
    }
}

/// <summary>ISBNs as their 10 or 13 characters (digits, a final X), hyphens and spaces off; anything else refused.</summary>
public static class Isbns
{
    public static string? Normalize(string? text)
    {
        if (text is null)
            return null;
        var isbn = new string(text.Where(c => char.IsAsciiDigit(c) || c is 'x' or 'X').Select(char.ToUpperInvariant).ToArray());
        if (text.StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase))
            isbn = new string(text[9..].Where(c => char.IsAsciiDigit(c) || c is 'x' or 'X').Select(char.ToUpperInvariant).ToArray());
        return isbn.Length is 10 or 13 && isbn[..^1].All(char.IsAsciiDigit) ? isbn : null;
    }
}
