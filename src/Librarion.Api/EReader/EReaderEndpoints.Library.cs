using Librarion.Api.EReader.Pages;
using Librarion.Api.Endpoints;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Covers;
using Librarion.Core.Search;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Librarion.Api.EReader;

/// <summary>
/// The home page, a library's page and its lists of books (stage 8 chunk 3): the same queries as the app and the OPDS
/// feeds (<see cref="BookQueries"/>, <see cref="TagQueries"/>), pages by key (<see cref="PageSize"/>, Next and First:
/// D-8.6), the account's visibility (content filters) as everywhere.
/// </summary>
public static partial class EReaderEndpoints
{
    /// <summary>Books on a page (Q7: 20 with small covers).</summary>
    public const int PageSize = 20;

    /// <summary>Books of "Recently added" on a library's page; the rest a tap away.</summary>
    public const int RecentOnLibraryPage = 5;

    private static void MapLibrary(RouteGroupBuilder e)
    {
        e.MapGet("", HomeAsync).RequireRight(Rights.Browse);

        var library = e.MapGroup("/l/{library:int}").RequireRight(Rights.Browse);
        library.MapGet("", LibraryAsync);
        library.MapGet("/recent", (int library, string? after, HttpContext context, CurrentAccess current, AccessService accounts, BookVisibilities visibilities,
            BookQueries books, ServerTexts texts) => BooksAsync(library, BookSort.Added, after, context, current, accounts, visibilities, books, texts));
        library.MapGet("/titles", (int library, string? after, HttpContext context, CurrentAccess current, AccessService accounts, BookVisibilities visibilities,
            BookQueries books, ServerTexts texts) => BooksAsync(library, BookSort.Title, after, context, current, accounts, visibilities, books, texts));
        library.MapGet("/search", SearchAsync);
        MapBrowse(library);
        library.MapGet("/books/{id:int}/cover/{size}", CoverAsync);
    }

    /// <summary>
    /// The home page: with one library to browse, that library's page itself (one refresh less on e-ink, as the OPDS
    /// root); with several, the list of them.
    /// </summary>
    private static async Task<IResult> HomeAsync(HttpContext context, CurrentAccess current, AccessService accounts, BookVisibilities visibilities,
        BookQueries books, TagQueries tags)
    {
        var state = await current.GetAsync();
        var libraries = state.TenantId is { } tenantId
            ? (await accounts.ListLibrariesAsync(tenantId, context.RequestAborted)).Where(l => Opens(state, l.Id)).ToList()
            : [];
        if (libraries.Count == 1)
            return await LibraryPageAsync(libraries[0].Id, libraries[0].Name, state, context, visibilities, books, tags);
        return new RazorComponentResult<HomePage>(new
        {
            Frame = await EReaderFrame.ForAsync(context),
            Libraries = (IReadOnlyList<HomePage.LibraryLink>)libraries.Select(l => new HomePage.LibraryLink(l.Name, new EReaderLinks(context).Library(l.Id))).ToList(),
        });
    }

    private static async Task<IResult> LibraryAsync(int library, HttpContext context, CurrentAccess current, AccessService accounts, BookVisibilities visibilities,
        BookQueries books, TagQueries tags)
    {
        var state = await current.GetAsync();
        if (await LibraryNameAsync(state, library, accounts, context.RequestAborted) is not { } name)
            return Results.NotFound();
        return await LibraryPageAsync(library, name, state, context, visibilities, books, tags);
    }

    /// <summary>A library's page: its search box, the books added last, and what it can be browsed by.</summary>
    private static async Task<IResult> LibraryPageAsync(int library, string name, AccessState state, HttpContext context, BookVisibilities visibilities,
        BookQueries books, TagQueries tags)
    {
        var tenantId = state.TenantId!.Value;
        var visibility = await visibilities.GetAsync(new AccessSubject(tenantId, state.User!.Id), context.RequestAborted);
        var recent = await books.ListAsync(tenantId, visibility, new BookPageRequest(library, BookSort.Added, null, RecentOnLibraryPage), context.RequestAborted);
        var fields = await tags.FieldsWithValuesAsync(tenantId, context.RequestAborted);
        return new RazorComponentResult<LibraryPage>(new
        {
            Frame = await EReaderFrame.ForAsync(context),
            LibraryId = library,
            Name = name,
            Recent = recent.Items,
            MoreRecent = recent.Next is not null,
            Fields = (IReadOnlyList<FieldInfo>)fields,
        });
    }

    /// <summary>A page of a library's books in an order, with Next (by key) and First.</summary>
    private static async Task<IResult> BooksAsync(int library, BookSort sort, string? after, HttpContext context, CurrentAccess current, AccessService accounts,
        BookVisibilities visibilities, BookQueries books, ServerTexts texts)
    {
        var state = await current.GetAsync();
        if (await LibraryNameAsync(state, library, accounts, context.RequestAborted) is not { } name)
            return Results.NotFound();
        var tenantId = state.TenantId!.Value;
        var visibility = await visibilities.GetAsync(new AccessSubject(tenantId, state.User!.Id), context.RequestAborted);
        // A cursor that isn't one of ours (typed or cut short) starts at the beginning (KeysetCursor.Decode).
        var page = await books.ListAsync(tenantId, visibility, new BookPageRequest(library, sort, after, PageSize), context.RequestAborted);

        var links = new EReaderLinks(context);
        var (title, next, first) = sort == BookSort.Added
            ? (texts["ereader.library.recent"], page.Next is { } n ? links.Recent(library, n) : null, links.Recent(library))
            : (texts["ereader.library.titles"], page.Next is { } m ? links.Titles(library, m) : null, links.Titles(library));
        return new RazorComponentResult<BooksPage>(new
        {
            Frame = await EReaderFrame.ForAsync(context),
            Title = title,
            Up = new EPage.Crumb(name, links.Library(library)),
            Books = page.Items,
            Next = next,
            First = after is null ? null : first,
        });
    }

    /// <summary>
    /// A search in the library in the search language (as the app's box and the OPDS search: words, "phrases",
    /// <c>author:</c>, <c>-</c>…), by title, a page at a time by key (stage 8 chunk 4). Nothing to search for (blank, or
    /// only signs) lists nothing and says how to search; a search cut short (too long, too many terms) says so.
    /// </summary>
    private static async Task<IResult> SearchAsync(int library, string? q, string? after, HttpContext context, CurrentAccess current, AccessService accounts,
        BookVisibilities visibilities, BookQueries books)
    {
        var state = await current.GetAsync();
        if (await LibraryNameAsync(state, library, accounts, context.RequestAborted) is not { } name)
            return Results.NotFound();
        var query = q?.Trim() ?? "";
        // Read without the tenant's fields: whether there is anything to search for and whether it was cut doesn't
        // depend on them (a key that is no field is still one term, of text).
        var parsed = SearchQuery.Parse(query, _ => false);
        var page = new Page<BookListItem>([], null);
        if (!parsed.IsEmpty)
        {
            var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
            page = await books.ListAsync(state.TenantId!.Value, visibility, new BookPageRequest(library, BookSort.Title, after, PageSize, Query: query),
                context.RequestAborted);
        }

        var links = new EReaderLinks(context);
        return new RazorComponentResult<SearchPage>(new
        {
            Frame = await EReaderFrame.ForAsync(context),
            Up = new EPage.Crumb(name, links.Library(library)),
            LibraryId = library,
            Query = query,
            Searched = !parsed.IsEmpty,
            Cut = parsed.Cut,
            Books = page.Items,
            Next = page.Next is { } next ? links.Search(library, query, next) : null,
            First = after is null || parsed.IsEmpty ? null : links.Search(library, query),
        });
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

    /// <summary>The library's name when the account may browse it; null otherwise (not told apart from no library: 404).</summary>
    private static async Task<string?> LibraryNameAsync(AccessState state, int library, AccessService accounts, CancellationToken cancellationToken) =>
        Opens(state, library) ? (await accounts.ListLibrariesAsync(state.TenantId!.Value, cancellationToken)).FirstOrDefault(l => l.Id == library)?.Name : null;

    /// <summary>The account may browse the library (in its tenant).</summary>
    private static bool Opens(AccessState state, int library) => state is { TenantId: not null, User: not null } && state.Access.HasIn(Rights.Browse, library);
}
