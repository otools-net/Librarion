using System.Xml;
using Librarion.Core.Containers;
using Librarion.Core.Data;

namespace Librarion.Core.Import.Metadata;

/// <summary>
/// The package document of an EPUB (OPF 2 and 3): <c>META-INF/container.xml</c> names it, its <c>&lt;metadata&gt;</c>
/// gives title, creators by role (EPUB 3 <c>refines</c> too), subjects (categories), language, publisher, date,
/// identifiers, description, and the series (<c>calibre:series</c> or <c>belongs-to-collection</c>). Only those two
/// entries are read.
/// </summary>
public static class EpubReader
{
    private const int MaxDocument = 1024 * 1024;

    public static BookMetadata? Read(InnerZip zip)
    {
        var container = zip.Find("META-INF/container.xml");
        if (container is null)
            return null;
        string? opfPath = null;
        using (var xml = SafeXml.Open(zip.Open(container, MaxDocument), MaxDocument))
            while (xml.Read())
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "rootfile" && xml.GetAttribute("full-path") is { Length: > 0 } path)
                {
                    opfPath = path;
                    break;
                }

        if (opfPath is null || zip.Find(opfPath) is not { } opf)
            return null;
        using var reader = SafeXml.Open(zip.Open(opf, MaxDocument), MaxDocument);
        return Package(reader);
    }

    private sealed record Creator(string Name, string? Role, string? FileAs, string? Id);

    private static BookMetadata Package(XmlReader xml)
    {
        string? title = null, description = null, year = null, series = null, seriesIndex = null;
        var creators = new List<Creator>();
        var subjects = new List<string>();
        var publishers = new List<string>();
        var languages = new List<string>();
        var ids = new List<(string, string)>();
        var refines = new Dictionary<(string Id, string Property), string>();
        var collections = new Dictionary<string, string>();
        var inMetadata = false;

        while (!xml.EOF)
        {
            if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "metadata")
                break;
            if (xml.NodeType != XmlNodeType.Element)
            {
                xml.Read();
                continue;
            }

            if (xml.LocalName == "metadata")
            {
                inMetadata = true;
                xml.Read();
                continue;
            }

            if (!inMetadata)
            {
                xml.Read();
                continue;
            }

            var id = xml.GetAttribute("id");
            switch (xml.LocalName)
            {
                case "title":
                    var text = SafeXml.Text(xml, Book.MaxTitleLength);
                    title ??= text.Length > 0 ? text : null;
                    continue;
                case "creator" or "contributor":
                    var contributor = xml.LocalName == "contributor";
                    var role = Attribute(xml, "role");
                    var fileAs = Attribute(xml, "file-as");
                    var name = SafeXml.Text(xml, 200);
                    if (name.Length > 0)
                        creators.Add(new Creator(name, role ?? (contributor ? "ctb" : null), fileAs, id));
                    continue;
                case "subject":
                    if (SafeXml.Text(xml, 200) is { Length: > 0 } subject)
                        subjects.Add(subject);
                    continue;
                case "publisher":
                    if (SafeXml.Text(xml, 200) is { Length: > 0 } publisher)
                        publishers.Add(publisher);
                    continue;
                case "language":
                    if (Languages.Normalize(SafeXml.Text(xml, 50)) is { } language)
                        languages.Add(language);
                    continue;
                case "date":
                    // Read whatever: the reader must move past the element even when the value is known.
                    var date = SafeXml.Text(xml, 50);
                    year ??= Fb2Reader.Year(date);
                    continue;
                case "description":
                    var html = SafeXml.Text(xml, 64 * 1024);
                    description ??= Html(html);
                    continue;
                case "identifier":
                    var scheme = Attribute(xml, "scheme")?.ToLowerInvariant();
                    var value = SafeXml.Text(xml, BookIdentifier.MaxValueLength);
                    if (Isbns.Normalize(value) is { } isbn && (scheme == "isbn" || value.Contains("isbn", StringComparison.OrdinalIgnoreCase) || scheme is null && isbn.Length == 13))
                        ids.Add(("isbn", isbn));
                    else if (value.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase))
                        ids.Add(("uuid", value[9..].ToLowerInvariant()));
                    else if (scheme == "uuid")
                        ids.Add(("uuid", value.ToLowerInvariant()));
                    else if (scheme == "calibre")
                        ids.Add(("calibre", value));
                    continue;
                case "meta":
                    var metaName = xml.GetAttribute("name");
                    var content = xml.GetAttribute("content");
                    var property = xml.GetAttribute("property");
                    var refined = xml.GetAttribute("refines")?.TrimStart('#');
                    var body = SafeXml.Text(xml, 500);
                    if (metaName == "calibre:series" && content is { Length: > 0 })
                        series = content.Trim();
                    else if (metaName == "calibre:series_index")
                        seriesIndex = content;
                    else if (property is not null && refined is not null)
                        refines[(refined, property)] = body;
                    else if (property == "belongs-to-collection" && id is not null && body.Length > 0)
                        collections[id] = body;
                    continue;
            }

            xml.Read();
        }

        var values = new List<FieldValue>();
        var ordinals = new Dictionary<string, int>();
        foreach (var creator in creators)
        {
            var role = creator.Id is not null && refines.TryGetValue((creator.Id, "role"), out var refinedRole) ? refinedRole : creator.Role;
            var field = role?.Trim().ToLowerInvariant() switch
            {
                null or "" or "aut" => "author",
                "trl" => "translator",
                "edt" => "editor",
                "ill" => "illustrator",
                _ => null,
            };
            if (field is null)
                continue;
            var sort = creator.FileAs ?? (creator.Id is not null && refines.TryGetValue((creator.Id, "file-as"), out var refinedSort) ? refinedSort : null);
            var ordinal = ordinals.GetValueOrDefault(field);
            ordinals[field] = ordinal + 1;
            values.Add(new FieldValue(field, creator.Name, ordinal, SortName: sort));
        }

        if (series is not null)
            values.Add(new FieldValue("series", series, Number: Fb2Reader.Number(seriesIndex)));
        foreach (var (collectionId, name) in collections)
        {
            var type = refines.GetValueOrDefault((collectionId, "collection-type"));
            if (series is null && type is null or "series")
                values.Add(new FieldValue("series", name, Number: Fb2Reader.Number(refines.GetValueOrDefault((collectionId, "group-position")))));
        }

        values.AddRange(subjects.Select(s => new FieldValue("category", s)));
        values.AddRange(languages.Distinct().Select(l => new FieldValue("language", l)));
        values.AddRange(publishers.Take(1).Select(p => new FieldValue("publisher", p)));
        if (year is not null)
            values.Add(new FieldValue("year", year));
        return new BookMetadata(ImportTagSourceKind.Epub, title, description, values, null, ids);
    }

    /// <summary>The <c>opf:</c> attribute, with or without its namespace (OPF 2 writers differ).</summary>
    private static string? Attribute(XmlReader xml, string name)
    {
        for (var i = 0; i < xml.AttributeCount; i++)
        {
            xml.MoveToAttribute(i);
            if (xml.LocalName == name)
            {
                var value = xml.Value;
                xml.MoveToElement();
                return value;
            }
        }

        xml.MoveToElement();
        return null;
    }

    /// <summary>A description is often escaped HTML: tags out, entities of the common kind back to characters.</summary>
    internal static string? Html(string text)
    {
        if (text.Length == 0)
            return null;
        var plain = new System.Text.StringBuilder(text.Length);
        var inTag = false;
        foreach (var c in text)
        {
            if (c == '<')
            {
                inTag = true;
                plain.Append(' ');
            }
            else if (c == '>')
                inTag = false;
            else if (!inTag)
                plain.Append(c);
        }

        var decoded = System.Net.WebUtility.HtmlDecode(plain.ToString());
        return SafeXml.Collapse(decoded) is { Length: > 0 } result ? result : null;
    }
}

/// <summary>
/// A book that is itself a zip (EPUB, CBZ), opened from a loose file or from an entry of a container: a seekable
/// stream is used as it is; an entry's data is held in memory up to a limit (books are small; bigger ones are kept by
/// their name).
/// </summary>
public sealed class InnerZip : IDisposable
{
    public const long MaxInMemory = 64L * 1024 * 1024;

    private readonly Stream stream;
    private readonly ZipDirectory directory;
    private readonly Dictionary<string, ArchiveEntry> entries;

    private InnerZip(Stream stream, ZipDirectory directory, Dictionary<string, ArchiveEntry> entries)
    {
        this.stream = stream;
        this.directory = directory;
        this.entries = entries;
    }

    /// <summary>Null when the book is too big to hold or not a zip.</summary>
    public static InnerZip? Open(Func<Stream> open, long size, CancellationToken cancellationToken)
    {
        var data = open();
        if (!data.CanSeek)
        {
            if (size > MaxInMemory)
            {
                data.Dispose();
                return null;
            }

            var memory = new MemoryStream((int)size);
            using (data)
                data.CopyTo(memory);
            data = memory;
        }

        try
        {
            var directory = ZipDirectory.Locate(data);
            var entries = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in directory.Read(data, cancellationToken).Take(100_000))
                if (entry.Kind == ArchiveEntryKind.File)
                    entries.TryAdd(entry.Name.Replace('\\', '/'), entry);
            return new InnerZip(data, directory, entries);
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    public IEnumerable<ArchiveEntry> Entries => entries.Values;

    public ArchiveEntry? Find(string name) => entries.GetValueOrDefault(name.TrimStart('/'));

    /// <summary>An entry's data, at most <paramref name="max"/> bytes of it.</summary>
    public Stream Open(ArchiveEntry entry, long max)
    {
        if (entry.Size > max)
            throw new ArchiveException("An entry of the book is too large to be its metadata.");
        return directory.OpenEntry(stream, entry);
    }

    public void Dispose() => stream.Dispose();
}
