using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Covers;
using Librarion.Core.Reading;

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
            .WithDescription("`sort` = `title` (default), `author`, `added` (newest first) or `number` (with `tag`: by the number on the link, a series' order); `after` = the `next` of the page before; `tag` = only books with this tag or a descendant (a field value's page); `limit` up to 200.");

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

        books.MapGet("/{id:int}/cover/{size}", async (int id, string size, HttpContext context, CurrentAccess current, BookVisibilities visibilities,
                CoverService covers) =>
            {
                if (!Enum.TryParse<CoverSize>(size, ignoreCase: true, out var coverSize) || !Enum.IsDefined(coverSize))
                    return Results.NotFound();
                var state = await current.GetAsync();
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                if (await covers.GetAsync(state.TenantId!.Value, visibility, id, coverSize, context.RequestAborted) is not { } cover)
                    return Results.NotFound();
                // A day in the browser, then a revalidation (the tag changes with the cover: a re-import, an edit).
                context.Response.Headers.CacheControl = "private, max-age=86400";
                return Results.File(cover.Path, "image/webp", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(cover.ETag));
            })
            .WithName("GetBookCover")
            .WithSummary("A book's cover as WebP: `s` (80 px wide), `m` (240) or `l` (600); its own cover, else the generated one. Never missing.");

        books.MapGet("/{id:int}/files/{fileId:int}", async (int id, int fileId, HttpContext context, CurrentAccess current, BookVisibilities visibilities,
                BookDownloads downloads) =>
            {
                var state = await current.GetAsync();
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                if (await downloads.FindAsync(state.TenantId!.Value, visibility, id, fileId, context.RequestAborted) is not { } target)
                    return Results.NotFound();
                if (!state.Access.HasIn(Rights.Download, target.LibraryId))
                    return Results.Forbid();
                var (content, problem) = BookDownloads.Open(target);
                if (content is null)
                {
                    return problem switch
                    {
                        BookFileProblem.Gone => TypedResults.Problem("The file is no longer where the last scan found it.", statusCode: StatusCodes.Status410Gone, type: FileGoneType),
                        BookFileProblem.Changed => TypedResults.Problem("The file changed since the last scan: scan the library again.", statusCode: StatusCodes.Status409Conflict, type: FileChangedType),
                        _ => TypedResults.Problem("The archive can't give this file.", statusCode: StatusCodes.Status409Conflict, type: FileUnreadableType),
                    };
                }

                // A compressed entry is not seekable: its length is known all the same (the stream is held to it).
                if (!content.Seekable)
                    context.Response.ContentLength = content.Length;
                return Results.File(content.Stream, Librarion.Core.Formats.BookFormats.ContentType(target.Format), target.Name,
                    target.Modified is { } modified ? new DateTimeOffset(modified, TimeSpan.Zero) : null, EntityTag(target), enableRangeProcessing: content.Seekable);
            })
            .RequireRight(Rights.Download)
            .WithName("DownloadBookFile")
            .WithSummary("A file of a book, streamed from the library: a loose file or an entry of its ZIP, never unpacked.")
            .WithDescription("Needs `library.download` in the book's library. `ETag` from the CRC (else size and time); Range for loose files and stored entries. 410 `file.gone` when the file is no longer there, 409 `file.changed` when it changed since the last scan, 409 `file.unreadable` when its archive can't give it.");

        var tags = api.MapGroup("").WithTags("Books").RequireRight(Rights.Browse);

        tags.MapGet("/fields", async (HttpContext context, CurrentAccess current, TagQueries queries) =>
                TypedResults.Ok(await queries.FieldsAsync((await current.GetAsync()).TenantId!.Value, context.RequestAborted)))
            .WithName("ListFields")
            .WithSummary("The tenant's fields (authors, series, categories…, then custom ones): what has a page of values.");

        tags.MapGet("/fields/{key}/values", async (string key, int? library, string? from, string? after, int? limit, HttpContext context, CurrentAccess current,
                BookVisibilities visibilities, TagQueries queries) =>
            {
                var (tenantId, visibility) = await VisibilityAsync(context, current, visibilities);
                if (library is { } only && !visibility.Libraries.ContainsKey(only))
                    return Results.NotFound();
                return await queries.ValuesAsync(tenantId, key, library, after, limit ?? 100, context.RequestAborted, from, visibility) is { } page
                    ? Results.Ok(page)
                    : Results.NotFound();
            })
            .WithName("ListFieldValues")
            .WithSummary("The values of a field (authors, series, categories…) by sort name, a page at a time by key, with book counts of `library`.")
            .WithDescription(ValuesDescription);

        tags.MapGet("/tags", async (int? library, string? from, string? after, int? limit, HttpContext context, CurrentAccess current,
                BookVisibilities visibilities, TagQueries queries) =>
            {
                var (tenantId, visibility) = await VisibilityAsync(context, current, visibilities);
                if (library is { } only && !visibility.Libraries.ContainsKey(only))
                    return Results.NotFound();
                return Results.Ok(await queries.PlainTagsAsync(tenantId, library, after, limit ?? 100, context.RequestAborted, from, visibility));
            })
            .WithName("ListTags")
            .WithSummary("The plain tags (no field) by sort name, a page at a time by key, with book counts of `library`.")
            .WithDescription(ValuesDescription);

        tags.MapGet("/tags/{id:int}", async (int id, int? library, HttpContext context, CurrentAccess current, BookVisibilities visibilities, TagQueries queries) =>
            {
                var (tenantId, visibility) = await VisibilityAsync(context, current, visibilities);
                if (library is { } only && !visibility.Libraries.ContainsKey(only))
                    return Results.NotFound();
                return await queries.GetAsync(tenantId, id, library, visibility, context.RequestAborted) is { } tag ? Results.Ok(tag) : Results.NotFound();
            })
            .WithName("GetTag")
            .WithSummary("A tag or field value: its field, the tags it is under and those under it, its book count in `library`; its books are `GET api/books?tag=`.");
    }

    private const string ValuesDescription = "With `library` (one the caller browses) only values with books there, each with its count; a page may then be short (a bounded number of rows read per request) while `next` says more follow. `from` = start at the first value sorted at or after it (a jump). Tags the caller's content filters deny are left out.";

    private static async Task<(int TenantId, BookVisibility Visibility)> VisibilityAsync(HttpContext context, CurrentAccess current, BookVisibilities visibilities)
    {
        var state = await current.GetAsync();
        return (state.TenantId!.Value, await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted));
    }

    public const string FileGoneType = "file.gone";
    public const string FileChangedType = "file.changed";
    public const string FileUnreadableType = "file.unreadable";

    /// <summary>The CRC and size of an entry (the bytes themselves); size and time for a loose file whose CRC isn't known.</summary>
    private static Microsoft.Net.Http.Headers.EntityTagHeaderValue EntityTag(BookFileTarget target) =>
        new(target.Crc is { } crc
            ? $"\"{crc:x8}-{target.Size:x}\""
            : $"\"{target.Size:x}-{target.Modified?.Ticks ?? 0:x}\"");
}
