using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Covers;
using Librarion.Core.Reading;
using Librarion.Core.Search;
using Librarion.Core.Tagging;

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

        books.MapGet("/", async (int? library, string? sort, string? after, int? limit, int? tag, string? q, HttpContext context, CurrentAccess current,
                BookVisibilities visibilities, BookQueries queries) =>
            {
                var state = await current.GetAsync();
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                return TypedResults.Ok(await queries.ListAsync(state.TenantId!.Value, visibility,
                    new BookPageRequest(library, Enum.TryParse<BookSort>(sort, ignoreCase: true, out var order) ? order : BookSort.Title, after, limit ?? 50, tag, q),
                    context.RequestAborted));
            })
            .WithName("ListBooks")
            .WithSummary("Books the caller may browse, a page at a time by key.")
            .WithDescription("`sort` = `title` (default), `author`, `added` (newest first) or `number` (with `tag`: by the number on the link, a series' order); `after` = the `next` of the page before; `tag` = only books with this tag or a descendant (a field value's page); `q` = a search (words, \"phrases\", `author:\"Stephen King\"`, `author:`, `tag:`, `format:epub`, `title:`, `-` to leave out, `*` for a prefix; books with a hidden tag left out unless it is named); `limit` up to 200.");

        books.MapGet("/{id:int}", async (int id, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookQueries queries) =>
            {
                var state = await current.GetAsync();
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                return await queries.GetAsync(state.TenantId!.Value, visibility, id, context.RequestAborted) is { } book
                    ? Results.Ok(WithRights(book, state))
                    : Results.NotFound();
            })
            .WithName("GetBook")
            .WithSummary("One book: its tags by field, files and identifiers; `mergedIntoId` when it now lives in another book; `canDownload` when the caller may download its files, `canTag` / `canEdit` when it may change its tags / its metadata.");

        books.MapPut("/{id:int}", async (int id, BookEdit edit, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookQueries queries,
                BookEditor editor) =>
            {
                var state = await current.GetAsync();
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                if (await queries.GetAsync(state.TenantId!.Value, visibility, id, context.RequestAborted) is not { MergedIntoId: null } book)
                    return Results.NotFound();
                var metadata = edit.Columns is not null || edit.Fields?.Any(f => f.Field is not null) == true;
                var plain = edit.Fields?.Any(f => f.Field is null) == true;
                if (metadata && !state.Access.HasIn(Rights.EditBooks, book.LibraryId) || plain && !state.Access.HasIn(Rights.TagBooks, book.LibraryId))
                    return Results.Forbid();
                var saved = await editor.SaveAsync(state.TenantId.Value, id, edit, context.RequestAborted);
                if (saved.Status != AccessStatus.Ok)
                    return ApiResults.From(saved);
                return Results.Ok(WithRights((await queries.GetAsync(state.TenantId.Value, visibility, id, context.RequestAborted))!, state));
            })
            .RequireRight(Rights.TagBooks, Rights.EditBooks)
            .WithName("EditBook")
            .WithSummary("Changes a book: `columns` (title, description, rating 0-10, 0 none) need `library.edit`, as do `fields` of a field; `fields` with `field` null (plain tags) need `library.tag`. Each field's tags are set whole, in order (authors), by `id` or by `name` (found in the field, else made); what is set stays through re-imports, a tag taken off stays off. Answers the book as `GET` does.");

        // Plain tags on many books (library.tag in their libraries): a selection, or every book of a list (a scope).
        books.MapPost("/tags/state", async (SelectionInput input, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookTagger tagger) =>
            {
                var state = await current.GetAsync();
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                return Results.Ok(await tagger.StateAsync(state.TenantId!.Value, visibility, Taggable(state), input.Books ?? [], context.RequestAborted));
            })
            .RequireRight(Rights.TagBooks)
            .WithName("GetSelectionTags")
            .WithSummary($"The plain tags of the selected books (at most {BookTagger.MaxSelection}) the caller may tag, each with how many of them have it.");

        books.MapPost("/tags", async (SelectionInput input, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookTagger tagger) =>
            {
                var state = await current.GetAsync();
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                return ApiResults.From(await tagger.TagSelectionAsync(state.TenantId!.Value, visibility, Taggable(state), input.Books ?? [],
                    new TagChange(input.Add, input.Remove), context.RequestAborted));
            })
            .RequireRight(Rights.TagBooks)
            .WithName("TagSelection")
            .WithSummary("Gives the selected books the tags of `add` (by `id`, or by `name`: found, else made) and takes those of `remove` away; books the caller can't tag are left out. A tag taken off stays off through re-imports.");

        books.MapGet("/scope", async (int library, int? tag, string? q, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookTagger tagger) =>
            {
                var state = await current.GetAsync();
                if (!state.Access.HasIn(Rights.TagBooks, library))
                    return Results.NotFound();
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                return Results.Ok(new ScopeCount(await tagger.CountAsync(state.TenantId!.Value, visibility, new BookScope(library, tag, q), context.RequestAborted)));
            })
            .RequireRight(Rights.TagBooks)
            .WithName("CountScope")
            .WithSummary("How many books a list holds (the library's, with `tag`, matching `q`): what tagging all of them asks to confirm.");

        books.MapPost("/scope/tags", async (ScopeInput input, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookTagger tagger) =>
            {
                var state = await current.GetAsync();
                if (!state.Access.HasIn(Rights.TagBooks, input.Library))
                    return Results.NotFound();
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                var result = await tagger.TagScopeAsync(state.TenantId!.Value, visibility, new BookScope(input.Library, input.Tag, input.Q),
                    new TagChange(input.Add, input.Remove), state.User.Id, context.RequestAborted);
                return ApiResults.From(result, work => work.RunId is null ? Results.Ok(work) : Results.Accepted($"/api/tasks/{work.RunId}", work));
            })
            .RequireRight(Rights.TagBooks)
            .WithName("TagScope")
            .WithSummary($"Gives every book of a list (`library`, with `tag`, matching `q`) the tags of `add` and takes those of `remove` away, by batches of {TagService.BatchSize}; past {TagService.InlineLinks} changes a background run of the library (202 with it).");

        books.MapGet("/{id:int}/duplicates", async (int id, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookQueries queries) =>
            {
                var (tenantId, visibility) = await VisibilityAsync(context, current, visibilities);
                return await queries.DuplicatesAsync(tenantId, visibility, id, context.RequestAborted) is { } duplicates
                    ? Results.Ok(duplicates)
                    : Results.NotFound();
            })
            .WithName("ListBookDuplicates")
            .WithSummary("Other books the caller can see that may be this one: a file of the same size and CRC (`file`), a shared identifier (`identifier`, with its `scheme`), the same title and first author (`work`).")
            .WithDescription($"At most {BookQueries.MaxDuplicates}, strongest reason first; a key shared by more than {BookQueries.MaxGroup} books is noise and ignored.");

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

        tags.MapGet("/tags/pick", async (string? field, string? q, int? limit, HttpContext context, CurrentAccess current, BookVisibilities visibilities, TagPicker picker) =>
            {
                var (tenantId, visibility) = await VisibilityAsync(context, current, visibilities);
                return await picker.FindAsync(tenantId, field, q, limit ?? 10, visibility, context.RequestAborted) is { } found ? Results.Ok(found) : Results.NotFound();
            })
            .WithName("PickTags")
            .WithSummary("The tag picker's suggestions: tags of `field` (none: plain tags) whose name or other name starts with `q`, whose shorthand is `q`, or whose name has its words; the typed name first, then the most used; `exact` when one is named so.");

        tags.MapGet("/tags/{id:int}/moved", async (int id, int? library, HttpContext context, CurrentAccess current, BookVisibilities visibilities, TagQueries queries) =>
            {
                var (tenantId, visibility) = await VisibilityAsync(context, current, visibilities);
                if (library is { } only && !visibility.Libraries.ContainsKey(only))
                    return Results.NotFound();
                return await queries.MovedAsync(tenantId, id, library, visibility, context.RequestAborted) is { } tag ? Results.Ok(tag) : Results.NotFound();
            })
            .WithName("GetMovedTag")
            .WithSummary("Where a tag that is gone went: the tag it was merged into (through later merges), for its old addresses; 404 when it wasn't merged.");

        tags.MapGet("/search/suggestions", async (string? q, int? library, HttpContext context, CurrentAccess current, BookVisibilities visibilities,
                SearchSuggestions suggestions) =>
            {
                var (tenantId, visibility) = await VisibilityAsync(context, current, visibilities);
                if (library is { } only && !visibility.Libraries.ContainsKey(only))
                    return Results.NotFound();
                return Results.Ok(await suggestions.SuggestAsync(tenantId, q, library, visibility, context.RequestAborted));
            })
            .WithName("SuggestSearch")
            .WithSummary("What to offer while a search is typed: for its last term, fields by key, then field values and tags by name (the most used in `library` first).")
            .WithDescription("`from` = where that term starts in `q`: an item's `insert` replaces the text from there on.");

        tags.MapGet("/search/facets", async (int library, string? q, HttpContext context, CurrentAccess current, BookVisibilities visibilities,
                SearchFacets facets) =>
            {
                var (tenantId, visibility) = await VisibilityAsync(context, current, visibilities);
                if (!visibility.Libraries.ContainsKey(library))
                    return Results.NotFound();
                return Results.Ok(await facets.ListAsync(tenantId, visibility, library, q, context.RequestAborted));
            })
            .WithName("ListSearchFacets")
            .WithSummary("The most frequent values of each field (and format) among the books of `library` matching `q`, with counts: each value's `term` narrows the search.")
            .WithDescription($"Without `q` in a library seen whole, from the stored counts (fields of more than {SearchFacets.MaxValuesRead} values, as authors, left out); otherwise counted over at most {SearchFacets.MaxCounted} of the matches, `capped` when more may match (the counts are then at least that).");
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

    private static BookDetail WithRights(BookDetail book, AccessState state) => book with
    {
        CanDownload = book.MergedIntoId is null && state.Access.HasIn(Rights.Download, book.LibraryId),
        CanTag = book.MergedIntoId is null && state.Access.HasIn(Rights.TagBooks, book.LibraryId),
        CanEdit = book.MergedIntoId is null && state.Access.HasIn(Rights.EditBooks, book.LibraryId),
    };

    /// <summary>The libraries where the caller may tag books.</summary>
    private static IReadOnlySet<int> Taggable(AccessState state) => state.Access.LibrariesWith(Rights.TagBooks).ToHashSet();

    public sealed record SelectionInput(IReadOnlyList<int>? Books, IReadOnlyList<BookTagEdit>? Add = null, IReadOnlyList<int>? Remove = null);

    public sealed record ScopeInput(int Library, int? Tag = null, string? Q = null, IReadOnlyList<BookTagEdit>? Add = null, IReadOnlyList<int>? Remove = null);

    public sealed record ScopeCount(long Books);
}
