using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Covers;
using Librarion.Core.Covers.Generated;

namespace Librarion.Api.Endpoints;

/// <summary>
/// A book's cover chosen on its page (stage 6 chunk 10, agents/docs/features/book-display.md): an upload, a generated
/// style, back to its own, each with <c>library.edit</c> in the book's library, answering the book as <c>GET
/// api/books/{id}</c> does; the styles and their previews for the picker. A book the caller can't see is not found.
/// </summary>
public static class CoverEndpoints
{
    public sealed record StyleInput(int Template, int Variant);

    /// <summary>The generated styles: templates by index (their names, translated by the client) and how many palettes each has.</summary>
    public sealed record CoverStyles(IReadOnlyList<string> Templates, int Palettes);

    public static void MapCoverEndpoints(this IEndpointRouteBuilder api)
    {
        var covers = api.MapGroup("/books/{id:int}/cover").WithTags("Covers").RequireRight(Rights.EditBooks);

        covers.MapPut("", async (int id, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookQueries queries, CoverEditor editor) =>
            {
                var (state, book) = await EditableAsync(id, context, current, visibilities, queries);
                if (book is null)
                    return state is null ? Results.Forbid() : Results.NotFound();
                if (context.Request.ContentLength > CoverUploads.MaxBytes)
                    return Invalid("cover.tooBig");
                // Read up to one byte past the limit: a body without a length is cut there.
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > CoverUploads.MaxBytes)
                        return Invalid("cover.tooBig");
                }

                return await AnswerAsync(await editor.UploadAsync(state!.TenantId!.Value, id, buffer.ToArray(), context.RequestAborted), id, state, visibilities, queries, context);
            })
            .WithName("UploadCover")
            .WithSummary($"Makes the picture in the body (JPEG, PNG or WebP, at most {CoverUploads.MaxBytes / 1024 / 1024} MB) the book's cover: checked, made again at most {CoverUploads.MaxWidth} px wide, kept through re-imports. Refused: `cover.tooBig`, `cover.invalid`.");

        covers.MapPut("/style", async (int id, StyleInput input, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookQueries queries, CoverEditor editor) =>
            {
                var (state, book) = await EditableAsync(id, context, current, visibilities, queries);
                if (book is null)
                    return state is null ? Results.Forbid() : Results.NotFound();
                return await AnswerAsync(await editor.StyleAsync(state!.TenantId!.Value, id, input.Template, input.Variant, context.RequestAborted), id, state, visibilities, queries, context);
            })
            .WithName("SetCoverStyle")
            .WithSummary("Makes a generated style (`template`, `variant` = palette) the book's cover, over an upload or its own cover; kept through re-imports.");

        covers.MapDelete("", async (int id, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookQueries queries, CoverEditor editor) =>
            {
                var (state, book) = await EditableAsync(id, context, current, visibilities, queries);
                if (book is null)
                    return state is null ? Results.Forbid() : Results.NotFound();
                return await AnswerAsync(await editor.OwnAsync(state!.TenantId!.Value, id, context.RequestAborted), id, state, visibilities, queries, context);
            })
            .WithName("UseOwnCover")
            .WithSummary("Back to the book's own cover (Calibre's, the one in its files, else its generated style): an upload is let go.");

        covers.MapGet("/preview/{template:int}/{variant:int}", async (int id, int template, int variant, HttpContext context, CurrentAccess current,
                BookVisibilities visibilities, CoverService service) =>
            {
                var state = await current.GetAsync();
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                if (await service.PreviewAsync(state.TenantId!.Value, visibility, id, template, variant, CoverSize.S, context.RequestAborted) is not { } cover)
                    return Results.NotFound();
                context.Response.Headers.CacheControl = "private, max-age=86400";
                return Results.File(cover.Path, "image/webp", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(cover.ETag));
            })
            .WithName("PreviewCoverStyle")
            .WithSummary("The book drawn in a generated style, small (80 px wide): the previews of Change cover style.");

        api.MapGet("/covers/styles", () => Results.Ok(new CoverStyles(GeneratedCovers.Templates.Select(t => t.Name).ToList(), GeneratedCovers.Palettes.Count)))
            .WithTags("Covers")
            .RequireRight(Rights.EditBooks)
            .WithName("ListCoverStyles")
            .WithSummary("The generated cover styles: the templates by index and the number of palettes.");
    }

    /// <summary>The caller and the book when it may edit it; (state, null) when the book isn't there for it, (null, null) when it may not edit it.</summary>
    private static async Task<(AccessState? State, BookDetail? Book)> EditableAsync(int id, HttpContext context, CurrentAccess current, BookVisibilities visibilities,
        BookQueries queries)
    {
        var state = await current.GetAsync();
        var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
        if (await queries.GetAsync(state.TenantId!.Value, visibility, id, context.RequestAborted) is not { MergedIntoId: null } book)
            return (state, null);
        return state.Access.HasIn(Rights.EditBooks, book.LibraryId) ? (state, book) : (null, null);
    }

    private static async Task<IResult> AnswerAsync(AccessResult<bool> result, int id, AccessState state, BookVisibilities visibilities, BookQueries queries, HttpContext context)
    {
        if (result.Status != AccessStatus.Ok)
            return ApiResults.From(result);
        var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
        return Results.Ok(BookEndpoints.WithRights((await queries.GetAsync(state.TenantId!.Value, visibility, id, context.RequestAborted))!, state));
    }

    private static IResult Invalid(string code) => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["cover"] = [code] });
}
