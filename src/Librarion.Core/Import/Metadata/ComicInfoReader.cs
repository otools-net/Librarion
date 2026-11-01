using System.Xml;
using Librarion.Core.Data;

namespace Librarion.Core.Import.Metadata;

/// <summary>A CBZ's <c>ComicInfo.xml</c> (the ComicRack schema): title, series and number, people by role, genres, publisher, language, year, summary.</summary>
public static class ComicInfoReader
{
    private const int MaxDocument = 1024 * 1024;

    public static BookMetadata? Read(InnerZip zip)
    {
        var info = zip.Find("ComicInfo.xml") ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith("/ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
        if (info is null)
            return null;
        using var xml = SafeXml.Open(zip.Open(info, MaxDocument), MaxDocument);
        string? title = null, summary = null, series = null, number = null, year = null;
        var values = new List<FieldValue>();
        while (!xml.EOF)
        {
            if (xml.NodeType != XmlNodeType.Element || xml.Depth != 1)
            {
                xml.Read();
                continue;
            }

            var name = xml.LocalName;
            var text = SafeXml.Text(xml, name == "Summary" ? 16 * 1024 : 500);
            if (text.Length == 0)
                continue;
            switch (name)
            {
                case "Title": title = text.Length > Book.MaxTitleLength ? text[..Book.MaxTitleLength] : text; break;
                case "Series": series = text; break;
                case "Number": number = text; break;
                case "Summary": summary = text; break;
                case "Year": year = Fb2Reader.Year(text); break;
                case "Writer": People("author", text); break;
                case "Translator": People("translator", text); break;
                case "Editor": People("editor", text); break;
                case "Penciller" or "Inker" or "Colorist" or "CoverArtist": People("illustrator", text); break;
                case "Publisher": values.Add(new FieldValue("publisher", text)); break;
                case "Genre": values.AddRange(Split(text).Select(g => new FieldValue("category", g))); break;
                case "LanguageISO":
                    if (Languages.Normalize(text) is { } language)
                        values.Add(new FieldValue("language", language));
                    break;
            }
        }

        if (series is not null)
        {
            values.Add(new FieldValue("series", series, Number: Fb2Reader.Number(number)));
            title ??= number is null ? series : $"{series} {number}";
        }

        if (year is not null)
            values.Add(new FieldValue("year", year));
        return new BookMetadata(ImportTagSourceKind.ComicInfo, title, summary, values);

        void People(string field, string text)
        {
            var ordinal = values.Count(v => v.Field == field);
            foreach (var person in Split(text))
                values.Add(new FieldValue(field, person, ordinal++));
        }
    }

    private static string[] Split(string text) => text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
