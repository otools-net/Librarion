namespace Librarion.Core.Import;

/// <summary>
/// Which files are books, by name (agents/docs/features/archives.md): a loose file or an archive entry with a book
/// extension is a book of that format; a <c>.zip</c> that isn't a book format (EPUB, CBZ, DOCX are zips too) is a
/// container whose entries are looked at.
/// </summary>
public static class BookFormats
{
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".fb2"] = "fb2",
        [".epub"] = "epub",
        [".kepub"] = "kepub",
        [".pdf"] = "pdf",
        [".mobi"] = "mobi",
        [".azw"] = "azw",
        [".azw3"] = "azw3",
        [".djvu"] = "djvu",
        [".djv"] = "djvu",
        [".cbz"] = "cbz",
        [".cbr"] = "cbr",
        [".txt"] = "txt",
        [".rtf"] = "rtf",
        [".doc"] = "doc",
        [".docx"] = "docx",
    };

    /// <summary>The format of a book file (<c>fb2</c>, <c>epub</c>…), <c>kepub</c> for <c>.kepub.epub</c>; null for anything else.</summary>
    public static string? Of(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot < name.LastIndexOfAny(['/', '\\']))
            return null;
        if (!Extensions.TryGetValue(name[dot..], out var format))
            return null;
        return format == "epub" && name.EndsWith(".kepub.epub", StringComparison.OrdinalIgnoreCase) ? "kepub" : format;
    }

    /// <summary>A ZIP container (or a one-book wrapper such as <c>book.fb2.zip</c>) to look inside.</summary>
    public static bool IsContainer(string name) => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
}
