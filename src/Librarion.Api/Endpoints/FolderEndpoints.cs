using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Tagging;

namespace Librarion.Api.Endpoints;

/// <summary>
/// The folder view (agents/docs/features/folders.md): a library's roots, a folder and its folders, with
/// <see cref="Rights.Folders"/> in the folder's library. A folder elsewhere is not found, never told apart from none.
/// The books of a folder are <c>GET api/books?library&amp;folder</c>.
/// </summary>
public static class FolderEndpoints
{
    /// <param name="Folder">Null for the library's own default tags.</param>
    /// <param name="Existing">The books already there get it too.</param>
    public sealed record DefaultTagInput(int? Folder, BookTagEdit Tag, bool Existing = false);

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

        // Default tags (stage 6 chunk 8c): of the library (no folder) or of a folder, read with the folder view's right or
        // the default tags' own, changed with the latter.
        api.MapGet("/libraries/{id:int}/default-tags", async (int id, int? folder, HttpContext context, CurrentAccess current, DefaultTags defaults) =>
            {
                var state = await current.GetAsync();
                if (!state.Access.HasIn(Rights.Folders, id) && !state.Access.HasIn(Rights.DefaultTags, id))
                    return Results.NotFound();
                return ApiResults.From(await defaults.ListAsync(state.TenantId!.Value, id, folder, context.RequestAborted));
            })
            .RequireRight(Rights.Folders, Rights.DefaultTags)
            .WithTags("Folders")
            .WithName("ListDefaultTags")
            .WithSummary("The default tags of a library (no `folder`) or of a folder: its own, then those it inherits from the folders above it and the library, nearest first.");

        api.MapPost("/libraries/{id:int}/default-tags", async (int id, DefaultTagInput input, HttpContext context, CurrentAccess current, DefaultTags defaults) =>
            {
                var state = await current.GetAsync();
                if (!state.Access.HasIn(Rights.DefaultTags, id))
                    return Results.NotFound();
                var result = await defaults.AddAsync(state.TenantId!.Value, id, input.Folder, input.Tag, input.Existing, state.User!.Id, context.RequestAborted);
                return ApiResults.From(result, work => work.RunId is null ? Results.Ok(work) : Results.Accepted($"/api/tasks/{work.RunId}", work));
            })
            .RequireRight(Rights.DefaultTags)
            .WithTags("Folders")
            .WithName("AddDefaultTag")
            .WithSummary($"Makes a plain tag (`id`, or `name`: found, else made) a default of the library or of `folder` (and the folders under it): the books that come in get it. With `existing`, the books there get it too; past {TagService.InlineLinks} a background run of the library (202 with it).");

        api.MapDelete("/libraries/{id:int}/default-tags/{tagId:int}", async (int id, int tagId, int? folder, bool? books, HttpContext context, CurrentAccess current,
                DefaultTags defaults) =>
            {
                var state = await current.GetAsync();
                if (!state.Access.HasIn(Rights.DefaultTags, id))
                    return Results.NotFound();
                var result = await defaults.RemoveAsync(state.TenantId!.Value, id, folder, tagId, books == true, state.User!.Id, context.RequestAborted);
                return ApiResults.From(result, work => work.RunId is null ? Results.Ok(work) : Results.Accepted($"/api/tasks/{work.RunId}", work));
            })
            .RequireRight(Rights.DefaultTags)
            .WithTags("Folders")
            .WithName("RemoveDefaultTag")
            .WithSummary("Takes a default tag off the library or `folder`. With `books=true`, the books there and under it lose the links it gave (not those given by hand or by an import), unless the library or a folder above still gives it.");

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
