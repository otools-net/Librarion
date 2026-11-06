using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Catalog;

namespace Librarion.Api.Endpoints;

/// <summary>
/// The folder view (agents/docs/features/folders.md): a library's roots, a folder and its folders, with
/// <see cref="Rights.Folders"/> in the folder's library. A folder elsewhere is not found, never told apart from none.
/// The books of a folder are <c>GET api/books?library&amp;folder</c>.
/// </summary>
public static class FolderEndpoints
{
    public static void MapFolderEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/libraries/{id:int}/folders", async (int id, HttpContext context, CurrentAccess current, FolderQueries folders) =>
            {
                var state = await current.GetAsync();
                return state.Access.HasIn(Rights.Folders, id)
                    ? Results.Ok(await folders.RootsAsync(state.TenantId!.Value, id, context.RequestAborted))
                    : Results.NotFound();
            })
            .RequireRight(Rights.Folders)
            .WithTags("Folders")
            .WithName("ListLibraryFolders")
            .WithSummary("The library's roots that hold books, as folders: the top of its folder view.");

        var group = api.MapGroup("/folders").WithTags("Folders").RequireRight(Rights.Folders);

        group.MapGet("/{id:int}", async (int id, HttpContext context, CurrentAccess current, FolderQueries folders) =>
            {
                var state = await current.GetAsync();
                return await folders.GetAsync(state.TenantId!.Value, id, context.RequestAborted) is { } folder && state.Access.HasIn(Rights.Folders, folder.LibraryId)
                    ? Results.Ok(folder)
                    : Results.NotFound();
            })
            .WithName("GetFolder")
            .WithSummary("A folder (a directory, a ZIP, a directory inside one) with its counts and the folders above it.");

        group.MapGet("/{id:int}/folders", async (int id, string? after, int? limit, HttpContext context, CurrentAccess current, FolderQueries folders) =>
            {
                var state = await current.GetAsync();
                if (await folders.GetAsync(state.TenantId!.Value, id, context.RequestAborted) is not { } folder || !state.Access.HasIn(Rights.Folders, folder.LibraryId))
                    return Results.NotFound();
                return Results.Ok(await folders.ChildrenAsync(state.TenantId.Value, id, after, limit ?? 50, context.RequestAborted));
            })
            .WithName("ListFolders")
            .WithSummary("A folder's folders by name, a page at a time by key (`after` = the `next` of the page before, `limit` up to 200).");
    }
}
