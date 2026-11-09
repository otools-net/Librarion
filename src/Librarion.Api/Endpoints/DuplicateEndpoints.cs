using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Merging;
using Librarion.Core.Tasks;

namespace Librarion.Api.Endpoints;

/// <summary>
/// Duplicate books (agents/docs/features/merging.md): the stored groups (stage 6 chunk 11) built whole again on request
/// as a tenant run; the Duplicates page, merging books and marking them not duplicates (chunk 12), all with
/// <c>library.merge</c> in the books' library, on books the caller can see; bulk merges, undo and split (chunk 13).
/// </summary>
public static class DuplicateEndpoints
{
    public sealed record BuildStarted(long RunId);

    public sealed record NotDuplicatesInput(IReadOnlyList<int> BookIds);

    /// <summary>Groups of a library merged in bulk: <c>GroupIds</c>, else every group of <c>Level</c> (<c>file</c> or <c>identifier</c>).</summary>
    public sealed record BulkMergeInput(int Library, IReadOnlyList<long>? GroupIds, string? Level);

    public sealed record SplitInput(IReadOnlyList<int> FileIds);

    /// <param name="Books">The merged books given back.</param>
    public sealed record MergeUndone(int Books);

    /// <summary>The most groups chosen for one bulk merge (a page of the list holds far fewer).</summary>
    public const int MaxBulkGroups = 1000;

    /// <summary>The libraries where the caller may merge books (the Duplicates page's choice).</summary>
    public sealed record DuplicateContext(IReadOnlyList<int> Libraries);

    public static void MapDuplicateEndpoints(this IEndpointRouteBuilder api)
    {
        var duplicates = api.MapGroup("/duplicates").WithTags("Duplicates").RequireRight(Rights.MergeBooks);

        duplicates.MapGet("", async (int library, string? level, string? after, int? limit, HttpContext context, CurrentAccess current, BookVisibilities visibilities,
                DuplicateQueries queries) =>
            {
                var state = await current.GetAsync();
                if (!state.Access.HasIn(Rights.MergeBooks, library))
                    return Results.NotFound();
                DuplicateReason? only = null;
                if (!string.IsNullOrEmpty(level))
                {
                    if (!Enum.TryParse<DuplicateReason>(level, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                        return Invalid("level", "level.invalid");
                    only = parsed;
                }

                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                return Results.Ok(await queries.ListAsync(state.TenantId!.Value, visibility, library, only, after, limit ?? 20, context.RequestAborted));
            })
            .WithName("ListDuplicateGroups")
            .WithSummary("The Duplicates page: groups of possible duplicates in a library, the biggest first, each with at least two books the caller can see; `level` = file, identifier or work; `next` is the `after` of the following page.")
            .WithDescription($"A request reads at most {DuplicateQueries.MaxChunks * DuplicateQueries.ChunkGroups} groups: a short page with a `next` means more to read. `built` is false until the groups were built.");

        duplicates.MapGet("/context", async (CurrentAccess current) =>
                Results.Ok(new DuplicateContext((await current.GetAsync()).Access.LibrariesWith(Rights.MergeBooks).Order().ToList())))
            .WithName("GetDuplicateContext")
            .WithSummary("The libraries where the caller may merge books: those the Duplicates page offers.");

        duplicates.MapPost("/build", async (HttpContext context, CurrentAccess current, BackgroundTasks tasks, DuplicateIndex index) =>
            {
                var state = await current.GetAsync();
                var tenantId = state.TenantId!.Value;
                var run = await tasks.StartTenantAsync(tenantId, BackgroundTaskKind.DuplicateBuild, state.User!.Id, async (progress, cancel) =>
                    new ScanResult(0, await index.BuildAsync(tenantId, progress, cancel), 0, 0, 0, 0, 0, [], []), context.RequestAborted);
                return Results.Accepted(null, new BuildStarted(run));
            })
            .WithName("BuildDuplicates")
            .WithSummary("Starts a background run building the tenant's duplicate groups whole again (same file, identifier, work): 202 with the run's id; its `changed` counts the groups.");

        duplicates.MapPost("/not", async (NotDuplicatesInput input, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookMerger merger,
                DuplicateQueries queries) =>
            {
                var ids = input.BookIds.Distinct().ToList();
                if (ids.Count is < 2 or > BookMerger.MaxBooks)
                    return Invalid("bookIds", "merge.books");
                if (await MayChangeAsync(ids, context, current, visibilities, merger, queries) is { } refused)
                    return refused;
                await queries.MarkAsync((await current.GetAsync()).TenantId!.Value, ids, context.RequestAborted);
                return Results.NoContent();
            })
            .WithName("MarkNotDuplicates")
            .WithSummary("Says the books are not duplicates of each other (every pair among them): never suggested together again, also after re-imports.");

        duplicates.MapPost("/merge", async (BulkMergeInput input, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BackgroundTasks tasks,
                BookMerger merger) =>
            {
                var state = await current.GetAsync();
                if (!state.Access.HasIn(Rights.MergeBooks, input.Library))
                    return Results.NotFound();
                var groups = input.GroupIds is { Count: > 0 } chosen ? chosen.Distinct().ToList() : null;
                if (groups is { Count: > MaxBulkGroups })
                    return Invalid("groupIds", "merge.groups");
                DuplicateReason? level = null;
                if (groups is null)
                {
                    if (!Enum.TryParse<DuplicateReason>(input.Level, ignoreCase: true, out var parsed) || !BookMerger.BulkLevels.Contains(parsed))
                        return Invalid("level", "merge.level");
                    level = parsed;
                }

                var tenantId = state.TenantId!.Value;
                var userId = state.User!.Id;
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, userId), context.RequestAborted);
                var run = await tasks.StartLibraryAsync(tenantId, input.Library, BackgroundTaskKind.BookMerge, userId, async (progress, cancel) =>
                {
                    var (merged, skipped) = await merger.MergeGroupsAsync(tenantId, userId, visibility, input.Library, groups, level, progress, cancel);
                    return new ScanResult(0, merged, 0, 0, 0, 0, 0, [], [], Skipped: skipped);
                }, context.RequestAborted);
                return Results.Accepted(null, new BuildStarted(run));
            })
            .WithName("MergeDuplicateGroups")
            .WithSummary("Merges duplicate groups of a library in bulk as a background run: `groupIds`, else every group of `level` (`file` or `identifier`; title and author groups are merged one at a time). Each group keeps the book with most metadata, with its own values; a group with books marked not duplicates is skipped. 202 with the run's id; its `changed` counts the groups merged, `skipped` those left.")
            .WithDescription($"Refused: `merge.level` (no groups and no bulk level), `merge.groups` (more than {MaxBulkGroups} groups).");

        duplicates.MapGet("/merges", async (int library, long? before, HttpContext context, CurrentAccess current, BookMerger merger) =>
                await current.GetAsync() is { TenantId: { } tenantId } state && state.Access.HasIn(Rights.MergeBooks, library)
                    ? Results.Ok(await merger.ListAsync(tenantId, library, before, context.RequestAborted))
                    : Results.NotFound())
            .WithName("ListBookMerges")
            .WithSummary($"A library's book merges, the newest first, {BookMerger.ListSize} at a time (`before` = the last id of the page before): the kept book and the books merged into it, and when one was undone.");

        duplicates.MapPost("/merges/{id:long}/undo", async (long id, HttpContext context, CurrentAccess current, BookMerger merger) =>
            {
                var state = await current.GetAsync();
                var tenantId = state.TenantId!.Value;
                if (await merger.LogBookAsync(tenantId, id, context.RequestAborted) is not { } book || !state.Access.HasIn(Rights.MergeBooks, book.LibraryId))
                    return Results.NotFound();
                var result = await merger.UndoAsync(tenantId, id, context.RequestAborted);
                return result.Status == AccessStatus.Ok ? Results.Ok(new MergeUndone(result.Value)) : ApiResults.From(result);
            })
            .WithName("UndoBookMerge")
            .WithSummary("Undoes a book merge whole: the merged books get back their files, tags, identifiers and properties and stop leading to the kept book; the kept book's title, description, rating and cover go back when unchanged since.")
            .WithDescription("Refused (409): `merge.undone`, `merge.targetGone`, `merge.later` (the kept book was merged into another since: undo that first).");

        api.MapPost("/books/{id:int}/split", async (int id, SplitInput input, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookMerger merger,
                BookSplitter splitter, DuplicateQueries queries, BookQueries books) =>
            {
                if (await MayChangeAsync([id], context, current, visibilities, merger, queries) is { } refused)
                    return refused;
                var state = await current.GetAsync();
                var result = await splitter.SplitAsync(state.TenantId!.Value, id, input.FileIds ?? [], context.RequestAborted);
                if (result.Status != AccessStatus.Ok)
                    return ApiResults.From(result);
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                return await books.GetAsync(state.TenantId!.Value, visibility, result.Value, context.RequestAborted) is { } split
                    ? Results.Ok(BookEndpoints.WithRights(split, state))
                    : Results.NoContent();
            })
            .WithTags("Duplicates")
            .RequireRight(Rights.MergeBooks)
            .WithName("SplitBook")
            .WithSummary("Moves chosen files of a book out into a new book: read from those files, or a copy of the book's metadata when it came from a Calibre or INPX import. The two are marked not duplicates. Answers the new book (no content when the caller's filters hide it).")
            .WithDescription("Refused: `split.files` (none, not the book's, or all of them).");

        api.MapPost("/books/merge", async (BookMergeInput input, HttpContext context, CurrentAccess current, BookVisibilities visibilities, BookMerger merger,
                DuplicateQueries queries, BookQueries books) =>
            {
                var ids = input.SourceIds.Prepend(input.TargetId).Distinct().ToList();
                if (ids.Count > BookMerger.MaxBooks)
                    return Invalid("sourceIds", "merge.books");
                if (await MayChangeAsync(ids, context, current, visibilities, merger, queries) is { } refused)
                    return refused;
                var state = await current.GetAsync();
                var result = await merger.MergeAsync(state.TenantId!.Value, state.User!.Id, input, context.RequestAborted);
                if (result.Status != AccessStatus.Ok)
                    return ApiResults.From(result);
                var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
                return Results.Ok(BookEndpoints.WithRights((await books.GetAsync(state.TenantId!.Value, visibility, input.TargetId, context.RequestAborted))!, state));
            })
            .WithTags("Duplicates")
            .RequireRight(Rights.MergeBooks)
            .WithName("MergeBooks")
            .WithSummary("Merges books of one library into `targetId`: their files, tags, identifiers and properties move to it; `titleFrom`, `descriptionFrom`, `ratingFrom`, `coverFrom` name the book each value comes from (the target when left out). The merged books' addresses lead to the kept one. Answers the kept book.")
            .WithDescription("Refused: `merge.books` (none, too many, or the target among the sources), `merge.library` (books of two libraries), `merge.choice` (a value from a book not merged).");
    }

    /// <summary>Null when the caller may merge every book (they exist, unmerged, it sees them, it holds <c>library.merge</c> in their libraries); else the answer.</summary>
    private static async Task<IResult?> MayChangeAsync(IReadOnlyCollection<int> ids, HttpContext context, CurrentAccess current, BookVisibilities visibilities,
        BookMerger merger, DuplicateQueries queries)
    {
        var state = await current.GetAsync();
        var tenantId = state.TenantId!.Value;
        var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
        if (await merger.LibrariesAsync(tenantId, ids, context.RequestAborted) is not { } libraries || !await queries.AllVisibleAsync(tenantId, visibility, ids, context.RequestAborted))
            return Results.NotFound();
        return libraries.Values.All(library => state.Access.HasIn(Rights.MergeBooks, library)) ? null : Results.Forbid();
    }

    private static IResult Invalid(string field, string code) => TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [code] });
}
