using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Catalog;

namespace Librarion.Api.Endpoints;

/// <summary>
/// The catalog: books and field values, by key, only what the caller may browse (agents/docs/development/scale.md,
/// agents/docs/features/rights.md). A book the caller can't see is not found, never told apart from a missing one.
/// </summary>
public static class BookEndpoints
{
    public static void MapBookEndpoints(this IEndpointRouteBuilder api)
    {
        var books = api.MapGroup("/books").WithTags("Books").RequireRight(Rights.Browse);

        books.MapGet("/", async (int? library, string? sort, string? after, int? limit, int? tag, HttpContext context, CurrentAccess current,
                BookVisibilities visibilities, BookQueries queries) =>
            {
                var state = await current.GetAsync();
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                return TypedResults.Ok(await queries.ListAsync(state.TenantId!.Value, visibility,
                    new BookPageRequest(library, Enum.TryParse<BookSort>(sort, ignoreCase: true, out var order) ? order : BookSort.Title, after, limit ?? 50, tag),
                    context.RequestAborted));
            })
            .WithName("ListBooks")
            .WithSummary("Books the caller may browse, a page at a time by key.")
            .WithDescription("`sort` = `title` (default), `author` or `added` (newest first); `after` = the `next` of the page before; `tag` = only books with this tag or a descendant (a field value's page); `limit` up to 200.");

        books.MapGet("/{id:int}", async (int id, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookQueries queries) =>
            {
                var state = await current.GetAsync();
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                return await queries.GetAsync(state.TenantId!.Value, visibility, id, context.RequestAborted) is { } book
                    ? Results.Ok(book)
                    : Results.NotFound();
            })
            .WithName("GetBook")
            .WithSummary("One book: its tags by field, files and identifiers; `mergedIntoId` when it now lives in another book.");

        api.MapGet("/fields/{key}/values", async (string key, int? library, string? after, int? limit, HttpContext context, CurrentAccess current, TagQueries queries) =>
                await queries.ValuesAsync((await current.GetAsync()).TenantId!.Value, key, library, after, limit ?? 100, context.RequestAborted) is { } page
                    ? Results.Ok(page)
                    : Results.NotFound())
            .RequireRight(Rights.Browse)
            .WithTags("Books")
            .WithName("ListFieldValues")
            .WithSummary("The values of a field (authors, series, categories…) by sort name, a page at a time by key, with book counts of `library`.");
    }
}
