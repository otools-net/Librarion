namespace Librarion.Api.EReader;

/// <summary>
/// The addresses the e-reader pages link to, from the app's root (the request's <c>PathBase</c>, a virtual directory
/// behind a proxy). Absolute paths, so a page links the same from any depth; stage 8 chunk 8 adds the key prefix
/// (<c>/e/k/&lt;key&gt;</c>) here, so every link keeps it.
/// </summary>
public sealed class EReaderLinks(HttpContext context)
{
    private readonly string _root = context.Request.PathBase.Value?.TrimEnd('/') + EReaderEndpoints.Prefix;

    /// <summary>The home page (<c>/e/</c>, with its slash: what people type).</summary>
    public string Home => _root + "/";

    public string Stylesheet => _root + EReaderEndpoints.StylesheetPath + "?v=" + EReaderStylesheet.Version;

    /// <summary>A library's page.</summary>
    public string Library(int library) => $"{_root}/l/{library}/";

    /// <summary>A library's books, newest first; a page after <paramref name="after"/> (a cursor) when given.</summary>
    public string Recent(int library, string? after = null) => $"{_root}/l/{library}/recent" + After(after);

    /// <summary>A library's books A to Z.</summary>
    public string Titles(int library, string? after = null) => $"{_root}/l/{library}/titles" + After(after);

    /// <summary>Where a library's search box sends its words (<c>q</c>); with them, its results (a page after <paramref name="after"/>).</summary>
    public string Search(int library, string? query = null, string? after = null) => $"{_root}/l/{library}/search"
        + (query is null ? "" : "?q=" + Uri.EscapeDataString(query) + (after is null ? "" : "&after=" + Uri.EscapeDataString(after)));

    /// <summary>A book's page.</summary>
    public string Book(int library, int book) => $"{_root}/l/{library}/books/{book}";

    /// <summary>A book's cover; <paramref name="version"/> changes with the book, so a cached picture isn't kept after an edit.</summary>
    public string Cover(int library, int book, string size, string? version) =>
        $"{_root}/l/{library}/books/{book}/cover/{size}" + (version is null ? "" : "?v=" + Uri.EscapeDataString(version));

    /// <summary>A field's values (authors, series…), A to Z.</summary>
    public string Field(int library, string key) => $"{_root}/l/{library}/fields/{Uri.EscapeDataString(key)}";

    /// <summary>The plain tags of a library.</summary>
    public string Tags(int library) => $"{_root}/l/{library}/tags";

    /// <summary>
    /// A field's values (<paramref name="key"/>), or the plain tags (null): their first letters, or a page of them from
    /// a letter (<paramref name="from"/>; empty: from the start) or after a cursor.
    /// </summary>
    public string Values(int library, string? key, string? from = null, string? after = null) =>
        (key is null ? Tags(library) : Field(library, key)) + Query(("from", from), ("after", after));

    /// <summary>A tag's page (a field's value or a plain tag): its books, or the tags under it.</summary>
    public string Tag(int library, int tag) => $"{_root}/l/{library}/tags/{tag}";

    /// <summary>A tag's books (with those of the tags under it).</summary>
    public string TagBooks(int library, int tag, string? after = null) => Tag(library, tag) + "/books" + After(after);

    private static string After(string? after) => after is null ? "" : "?after=" + Uri.EscapeDataString(after);

    private static string Query(params (string Name, string? Value)[] parameters)
    {
        var given = parameters.Where(p => p.Value is not null).Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value!)).ToList();
        return given.Count == 0 ? "" : "?" + string.Join("&", given);
    }

    /// <summary>Where the sign-out form posts.</summary>
    public string SignOut => _root + "/signout";

    /// <summary>The sign-in page, coming back to <paramref name="returnTo"/> (a path under <c>/e</c>) afterwards.</summary>
    public string SignIn(string? returnTo = null) =>
        _root + "/signin" + (string.IsNullOrEmpty(returnTo) ? "" : "?return=" + Uri.EscapeDataString(returnTo));
}
