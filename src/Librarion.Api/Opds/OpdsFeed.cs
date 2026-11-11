using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Librarion.Core.Catalog;
using Librarion.Core.Formats;

namespace Librarion.Api.Opds;

/// <summary>
/// An OPDS 1.2 feed (Atom) as an answer: written into memory with <see cref="XmlWriter"/> (a page holds at most a
/// few dozen entries), then sent at once. The writer's methods follow a feed's order: <see cref="Start"/>, entries,
/// then the result is sent.
/// </summary>
public sealed partial class OpdsFeed : IResult
{
    public const string AtomNamespace = "http://www.w3.org/2005/Atom";
    public const string DcNamespace = "http://purl.org/dc/terms/";
    public const string OpdsNamespace = "http://opds-spec.org/2010/catalog";
    public const string NavigationType = "application/atom+xml;profile=opds-catalog;kind=navigation";
    public const string AcquisitionType = "application/atom+xml;profile=opds-catalog;kind=acquisition";

    /// <summary>A link to a feed that is either kind (a tag's page: a tree or its books, known only once it is read).</summary>
    public const string CatalogType = "application/atom+xml;profile=opds-catalog";

    public const string AcquisitionRel = "http://opds-spec.org/acquisition";
    public const string ImageRel = "http://opds-spec.org/image";
    public const string ThumbnailRel = "http://opds-spec.org/image/thumbnail";
    public const string NewRel = "http://opds-spec.org/sort/new";
    public const string OpenSearchType = "application/opensearchdescription+xml";

    /// <summary>An FB2 in a ZIP, as OPDS catalogs of FB2 libraries name it (reader apps of those catalogs know it).</summary>
    public const string Fb2ZipType = "application/fb2+zip";

    /// <summary>Formats also offered zipped (FB2 is XML: a third or less of its size, and many apps ask for it so).</summary>
    public static bool Zipped(string format) => format == "fb2";
    public const string OpenSearchNamespace = "http://a9.com/-/spec/opensearch/1.1/";

    /// <summary>The longest summary of a book entry (its description as plain text, cut at a word).</summary>
    public const int MaxSummary = 1000;

    private readonly MemoryStream _buffer = new();
    private readonly XmlWriter _xml;
    private readonly string _type;

    private OpdsFeed(string type)
    {
        _type = type;
        _xml = XmlWriter.Create(_buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
    }

    /// <summary>
    /// A feed with its head: id, title, now as its time, and its links (self, start, up, next, and the search of
    /// <paramref name="library"/>, else of the library the address names; none across libraries, Q11).
    /// </summary>
    public static OpdsFeed Start(string type, string id, string title, OpdsLinks links, string self, string? up = null, string? next = null,
        int? library = null)
    {
        var feed = new OpdsFeed(type);
        var xml = feed._xml;
        xml.WriteStartDocument();
        xml.WriteStartElement("feed", AtomNamespace);
        xml.WriteAttributeString("xmlns", "dc", null, DcNamespace);
        xml.WriteAttributeString("xmlns", "opds", null, OpdsNamespace);
        xml.WriteElementString("id", AtomNamespace, id);
        xml.WriteElementString("title", AtomNamespace, title);
        xml.WriteElementString("updated", AtomNamespace, Time(DateTime.UtcNow));
        xml.WriteStartElement("author", AtomNamespace);
        xml.WriteElementString("name", AtomNamespace, "Librarion");
        xml.WriteEndElement();
        feed.Link("self", self, type);
        feed.Link("start", links.Root, NavigationType);
        if (up is not null)
            feed.Link("up", up, NavigationType);
        if (next is not null)
            feed.Link("next", next, type);
        if ((library ?? links.CurrentLibrary) is { } searched)
        {
            // Both forms: the description (the standard) and the template itself (apps that read only that).
            feed.Link("search", links.OpenSearch(searched), OpenSearchType);
            feed.Link("search", links.Search(searched, null), AcquisitionType);
        }
        return feed;
    }

    /// <summary>An entry leading to another feed.</summary>
    public OpdsFeed Navigation(string id, string title, string href, string type, string? content = null, string rel = "subsection")
    {
        _xml.WriteStartElement("entry", AtomNamespace);
        _xml.WriteElementString("title", AtomNamespace, title);
        _xml.WriteElementString("id", AtomNamespace, id);
        _xml.WriteElementString("updated", AtomNamespace, Time(DateTime.UtcNow));
        if (content is not null)
        {
            _xml.WriteStartElement("content", AtomNamespace);
            _xml.WriteAttributeString("type", "text");
            _xml.WriteString(content);
            _xml.WriteEndElement();
        }
        Link(rel, href, type);
        _xml.WriteEndElement();
        return this;
    }

    /// <summary>
    /// A book: its title (the file name when it has none, D15), authors, language, categories, a summary (series and
    /// description), its cover and thumbnail (JPEG), and a link per file when the caller may download.
    /// </summary>
    public OpdsFeed Book(BookListItem book, BookEntryExtras? extras, OpdsLinks links, bool canDownload)
    {
        _xml.WriteStartElement("entry", AtomNamespace);
        _xml.WriteElementString("title", AtomNamespace, book.Title ?? book.FileName ?? $"#{book.Id}");
        _xml.WriteElementString("id", AtomNamespace, $"urn:librarion:book:{book.Id}");
        _xml.WriteElementString("updated", AtomNamespace, Time(extras?.Updated ?? DateTime.UtcNow));
        foreach (var author in book.Authors)
        {
            _xml.WriteStartElement("author", AtomNamespace);
            _xml.WriteElementString("name", AtomNamespace, author);
            _xml.WriteEndElement();
        }
        foreach (var language in extras?.Languages ?? [])
            _xml.WriteElementString("language", DcNamespace, language);
        foreach (var category in extras?.Categories ?? [])
        {
            _xml.WriteStartElement("category", AtomNamespace);
            _xml.WriteAttributeString("term", category);
            _xml.WriteAttributeString("label", category);
            _xml.WriteEndElement();
        }
        if (Summary(book, extras?.Description) is { Length: > 0 } summary)
        {
            _xml.WriteStartElement("summary", AtomNamespace);
            _xml.WriteAttributeString("type", "text");
            _xml.WriteString(summary);
            _xml.WriteEndElement();
        }

        Link(ImageRel, links.Cover(book.LibraryId, book.Id, "l", book.CoverVersion), Librarion.Core.Covers.CoverImages.ContentType);
        Link(ThumbnailRel, links.Cover(book.LibraryId, book.Id, "m", book.CoverVersion), Librarion.Core.Covers.CoverImages.ContentType);
        if (canDownload)
        {
            foreach (var file in extras?.Files ?? [])
            {
                _xml.WriteStartElement("link", AtomNamespace);
                _xml.WriteAttributeString("rel", AcquisitionRel);
                _xml.WriteAttributeString("href", links.File(book.LibraryId, book.Id, file.Id));
                _xml.WriteAttributeString("type", BookFormats.ContentType(file.Format));
                _xml.WriteAttributeString("length", file.Size.ToString(CultureInfo.InvariantCulture));
                _xml.WriteAttributeString("title", file.Format.ToUpperInvariant());
                _xml.WriteEndElement();
                if (Zipped(file.Format))
                {
                    // No length: the ZIP is made while it is sent.
                    _xml.WriteStartElement("link", AtomNamespace);
                    _xml.WriteAttributeString("rel", AcquisitionRel);
                    _xml.WriteAttributeString("href", links.ZippedFile(book.LibraryId, book.Id, file.Id));
                    _xml.WriteAttributeString("type", Fb2ZipType);
                    _xml.WriteAttributeString("title", "FB2.ZIP");
                    _xml.WriteEndElement();
                }
            }
        }
        _xml.WriteEndElement();
        return this;
    }

    /// <summary>
    /// A library's OpenSearch 1.1 description: its name and the search address as a template (<c>{searchTerms}</c> in
    /// the search language; no <c>{startPage}</c>: next pages come by key from the feed's next link).
    /// </summary>
    public static IResult OpenSearchDescription(string libraryName, OpdsLinks links, int library)
    {
        var buffer = new MemoryStream();
        using (var xml = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("OpenSearchDescription", OpenSearchNamespace);
            // OpenSearch 1.1 limits: ShortName 16 characters, LongName 48.
            xml.WriteElementString("ShortName", OpenSearchNamespace, libraryName.Length > 16 ? libraryName[..16] : libraryName);
            xml.WriteElementString("LongName", OpenSearchNamespace, libraryName.Length > 48 ? libraryName[..48] : libraryName);
            xml.WriteElementString("Description", OpenSearchNamespace, $"Search the books of {libraryName}.");
            xml.WriteElementString("InputEncoding", OpenSearchNamespace, "UTF-8");
            xml.WriteElementString("OutputEncoding", OpenSearchNamespace, "UTF-8");
            foreach (var type in new[] { AcquisitionType, "application/atom+xml" })
            {
                xml.WriteStartElement("Url", OpenSearchNamespace);
                xml.WriteAttributeString("type", type);
                xml.WriteAttributeString("template", links.Search(library, null));
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }
        return Results.Bytes(buffer.ToArray(), OpenSearchType + ";charset=utf-8");
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        _xml.WriteEndElement();
        _xml.WriteEndDocument();
        _xml.Flush();
        _xml.Dispose();
        httpContext.Response.ContentType = _type + ";charset=utf-8";
        httpContext.Response.ContentLength = _buffer.Length;
        _buffer.Position = 0;
        await _buffer.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
    }

    private void Link(string rel, string href, string type)
    {
        _xml.WriteStartElement("link", AtomNamespace);
        _xml.WriteAttributeString("rel", rel);
        _xml.WriteAttributeString("href", href);
        _xml.WriteAttributeString("type", type);
        _xml.WriteEndElement();
    }

    /// <summary>Atom's time: UTC, to the second.</summary>
    public static string Time(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The series and number on a line, then the description as plain text (tags dropped, entities read), cut at a word.</summary>
    public static string Summary(BookListItem book, string? description)
    {
        var parts = new List<string>();
        if (book.Series is { } series)
            parts.Add(book.SeriesNumber is { } number ? $"{series} #{number.ToString(CultureInfo.InvariantCulture)}" : series);
        if (description is not null)
        {
            var text = Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(description, " ")), " ").Trim();
            if (text.Length > MaxSummary)
            {
                var cut = text.LastIndexOf(' ', MaxSummary);
                text = text[..(cut > MaxSummary / 2 ? cut : MaxSummary)] + "…";
            }
            if (text.Length > 0)
                parts.Add(text);
        }
        return string.Join("\n", parts);
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
