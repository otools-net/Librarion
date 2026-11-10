using System.Globalization;

namespace Librarion.Api.Opds;

/// <summary>
/// Every address a feed links to, from one place (agents/docs/features/opds.md): under the app's root (a proxy's folder
/// too), so a path key's prefix (chunk 8) is added here and every link keeps it.
/// </summary>
public sealed class OpdsLinks(HttpContext context)
{
    /// <summary>The catalog's root as this request reached it.</summary>
    public string Root { get; } = context.Request.PathBase + OpdsEndpoints.Prefix;

    public string Library(int library) => $"{Root}/l/{library}";

    public string Recent(int library, string? after = null) => Library(library) + "/recent" + After(after);

    public string Titles(int library, string? after = null) => Library(library) + "/titles" + After(after);

    public string File(int library, int book, int file) => $"{Library(library)}/books/{book}/files/{file}";

    /// <summary>A cover; <paramref name="version"/> changes with the book, so a reader app's cached picture isn't shown after an edit.</summary>
    public string Cover(int library, int book, string size, string? version) =>
        $"{Library(library)}/books/{book}/cover/{size}" + (version is null ? "" : "?v=" + Uri.EscapeDataString(version));

    private static string After(string? after) => after is null ? "" : "?after=" + Uri.EscapeDataString(after);

    /// <summary>The library a feed's address names (<c>/opds/l/&lt;id&gt;/…</c>), for the request's tenant; null for others.</summary>
    public static int? LibraryOf(HttpContext context) =>
        context.Request.Path.StartsWithSegments(OpdsEndpoints.Prefix)
        && context.GetRouteValue("library") is string text
        && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
}
