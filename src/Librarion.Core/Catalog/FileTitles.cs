namespace Librarion.Core.Catalog;

/// <summary>
/// The title of a book whose metadata gives none: its file's name, book extensions removed, <c>_</c> and <c>.</c> as
/// spaces (agents/docs/features/book-display.md#title).
/// </summary>
public static class FileTitles
{
    private static readonly string[] Extensions =
        [".zip", ".7z", ".rar", ".fb2", ".epub", ".kepub", ".pdf", ".mobi", ".azw3", ".azw", ".djvu", ".djv", ".cbz", ".cbr", ".txt", ".rtf", ".doc", ".docx"];

    public static string FromPath(string path)
    {
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        // Double extensions too: book.fb2.zip.
        for (var stripped = true; stripped;)
        {
            stripped = false;
            foreach (var extension in Extensions)
            {
                if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^extension.Length];
                    stripped = true;
                    break;
                }
            }
        }

        return string.Join(' ', name.Replace('_', ' ').Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
