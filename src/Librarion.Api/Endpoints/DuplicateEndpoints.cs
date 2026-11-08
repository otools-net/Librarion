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
/// <c>library.merge</c> in the books' library, on books the caller can see.
/// </summary>
public static class DuplicateEndpoints
{
    public sealed record BuildStarted(long RunId);

    public sealed record NotDuplicatesInput(IReadOnlyList<int> BookIds);

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
