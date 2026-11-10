using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Tagging;

namespace Librarion.Api.Endpoints;

/// <summary>
/// One's own shelves (agents/docs/features/shelves.md, <see cref="Rights.Shelves"/>), in the tenant one works in:
/// another account's shelf is not found. Books are put on and taken off one or a selection at a time; a shelf's
/// books come by key, only those one may see now.
/// </summary>
public static class ShelfEndpoints
{
    public sealed record ShelfInput(string? Name);

    public sealed record ShelfBooksInput(IReadOnlyList<int>? BookIds);

    /// <param name="Before">The book to go just before; null: to the end.</param>
    public sealed record ShelfMoveInput(int? Before);

    public static void MapShelfEndpoints(this IEndpointRouteBuilder api)
    {
        var shelves = api.MapGroup("/shelves").WithTags("Shelves").RequireRight(Rights.Shelves);

        shelves.MapGet("/", async (HttpContext context, CurrentAccess current, Shelves store) =>
                await current.GetAsync() is { TenantId: { } tenantId } state
                    ? Results.Ok(await store.ListAsync(tenantId, state.User!.Id, context.RequestAborted))
                    : Results.Ok(Array.Empty<ShelfInfo>()))
            .WithName("ListShelves")
            .WithSummary($"The signed-in account's shelves in its tenant, by name, with their counts of books (at most {Shelves.MaxShelves}).");

        shelves.MapPost("/", async (ShelfInput input, HttpContext context, CurrentAccess current, Shelves store) =>
                await current.GetAsync() is { TenantId: { } tenantId } state
                    ? ApiResults.From(await store.CreateAsync(tenantId, state.User!.Id, input.Name, context.RequestAborted))
                    : Results.NotFound())
            .WithName("CreateShelf")
            .WithSummary("Makes a shelf with a name of its own among the account's shelves.");

        shelves.MapGet("/{id:int}", async (int id, HttpContext context, CurrentAccess current, Shelves store) =>
                await current.GetAsync() is { TenantId: { } tenantId } state && await store.GetAsync(tenantId, state.User!.Id, id, context.RequestAborted) is { } shelf
                    ? Results.Ok(shelf)
                    : Results.NotFound())
            .WithName("GetShelf")
            .WithSummary("One of the account's shelves.");

        shelves.MapPut("/{id:int}", async (int id, ShelfInput input, HttpContext context, CurrentAccess current, Shelves store) =>
                await current.GetAsync() is { TenantId: { } tenantId } state
                    ? ApiResults.From(await store.RenameAsync(tenantId, state.User!.Id, id, input.Name, context.RequestAborted))
                    : Results.NotFound())
            .WithName("RenameShelf")
            .WithSummary("Renames one of the account's shelves.");

        shelves.MapDelete("/{id:int}", async (int id, HttpContext context, CurrentAccess current, Shelves store) =>
                await current.GetAsync() is { TenantId: { } tenantId } state && await store.DeleteAsync(tenantId, state.User!.Id, id, context.RequestAborted)
                    ? Results.NoContent()
                    : Results.NotFound())
            .WithName("DeleteShelf")
            .WithSummary("Deletes one of the account's shelves (never its books).");

        shelves.MapGet("/{id:int}/books", async (int id, string? sort, string? after, int? limit, int? library, HttpContext context, CurrentAccess current,
                BookVisibilities visibilities, Shelves store) =>
            {
                if (await current.GetAsync() is not { TenantId: { } tenantId } state)
                    return Results.NotFound();
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                var order = Enum.TryParse<ShelfSort>(sort, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : ShelfSort.Shelf;
                return await store.BooksAsync(tenantId, state.User.Id, visibility, id, order, after, limit ?? 50, context.RequestAborted, library) is { } page
                    ? Results.Ok(page)
                    : Results.NotFound();
            })
            .WithName("ListShelfBooks")
            .WithSummary("A page of a shelf's books the account may see now, by key.")
            .WithDescription("`sort` = `shelf` (the shelf's own order, default), `title`, `author` or `added` (put on the shelf last, first); `after` = the `next` of the page before; `library` = only that library's books; `limit` up to 200.");

        shelves.MapPost("/{id:int}/books", async (int id, ShelfBooksInput input, HttpContext context, CurrentAccess current, BookVisibilities visibilities, Shelves store) =>
            {
                if (await current.GetAsync() is not { TenantId: { } tenantId } state)
                    return Results.NotFound();
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                return ApiResults.From(await store.AddAsync(tenantId, state.User.Id, visibility, id, input.BookIds ?? [], context.RequestAborted));
            })
            .WithName("AddShelfBooks")
            .WithSummary($"Puts books (at most {BookTagger.MaxSelection} at once) on a shelf, after its last; those it has already stay where they are, those the account can't see are skipped (404 when none is seen). A shelf holds at most {Shelves.MaxBooks}.");

        shelves.MapPost("/{id:int}/books/remove", async (int id, ShelfBooksInput input, HttpContext context, CurrentAccess current, Shelves store) =>
                await current.GetAsync() is { TenantId: { } tenantId } state
                    ? ApiResults.From(await store.RemoveAsync(tenantId, state.User!.Id, id, input.BookIds ?? [], context.RequestAborted))
                    : Results.NotFound())
            .WithName("RemoveShelfBooks")
            .WithSummary($"Takes books (at most {BookTagger.MaxSelection} at once) off a shelf.");

        shelves.MapPut("/{id:int}/books/{bookId:int}/position", async (int id, int bookId, ShelfMoveInput input, HttpContext context, CurrentAccess current, Shelves store) =>
                await current.GetAsync() is { TenantId: { } tenantId } state
                    ? ApiResults.From(await store.MoveAsync(tenantId, state.User!.Id, id, bookId, input.Before, context.RequestAborted), _ => Results.NoContent())
                    : Results.NotFound())
            .WithName("MoveShelfBook")
            .WithSummary("Moves a book of a shelf to just before another of its books (`before`), or to the end (none).");

        api.MapGet("/books/{id:int}/shelves", async (int id, HttpContext context, CurrentAccess current, Shelves store) =>
                await current.GetAsync() is { TenantId: { } tenantId } state
                    ? Results.Ok(await store.ShelvesOfAsync(tenantId, state.User!.Id, id, context.RequestAborted))
                    : Results.Ok(Array.Empty<int>()))
            .WithTags("Shelves")
            .RequireRight(Rights.Shelves)
            .WithName("ListBookShelves")
            .WithSummary("The ids of the account's shelves holding the book.");
    }
}
