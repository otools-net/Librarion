namespace Librarion.Core.Formats;

/// <summary>
/// The formats Librarion knows (agents/docs/features/book-formats.md), and which files are books, by name: a loose file
/// or an archive entry with a book extension is a book of that format; a <c>.zip</c> that isn't a book format (EPUB,
/// CBZ, DOCX are zips too) is a container whose entries are looked at.
/// </summary>
public static class BookFormats
{
    /// <summary>Every format. A new one is added here: its key, extensions and media type, and what it reads.</summary>
    public static IReadOnlyList<BookFormat> All { get; } =
    [
        new Fb2Format(),
        new EpubFormat(),
        new EpubFormat("kepub", ".kepub", ".kepub.epub"),
        new PdfFormat(),
        new MobiFormat("mobi", "application/x-mobipocket-ebook", ".mobi"),
        new MobiFormat("azw", "application/vnd.amazon.ebook", ".azw"),
        new MobiFormat("azw3", "application/vnd.amazon.ebook", ".azw3"),
        new BookFormat("djvu", "image/vnd.djvu", ".djvu", ".djv"),
        new CbzFormat(),
        new BookFormat("cbr", "application/vnd.comicbook-rar", ".cbr"),
        new BookFormat("txt", "text/plain; charset=utf-8", ".txt"),
        new BookFormat("rtf", "application/rtf", ".rtf"),
        new BookFormat("doc", "application/msword", ".doc"),
        new BookFormat("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx"),
    ];

    private static readonly Dictionary<string, BookFormat> ByKey = All.ToDictionary(f => f.Key, StringComparer.Ordinal);

    /// <summary>By last extension (<c>.epub</c>); compound ones (<c>.kepub.epub</c>) are checked first, they are few.</summary>
    private static readonly Dictionary<string, BookFormat> ByExtension = All
        .SelectMany(f => f.Extensions.Where(e => e.LastIndexOf('.') == 0).Select(e => (Extension: e, Format: f)))
        .ToDictionary(x => x.Extension, x => x.Format, StringComparer.OrdinalIgnoreCase);

    private static readonly (string Extension, BookFormat Format)[] Compound = All
        .SelectMany(f => f.Extensions.Where(e => e.LastIndexOf('.') > 0).Select(e => (e, f)))
        .OrderByDescending(x => x.e.Length).ToArray();

    /// <summary>A format by its stored key; null for one this version doesn't know.</summary>
    public static BookFormat? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>The format of a book file (<c>fb2</c>, <c>epub</c>…), <c>kepub</c> for <c>.kepub.epub</c>; null for anything else.</summary>
    public static string? Of(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot < name.LastIndexOfAny(['/', '\\']))
            return null;
        foreach (var (extension, format) in Compound)
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && name.Length > extension.Length)
                return format.Key;
        return ByExtension.TryGetValue(name[dot..], out var found) ? found.Key : null;
    }

    /// <summary>The media type of a format; <c>application/octet-stream</c> for one this version doesn't know.</summary>
    public static string ContentType(string key) => Find(key)?.ContentType ?? "application/octet-stream";

    /// <summary>A ZIP container (or a one-book wrapper such as <c>book.fb2.zip</c>) to look inside.</summary>
    public static bool IsContainer(string name) => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
}
