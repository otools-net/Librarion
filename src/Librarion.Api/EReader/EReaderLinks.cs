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

    /// <summary>The sign-in page, coming back to <paramref name="returnTo"/> (a path under <c>/e</c>) afterwards.</summary>
    public string SignIn(string? returnTo = null) =>
        _root + "/signin" + (string.IsNullOrEmpty(returnTo) ? "" : "?return=" + Uri.EscapeDataString(returnTo));
}
