using Librarion.Api.EReader.Pages;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Librarion.Api.EReader;

/// <summary>
/// The e-reader mode under <c>/e</c> (agents/docs/features/ereader-mode.md): a plain, grayscale face of the library for
/// the browsers of e-ink devices, rendered on the server by Razor components (D-8.1) with no script at all (its own
/// CSP, <see cref="SecurityHeaders.EReaderPolicy"/>). The app's cookie session; every route names its right; anything
/// else under <c>/e</c> is a plain 404 page (<see cref="EReaderGuard"/>), never the client's.
/// </summary>
public static class EReaderEndpoints
{
    public const string Prefix = "/e";

    /// <summary>The stylesheet's address under <see cref="Prefix"/> (<see cref="EReaderStylesheet"/>).</summary>
    public const string StylesheetPath = "/ereader.css";

    public static void MapEReaderEndpoints(this IEndpointRouteBuilder app)
    {
        var e = app.MapGroup(Prefix).ExcludeFromDescription();

        e.MapGet("", HomeAsync).RequireRight(Rights.Browse);
        e.MapGet(StylesheetPath, EReaderStylesheet.Serve).AllowAnonymous();

        // Every other address, file-like ones too: not found (EReaderGuard renders the page), before the client's fallback takes it.
        e.MapFallback("{**path}", () => TypedResults.NotFound()).AllowAnonymous();
    }

    /// <summary>The home page: for now the libraries one may browse (search, recently added and browsing: chunk 3).</summary>
    private static async Task<RazorComponentResult<HomePage>> HomeAsync(HttpContext context, CurrentAccess current, AccessService accounts)
    {
        var state = await current.GetAsync();
        var libraries = state.TenantId is { } tenantId
            ? (await accounts.ListLibrariesAsync(tenantId, context.RequestAborted)).Where(l => state.Access.HasIn(Rights.Browse, l.Id)).Select(l => l.Name).ToList()
            : [];
        return new RazorComponentResult<HomePage>(new { Links = new EReaderLinks(context), Libraries = (IReadOnlyList<string>)libraries });
    }

    /// <summary>The request is for an e-reader page (a visitor without a session goes to its sign-in page, not a 401).</summary>
    public static bool IsEReader(HttpRequest request) => request.Path.StartsWithSegments(Prefix);
}
