using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Catalog;

namespace Librarion.Api.Opds;

/// <summary>
/// The account's shelves in a library's catalog (stage 7 chunk 11, agents/docs/features/shelves.md): the list of its
/// shelves, then each shelf's books of that library in the shelf's own order, by key. Shelves hold books of every
/// library, but a catalog is one library's (Q11): each library shows its own books of them. Needs
/// <c>account.shelves</c> besides the library's rights.
/// </summary>
public static partial class OpdsEndpoints
{
    private static void MapShelves(RouteGroupBuilder library)
    {
        library.MapGet("/shelves", ShelvesAsync).RequireRight(Rights.Shelves);
        library.MapGet("/shelves/{shelf:int}", ShelfAsync).RequireRight(Rights.Shelves);
    }

    /// <summary>The library's catalog lists My shelves only to an account that may keep shelves and has one (a short list, one query).</summary>
    private static async Task<bool> HasShelvesAsync(AccessState state, Shelves shelves, CancellationToken cancellationToken) =>
        state is { TenantId: { } tenantId, User: { } user } && state.Access.Has(Rights.Shelves)
        && (await shelves.ListAsync(tenantId, user.Id, cancellationToken)).Count > 0;

    /// <summary>
    /// The account's shelves by name (at most the setting <c>shelves.maxPerAccount</c>: never paged). No counts: a
    /// shelf's count is of every library's books, the feed shows this library's.
    /// </summary>
    private static async Task<IResult> ShelvesAsync(int library, HttpContext context, CurrentAccess current, Shelves shelves)
    {
        var state = await current.GetAsync();
        if (!Opens(state, library))
            return Results.NotFound();
        var links = new OpdsLinks(context);
        var feed = OpdsFeed.Start(OpdsFeed.NavigationType, $"urn:librarion:library:{library}:shelves", "My shelves", links, links.Shelves(library),
            links.Library(library));
        foreach (var shelf in await shelves.ListAsync(state.TenantId!.Value, state.User!.Id, context.RequestAborted))
            feed.Navigation($"urn:librarion:library:{library}:shelf:{shelf.Id}", shelf.Name, links.Shelf(library, shelf.Id), OpdsFeed.AcquisitionType);
        return feed;
    }

    /// <summary>A shelf's books of the library that the account may see now, in the shelf's own order, by key; another's shelf 404.</summary>
    private static async Task<IResult> ShelfAsync(int library, int shelf, string? after, HttpContext context, CurrentAccess current,
        BookVisibilities visibilities, Shelves shelves, BookQueries books)
    {
        var state = await current.GetAsync();
        if (!Opens(state, library))
            return Results.NotFound();
        var tenantId = state.TenantId!.Value;
        var userId = state.User!.Id;
        if (await shelves.GetAsync(tenantId, userId, shelf, context.RequestAborted) is not { } info)
            return Results.NotFound();
        var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, userId), context.RequestAborted);
        if (await shelves.BooksAsync(tenantId, userId, visibility, shelf, ShelfSort.Shelf, after, PageSize, context.RequestAborted, library) is not { } page)
            return Results.NotFound();
        var extras = await books.EntriesAsync(tenantId, page.Items.Select(b => b.Id).ToList(), context.RequestAborted);

        var links = new OpdsLinks(context);
        var feed = OpdsFeed.Start(OpdsFeed.AcquisitionType, $"urn:librarion:library:{library}:shelf:{shelf}", info.Name, links,
            links.Shelf(library, shelf, after), links.Shelves(library), page.Next is { } next ? links.Shelf(library, shelf, next) : null);
        var canDownload = state.Access.HasIn(Rights.Download, library);
        foreach (var book in page.Items)
            feed.Book(book, extras.GetValueOrDefault(book.Id), links, canDownload);
        return feed;
    }
}
