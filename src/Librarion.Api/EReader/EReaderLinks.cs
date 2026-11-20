using Librarion.Api.Security;

namespace Librarion.Api.EReader;

/// <summary>
/// The addresses the e-reader pages link to, from the app's root (the request's <c>PathBase</c>, a virtual directory
/// behind a proxy). Absolute paths, so a page links the same from any depth; with a key in the address
/// (<c>/e/k/&lt;key&gt;</c>, D-8.7: <see cref="AddressKey"/>) every link keeps it, covers and downloads too.
/// </summary>
/// <param name="keepKey">False: the links of the mode without the address's key (where a refused key leads).</param>
public sealed class EReaderLinks(HttpContext context, bool keepKey = true)
{
    private readonly string _root = context.Request.PathBase.Value?.TrimEnd('/')
        + (keepKey ? AddressKey.Root(context, EReaderEndpoints.Prefix) : EReaderEndpoints.Prefix);

    /// <summary>The request signed in with a key in its address (<c>/e/k/&lt;key&gt;</c>): no sign-in or sign-out there.</summary>
    public bool ByKey { get; } = keepKey && AddressKey.Of(context) is not null;

    /// <summary>
    /// Where a return address (a path of the mode, <c>/e/…</c>, never with the key: <see cref="EReaderEndpoints.SafeReturn"/>)
    /// leads from this request: under the key's prefix when the address had one.
    /// </summary>
    public string Back(string returnTo) => _root + returnTo[EReaderEndpoints.Prefix.Length..];

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

    /// <summary>A book's page by its id alone (where the app's book address leads an e-ink browser): on to its library's.</summary>
    public string AnyBook(int book) => $"{_root}/books/{book}";

    /// <summary>The app instead of these pages, for this browser (<see cref="EReaderRedirect"/>): every page links it.</summary>
    public string FullSite => _root + "/full-site";

    /// <summary>These pages again after the full site was chosen: the app's link to them.</summary>
    public string Start => _root + "/start";

    /// <summary>A book's page.</summary>
    public string Book(int library, int book) => $"{_root}/l/{library}/books/{book}";

    /// <summary>A book's file to download, as it lies.</summary>
    public string File(int library, int book, int file) => $"{Book(library, book)}/files/{file}";

    /// <summary>An FB2 file zipped while it is sent (<c>.fb2.zip</c>).</summary>
    public string ZippedFile(int library, int book, int file) => File(library, book, file) + "/zip";

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

    /// <summary>The device chooser (whose formats come first), coming back to <paramref name="returnTo"/> (a path under <c>/e</c>); also where its form posts.</summary>
    public string Device(string? returnTo = null) =>
        _root + "/device" + (string.IsNullOrEmpty(returnTo) ? "" : "?return=" + Uri.EscapeDataString(returnTo));

    /// <summary>Where the sign-out form posts.</summary>
    public string SignOut => _root + "/signout";

    /// <summary>The sign-in page, coming back to <paramref name="returnTo"/> (a path under <c>/e</c>) afterwards.</summary>
    public string SignIn(string? returnTo = null) =>
        _root + "/signin" + (string.IsNullOrEmpty(returnTo) ? "" : "?return=" + Uri.EscapeDataString(returnTo));
}
