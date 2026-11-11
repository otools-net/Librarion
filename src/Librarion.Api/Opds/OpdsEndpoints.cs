using Librarion.Api.Endpoints;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Covers;
using Librarion.Core.Reading;
using Microsoft.AspNetCore.Authorization;

namespace Librarion.Api.Opds;

/// <summary>
/// The OPDS 1.2 catalog for reader apps under <c>/opds</c> (agents/docs/features/opds.md): GET only, signed in by
/// <see cref="OpdsAuthenticationHandler"/> (never the cookie), each route naming its right; anything else under
/// <c>/opds</c> is 404, never the client's page. A library's feeds need <c>library.opds</c> and <c>library.browse</c>
/// there (else 404: a library one can't open is not told apart from none); files also <c>library.download</c>.
/// </summary>
public static partial class OpdsEndpoints
{
    public const string Prefix = "/opds";

    /// <summary>Entries in a page of books (D-7.6): next pages by key, never by offset.</summary>
    public const int PageSize = 50;

    public static void MapOpdsEndpoints(this IEndpointRouteBuilder app)
    {
        var opds = app.MapGroup(Prefix)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = OpdsAuthenticationHandler.SchemeName })
            .RequireRateLimiting(SecurityServiceCollectionExtensions.OpdsRateLimit)
            .ExcludeFromDescription();

        opds.MapGet("", Root).RequireRight(Rights.Opds);

        var library = opds.MapGroup("/l/{library:int}").RequireRight(Rights.Opds);
        library.MapGet("", LibraryRoot);
        library.MapGet("/recent", (int library, string? after, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookQueries books) =>
            BooksAsync(library, BookSort.Added, after, context, current, visibilities, books));
        library.MapGet("/titles", (int library, string? after, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookQueries books) =>
            BooksAsync(library, BookSort.Title, after, context, current, visibilities, books));
        library.MapGet("/opensearch.xml", OpenSearchAsync);
        library.MapGet("/search", SearchAsync);
        library.MapGet("/books/{id:int}/files/{fileId:int}", (int library, int id, int fileId, HttpContext context, CurrentAccess current,
            BookVisibilities visibilities, BookDownloads downloads) => DownloadAsync(library, id, fileId, zipped: false, context, current, visibilities, downloads));
        library.MapGet("/books/{id:int}/files/{fileId:int}/zip", (int library, int id, int fileId, HttpContext context, CurrentAccess current,
            BookVisibilities visibilities, BookDownloads downloads) => DownloadAsync(library, id, fileId, zipped: true, context, current, visibilities, downloads));
        library.MapGet("/books/{id:int}/cover/{size}", CoverAsync);
        MapBrowse(library);
        MapShelves(library);

        // Every other address, file-like ones too (opensearch.xml): not found, before the client's fallback takes it.
        opds.MapFallback("{**path}", () => TypedResults.NotFound()).AllowAnonymous();
    }

    /// <summary>The libraries the account may read by OPDS; with only one, its catalog itself (no extra step in the app).</summary>
    private static async Task<IResult> Root(HttpContext context, CurrentAccess current, AccessService accounts, TagQueries tags, Shelves shelves)
    {
        var state = await current.GetAsync();
        var links = new OpdsLinks(context);
        var libraries = state.TenantId is { } tenantId
            ? (await accounts.ListLibrariesAsync(tenantId, context.RequestAborted)).Where(l => Opens(state, l.Id)).ToList()
            : [];
        if (libraries.Count == 1)
            return LibraryFeed(libraries[0].Id, libraries[0].Name, links, links.Root, await tags.FieldsWithValuesAsync(state.TenantId!.Value, context.RequestAborted),
                await HasShelvesAsync(state, shelves, context.RequestAborted));

        // A list of catalogs: no search across them (Q11); each library's feeds carry its own.

        var feed = OpdsFeed.Start(OpdsFeed.NavigationType, "urn:librarion:root", "Librarion", links, links.Root);
        foreach (var each in libraries)
            feed.Navigation($"urn:librarion:library:{each.Id}", each.Name, links.Library(each.Id), OpdsFeed.NavigationType);
        return feed;
    }

    private static async Task<IResult> LibraryRoot(int library, HttpContext context, CurrentAccess current, AccessService accounts, TagQueries tags, Shelves shelves)
    {
        var state = await current.GetAsync();
        if (!Opens(state, library))
            return Results.NotFound();
        var name = (await accounts.ListLibrariesAsync(state.TenantId!.Value, context.RequestAborted)).FirstOrDefault(l => l.Id == library)?.Name;
        if (name is null)
            return Results.NotFound();
        var links = new OpdsLinks(context);
        return LibraryFeed(library, name, links, links.Library(library), await tags.FieldsWithValuesAsync(state.TenantId!.Value, context.RequestAborted),
            await HasShelvesAsync(state, shelves, context.RequestAborted));
    }

    /// <summary>
    /// A library's catalog: what it can be browsed by (recent, by title, the account's shelves when it has some, each
    /// field with values, the plain tags; its search in the feed's links).
    /// </summary>
    private static OpdsFeed LibraryFeed(int library, string name, OpdsLinks links, string self, IReadOnlyList<FieldInfo> fields, bool shelves)
    {
        var feed = OpdsFeed.Start(OpdsFeed.NavigationType, $"urn:librarion:library:{library}", name, links, self, library: library)
            .Navigation($"urn:librarion:library:{library}:recent", "Recently added", links.Recent(library), OpdsFeed.AcquisitionType,
                "The books added last, newest first.", OpdsFeed.NewRel)
            .Navigation($"urn:librarion:library:{library}:titles", "All books by title", links.Titles(library), OpdsFeed.AcquisitionType,
                "Every book, A to Z.");
        if (shelves)
            feed.Navigation($"urn:librarion:library:{library}:shelves", "My shelves", links.Shelves(library), OpdsFeed.NavigationType,
                "Your own lists of books, in your order.");
        foreach (var field in fields)
            feed.Navigation($"urn:librarion:library:{library}:field:{field.Key}", FieldName(field), links.Values(library, field.Key), OpdsFeed.NavigationType);
        return feed.Navigation($"urn:librarion:library:{library}:tags", "Tags", links.Values(library, null), OpdsFeed.NavigationType);
    }

    /// <summary>A page of a library's books in an order, with the link to the next page.</summary>
    private static async Task<IResult> BooksAsync(int library, BookSort sort, string? after, HttpContext context, CurrentAccess current,
        BookVisibilities visibilities, BookQueries books)
    {
        var state = await current.GetAsync();
        if (!Opens(state, library))
            return Results.NotFound();
        var tenantId = state.TenantId!.Value;
        var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
        var page = await books.ListAsync(tenantId, visibility, new BookPageRequest(library, sort, after, PageSize), context.RequestAborted);
        var extras = await books.EntriesAsync(tenantId, page.Items.Select(b => b.Id).ToList(), context.RequestAborted);

        var links = new OpdsLinks(context);
        var (id, title, self, next) = sort == BookSort.Added
            ? ($"urn:librarion:library:{library}:recent", "Recently added", links.Recent(library, after), page.Next is { } n ? links.Recent(library, n) : null)
            : ($"urn:librarion:library:{library}:titles", "All books by title", links.Titles(library, after), page.Next is { } m ? links.Titles(library, m) : null);
        var feed = OpdsFeed.Start(OpdsFeed.AcquisitionType, id, title, links, self, links.Library(library), next);
        var canDownload = state.Access.HasIn(Rights.Download, library);
        foreach (var book in page.Items)
            feed.Book(book, extras.GetValueOrDefault(book.Id), links, canDownload);
        return feed;
    }

    /// <summary>The library's OpenSearch description: where its search is, as a template.</summary>
    private static async Task<IResult> OpenSearchAsync(int library, HttpContext context, CurrentAccess current, AccessService accounts)
    {
        var state = await current.GetAsync();
        if (!Opens(state, library))
            return Results.NotFound();
        var name = (await accounts.ListLibrariesAsync(state.TenantId!.Value, context.RequestAborted)).FirstOrDefault(l => l.Id == library)?.Name;
        return name is null ? Results.NotFound() : OpdsFeed.OpenSearchDescription(name, new OpdsLinks(context), library);
    }

    /// <summary>
    /// A search in the library in the search language (as the app's search box: words, <c>author:</c>, <c>-</c>…), by
    /// title, a page at a time by key; a blank search gives an empty feed, not every book.
    /// </summary>
    private static async Task<IResult> SearchAsync(int library, string? q, string? after, HttpContext context, CurrentAccess current,
        BookVisibilities visibilities, BookQueries books)
    {
        var state = await current.GetAsync();
        if (!Opens(state, library))
            return Results.NotFound();
        var tenantId = state.TenantId!.Value;
        var links = new OpdsLinks(context);
        var query = q?.Trim() ?? "";
        var page = new Page<BookListItem>([], null);
        if (query.Length > 0)
        {
            var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
            page = await books.ListAsync(tenantId, visibility, new BookPageRequest(library, BookSort.Title, after, PageSize, Query: query), context.RequestAborted);
        }
        var extras = await books.EntriesAsync(tenantId, page.Items.Select(b => b.Id).ToList(), context.RequestAborted);

        var feed = OpdsFeed.Start(OpdsFeed.AcquisitionType, $"urn:librarion:library:{library}:search", $"Search: {query}", links,
            links.Search(library, query, after), links.Library(library), page.Next is { } next ? links.Search(library, query, next) : null);
        var canDownload = state.Access.HasIn(Rights.Download, library);
        foreach (var book in page.Items)
            feed.Book(book, extras.GetValueOrDefault(book.Id), links, canDownload);
        return feed;
    }

    /// <summary>
    /// A file as it lies (Range, ETag), or an FB2 zipped while it is sent (<paramref name="zipped"/>; other formats
    /// are not offered so: 404). A merged book's old address still gives the file (it is its kept book's now).
    /// </summary>
    private static async Task<IResult> DownloadAsync(int library, int id, int fileId, bool zipped, HttpContext context, CurrentAccess current,
        BookVisibilities visibilities, BookDownloads downloads)
    {
        var state = await current.GetAsync();
        if (!Opens(state, library))
            return Results.NotFound();
        var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
        if (await downloads.FindAsync(state.TenantId!.Value, visibility, id, fileId, context.RequestAborted) is not { } target || target.LibraryId != library
            || (zipped && !OpdsFeed.Zipped(target.Format)))
            return Results.NotFound();
        if (!state.Access.HasIn(Rights.Download, library))
            return Results.Forbid();
        if (!zipped)
            return BookEndpoints.Download(context, target);
        var (content, problem) = BookDownloads.Open(target);
        return content is null ? BookEndpoints.FileProblem(problem) : new ZippedDownload(content, target.Name, target.Modified);
    }

    private static async Task<IResult> CoverAsync(int library, int id, string size, HttpContext context, CurrentAccess current, BookVisibilities visibilities,
        CoverService covers)
    {
        var state = await current.GetAsync();
        if (!Opens(state, library) || !Enum.TryParse<CoverSize>(size, ignoreCase: true, out var coverSize) || !Enum.IsDefined(coverSize))
            return Results.NotFound();
        var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
        return await BookEndpoints.CoverAsync(context, covers, state.TenantId!.Value, visibility, id, coverSize);
    }

    /// <summary>The account may read the library by OPDS (an API key's libraries and scopes already narrowed the rights).</summary>
    private static bool Opens(AccessState state, int library) =>
        state.TenantId is not null && state.Access.HasIn(Rights.Opds, library) && state.Access.HasIn(Rights.Browse, library);
}
