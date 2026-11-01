namespace Librarion.Core.Catalog;

/// <summary>Sort keys of titles (agents/docs/features/book-display.md#title).</summary>
public static class BookTitles
{
    private static readonly string[] Articles = ["the ", "a ", "an "];

    /// <summary>Lower case, trimmed, a leading English article dropped, cut to <see cref="Data.Book.MaxSortLength"/>.</summary>
    public static string SortKey(string title)
    {
        var key = title.Trim().ToLowerInvariant();
        foreach (var article in Articles)
        {
            if (key.Length > article.Length && key.StartsWith(article, StringComparison.Ordinal))
            {
                key = key[article.Length..].TrimStart();
                break;
            }
        }

        return key.Length > Data.Book.MaxSortLength ? key[..Data.Book.MaxSortLength] : key;
    }
}
