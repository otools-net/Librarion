using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Catalog;

namespace Librarion.Api.Opds;

/// <summary>
/// Browsing a library by its fields and tags (stage 7 chunk 6, agents/docs/features/opds.md): a field's values A to
/// Z (by first letter when they don't fit one page), a value's books (a series by its numbers), a tag with tags under
/// it as a little tree.
/// </summary>
public static partial class OpdsEndpoints
{
    /// <summary>The built-in fields' names in feeds (their keys are their names in the database; the client translates them).</summary>
    private static readonly Dictionary<string, string> FieldNames = new(StringComparer.Ordinal)
    {
        ["author"] = "Authors",
        ["translator"] = "Translators",
        ["editor"] = "Editors",
        ["illustrator"] = "Illustrators",
        ["series"] = "Series",
        ["category"] = "Categories",
        ["publisher"] = "Publishers",
        ["language"] = "Languages",
        ["year"] = "Years",
    };

    internal static string FieldName(FieldInfo field) => field.BuiltIn && FieldNames.TryGetValue(field.Key, out var name) ? name : field.Name;

    private static void MapBrowse(RouteGroupBuilder library)
    {
        library.MapGet("/fields/{key}", FieldAsync);
        library.MapGet("/tags", (int library, string? from, string? after, HttpContext context, CurrentAccess current, BookVisibilities visibilities, TagQueries tags) =>
            ValuesAsync(library, null, from, after, context, current, visibilities, tags));
        library.MapGet("/tags/{tag:int}", TagAsync);
        library.MapGet("/tags/{tag:int}/books", TagBooksAsync);
    }

    private static Task<IResult> FieldAsync(int library, string key, string? from, string? after, HttpContext context, CurrentAccess current,
        BookVisibilities visibilities, TagQueries tags) =>
        ValuesAsync(library, key, from, after, context, current, visibilities, tags);

    /// <summary>
    /// A field's values (or the plain tags): without a starting point and more than a page of them, the first letters
    /// they start with (a skip scan, <see cref="TagQueries.InitialsAsync"/>); from a letter or a cursor, a page of values
    /// with their books in the library, by key.
    /// </summary>
    private static async Task<IResult> ValuesAsync(int library, string? key, string? from, string? after, HttpContext context, CurrentAccess current,
        BookVisibilities visibilities, TagQueries tags)
    {
        var state = await current.GetAsync();
        if (!Opens(state, library))
            return Results.NotFound();
        var tenantId = state.TenantId!.Value;
        var cancellationToken = context.RequestAborted;
        string title;
        if (key is null)
        {
            title = "Tags";
        }
        else
        {
            if ((await tags.FieldsAsync(tenantId, cancellationToken)).FirstOrDefault(f => f.Key == key) is not { } field)
                return Results.NotFound();
            title = FieldName(field);
        }

        var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), cancellationToken);
        var page = key is null
            ? await tags.PlainTagsAsync(tenantId, library, after, PageSize, cancellationToken, from, visibility)
            : (await tags.ValuesAsync(tenantId, key, library, after, PageSize, cancellationToken, from, visibility))!;
        var links = new OpdsLinks(context);
        var id = $"urn:librarion:library:{library}:" + (key is null ? "tags" : "field:" + key);

        // "from=" (empty): the whole list from its start, past the letters.
        if (from is null && after is null && page.Next is not null)
        {
            var letters = OpdsFeed.Start(OpdsFeed.NavigationType, id, title, links, links.Values(library, key), links.Library(library));
            foreach (var letter in await tags.InitialsAsync(tenantId, key, cancellationToken))
                letters.Navigation($"{id}:{letter}", letter, links.Values(library, key, from: letter), OpdsFeed.NavigationType);
            // Names in other scripts have no letter here: the whole list from its start reaches them.
            return letters.Navigation($"{id}:all", "All", links.Values(library, key, from: "", after: null), OpdsFeed.NavigationType,
                "Every one, from the start.");
        }

        var start = from is null && after is null;
        from = string.IsNullOrEmpty(from) ? null : from;
        var feed = OpdsFeed.Start(OpdsFeed.NavigationType, from is null ? id : $"{id}:{from}", from is null ? title : $"{title}: {from}", links,
            links.Values(library, key, from, after), start ? links.Library(library) : links.Values(library, key),
            page.Next is { } next ? links.Values(library, key, after: next) : null);
        foreach (var value in page.Items)
            feed.Navigation($"urn:librarion:tag:{value.Id}", value.Name, links.Tag(library, value.Id), OpdsFeed.CatalogType, BooksText(value.BookCount));
        return feed;
    }

    /// <summary>
    /// A tag's page: with tags under it, a little tree (all its books, then each tag under it); else its books. A merged
    /// tag's old address goes to the tag it became (301); one denied by the content filters is not there (404).
    /// </summary>
    private static async Task<IResult> TagAsync(int library, int tag, HttpContext context, CurrentAccess current, BookVisibilities visibilities,
        TagQueries tags, BookQueries books)
    {
        var state = await current.GetAsync();
        if (!Opens(state, library))
            return Results.NotFound();
        var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
        var links = new OpdsLinks(context);
        if (await tags.GetAsync(state.TenantId!.Value, tag, library, visibility, context.RequestAborted) is not { } detail)
        {
            return await tags.MovedAsync(state.TenantId!.Value, tag, library, visibility, context.RequestAborted) is { } moved
                ? Results.Redirect(links.Tag(library, moved.Id), permanent: true)
                : Results.NotFound();
        }

        if (detail.Children.Count == 0)
            return await TagBooksFeedAsync(library, detail, null, state, visibility, links, books, context.RequestAborted);

        var feed = OpdsFeed.Start(OpdsFeed.NavigationType, $"urn:librarion:tag:{tag}", detail.Name, links, links.Tag(library, tag), UpOf(library, detail, links))
            .Navigation($"urn:librarion:tag:{tag}:books", $"All books: {detail.Name}", links.TagBooks(library, tag), OpdsFeed.AcquisitionType,
                BooksText(detail.BookCount));
        foreach (var child in detail.Children)
            feed.Navigation($"urn:librarion:tag:{child.Id}", child.Name, links.Tag(library, child.Id), OpdsFeed.CatalogType);
        return feed;
    }

    private static async Task<IResult> TagBooksAsync(int library, int tag, string? after, HttpContext context, CurrentAccess current,
        BookVisibilities visibilities, TagQueries tags, BookQueries books)
    {
        var state = await current.GetAsync();
        if (!Opens(state, library))
            return Results.NotFound();
        var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
        var links = new OpdsLinks(context);
        if (await tags.GetAsync(state.TenantId!.Value, tag, library, visibility, context.RequestAborted) is not { } detail)
        {
            return await tags.MovedAsync(state.TenantId!.Value, tag, library, visibility, context.RequestAborted) is { } moved
                ? Results.Redirect(links.TagBooks(library, moved.Id), permanent: true)
                : Results.NotFound();
        }
        return await TagBooksFeedAsync(library, detail, after, state, visibility, links, books, context.RequestAborted);
    }

    /// <summary>
    /// A tag's books (with those of the tags under it): a series by its numbers (books without one are not in that
    /// order: a series with no numbers at all comes by title), anything else by title; next pages by key.
    /// </summary>
    private static async Task<IResult> TagBooksFeedAsync(int library, TagDetail detail, string? after, AccessState state, BookVisibility visibility,
        OpdsLinks links, BookQueries books, CancellationToken cancellationToken)
    {
        var tenantId = state.TenantId!.Value;
        var sort = detail.Field == "series" ? BookSort.Number : BookSort.Title;
        if (sort == BookSort.Number && after is not null && KeysetCursor.Decode(after) is { Real: null })
            sort = BookSort.Title;
        var page = await books.ListAsync(tenantId, visibility, new BookPageRequest(library, sort, after, PageSize, detail.Id), cancellationToken);
        if (sort == BookSort.Number && after is null && page.Items.Count == 0)
            page = await books.ListAsync(tenantId, visibility, new BookPageRequest(library, BookSort.Title, null, PageSize, detail.Id), cancellationToken);
        var extras = await books.EntriesAsync(tenantId, page.Items.Select(b => b.Id).ToList(), cancellationToken);

        var feed = OpdsFeed.Start(OpdsFeed.AcquisitionType, $"urn:librarion:tag:{detail.Id}:books", detail.Name, links,
            after is null ? links.TagBooks(library, detail.Id) : links.TagBooks(library, detail.Id, after), UpOf(library, detail, links),
            page.Next is { } next ? links.TagBooks(library, detail.Id, next) : null);
        var canDownload = state.Access.HasIn(Rights.Download, library);
        foreach (var book in page.Items)
            feed.Book(book, extras.GetValueOrDefault(book.Id), links, canDownload);
        return feed;
    }

    /// <summary>Up from a tag: its field's values, or the plain tags.</summary>
    private static string UpOf(int library, TagDetail detail, OpdsLinks links) => links.Values(library, detail.Field);

    private static string? BooksText(int? count) => count switch
    {
        null => null,
        1 => "1 book",
        _ => $"{count.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} books",
    };
}
