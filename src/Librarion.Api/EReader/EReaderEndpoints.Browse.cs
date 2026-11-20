using Librarion.Api.EReader.Pages;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Librarion.Api.EReader;

/// <summary>
/// Browsing a library by its fields and plain tags (stage 8 chunk 5), as the OPDS feeds do
/// (agents/docs/features/opds.md): a field's first letters when its values don't fit a page
/// (<see cref="TagQueries.InitialsAsync"/>), its values from a letter by key, a value's books (a series by its
/// numbers), a tag with tags under it as a list of them. Content filters as everywhere: a denied tag is not there.
/// </summary>
public static partial class EReaderEndpoints
{
    private static void MapBrowse(RouteGroupBuilder library)
    {
        library.MapGet("/fields/{key}", (int library, string key, string? from, string? after, HttpContext context, CurrentAccess current, AccessService accounts,
            BookVisibilities visibilities, TagQueries tags, ServerTexts texts) => ValuesAsync(library, key, from, after, context, current, accounts, visibilities, tags, texts));
        library.MapGet("/tags", (int library, string? from, string? after, HttpContext context, CurrentAccess current, AccessService accounts,
            BookVisibilities visibilities, TagQueries tags, ServerTexts texts) => ValuesAsync(library, null, from, after, context, current, accounts, visibilities, tags, texts));
        library.MapGet("/tags/{tag:int}", TagAsync);
        library.MapGet("/tags/{tag:int}/books", TagBooksAsync);
    }

    /// <summary>
    /// A field's values (or the plain tags): with no letter nor cursor and more than a page of them, the first letters
    /// they start with and "All"; from a letter (<c>from</c>; empty: from the start) or a cursor, a page of values with
    /// their books in the library, Next by key (keeping the letter, so First goes back to it).
    /// </summary>
    private static async Task<IResult> ValuesAsync(int library, string? key, string? from, string? after, HttpContext context, CurrentAccess current,
        AccessService accounts, BookVisibilities visibilities, TagQueries tags, ServerTexts texts)
    {
        var state = await current.GetAsync();
        if (await LibraryNameAsync(state, library, accounts, context.RequestAborted) is not { } libraryName)
            return Results.NotFound();
        var tenantId = state.TenantId!.Value;
        var cancellationToken = context.RequestAborted;
        string title;
        if (key is null)
        {
            title = texts["shell.nav.tags"];
        }
        else
        {
            if ((await tags.FieldsAsync(tenantId, cancellationToken)).FirstOrDefault(f => f.Key == key) is not { } field)
                return Results.NotFound();
            title = texts.FieldName(field.Key, field.Name, field.BuiltIn);
        }

        var visibility = await visibilities.GetAsync(new AccessSubject(tenantId, state.User!.Id), cancellationToken);
        var page = key is null
            ? await tags.PlainTagsAsync(tenantId, library, after, PageSize, cancellationToken, from, visibility)
            : (await tags.ValuesAsync(tenantId, key, library, after, PageSize, cancellationToken, from, visibility))!;
        var links = new EReaderLinks(context);
        var frame = await EReaderFrame.ForAsync(context);

        if (from is null && after is null && page.Next is not null)
        {
            return new RazorComponentResult<LettersPage>(new
            {
                Frame = frame,
                Title = title,
                Up = new EPage.Crumb(libraryName, links.Library(library)),
                Letters = (IReadOnlyList<LettersPage.Letter>)(await tags.InitialsAsync(tenantId, key, cancellationToken))
                    .Select(l => new LettersPage.Letter(l, links.Values(library, key, from: l))).ToList(),
                // Names in other scripts have no letter here: the whole list from its start reaches them.
                All = links.Values(library, key, from: ""),
            });
        }

        // A list that fits one page needs no letters: its page is the field's own (up to the library); from a letter, up to the letters.
        var letter = string.IsNullOrEmpty(from) ? null : from;
        var whole = from is null && after is null;
        return new RazorComponentResult<ValuesPage>(new
        {
            Frame = frame,
            Title = letter is null ? title : texts.Format("ereader.values.fromLetter", ("name", title), ("letter", letter)),
            Up = whole ? new EPage.Crumb(libraryName, links.Library(library)) : new EPage.Crumb(title, links.Values(library, key)),
            Values = (IReadOnlyList<ValuesPage.Value>)page.Items.Select(v => new ValuesPage.Value(v.Name, links.Tag(library, v.Id), v.BookCount)).ToList(),
            Next = page.Next is { } next ? links.Values(library, key, from, next) : null,
            First = after is null ? null : links.Values(library, key, from ?? ""),
        });
    }

    /// <summary>
    /// A tag's page: with tags under it, "All books" and those tags; else its books. A merged tag's old address goes to
    /// the tag it became (301); one the content filters deny, or of no book here, is not there (404).
    /// </summary>
    private static async Task<IResult> TagAsync(int library, int tag, HttpContext context, CurrentAccess current, AccessService accounts,
        BookVisibilities visibilities, TagQueries tags, BookQueries books, ServerTexts texts)
    {
        var state = await current.GetAsync();
        if (await LibraryNameAsync(state, library, accounts, context.RequestAborted) is null)
            return Results.NotFound();
        var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
        var links = new EReaderLinks(context);
        if (await tags.GetAsync(state.TenantId!.Value, tag, library, visibility, context.RequestAborted) is not { } detail)
            return await MovedTagAsync(state, library, tag, visibility, tags, context, moved => links.Tag(library, moved));

        if (detail.Children.Count == 0)
            return await TagBooksPageAsync(library, detail, null, state, visibility, links, books, texts, context);

        return new RazorComponentResult<TagPage>(new
        {
            Frame = await EReaderFrame.ForAsync(context),
            Title = detail.Name,
            Up = UpOf(library, detail, links, texts),
            AllBooks = links.TagBooks(library, tag),
            BookCount = detail.BookCount,
            Children = (IReadOnlyList<ValuesPage.Value>)detail.Children.Select(c => new ValuesPage.Value(c.Name, links.Tag(library, c.Id), null)).ToList(),
            MoreChildren = detail.MoreChildren,
        });
    }

    private static async Task<IResult> TagBooksAsync(int library, int tag, string? after, HttpContext context, CurrentAccess current, AccessService accounts,
        BookVisibilities visibilities, TagQueries tags, BookQueries books, ServerTexts texts)
    {
        var state = await current.GetAsync();
        if (await LibraryNameAsync(state, library, accounts, context.RequestAborted) is null)
            return Results.NotFound();
        var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
        var links = new EReaderLinks(context);
        if (await tags.GetAsync(state.TenantId!.Value, tag, library, visibility, context.RequestAborted) is not { } detail)
            return await MovedTagAsync(state, library, tag, visibility, tags, context, moved => links.TagBooks(library, moved));
        return await TagBooksPageAsync(library, detail, after, state, visibility, links, books, texts, context);
    }

    /// <summary>A merged tag's old address: on to the tag it became (permanent); else not found.</summary>
    private static async Task<IResult> MovedTagAsync(AccessState state, int library, int tag, BookVisibility visibility, TagQueries tags, HttpContext context,
        Func<int, string> address) =>
        await tags.MovedAsync(state.TenantId!.Value, tag, library, visibility, context.RequestAborted) is { } moved
            ? Results.Redirect(address(moved.Id), permanent: true)
            : Results.NotFound();

    /// <summary>
    /// A tag's books (with those of the tags under it): a series by its numbers (a series with no numbers at all comes
    /// by title; the cursor tells which order a next page is in), anything else by title; next pages by key.
    /// </summary>
    private static async Task<IResult> TagBooksPageAsync(int library, TagDetail detail, string? after, AccessState state, BookVisibility visibility,
        EReaderLinks links, BookQueries books, ServerTexts texts, HttpContext context)
    {
        var tenantId = state.TenantId!.Value;
        var cancellationToken = context.RequestAborted;
        var sort = detail.Field == "series" ? BookSort.Number : BookSort.Title;
        if (sort == BookSort.Number && after is not null && KeysetCursor.Decode(after) is { Real: null })
            sort = BookSort.Title;
        var page = await books.ListAsync(tenantId, visibility, new BookPageRequest(library, sort, after, PageSize, detail.Id), cancellationToken);
        if (sort == BookSort.Number && after is null && page.Items.Count == 0)
            page = await books.ListAsync(tenantId, visibility, new BookPageRequest(library, BookSort.Title, null, PageSize, detail.Id), cancellationToken);

        return new RazorComponentResult<BooksPage>(new
        {
            Frame = await EReaderFrame.ForAsync(context),
            Title = detail.Name,
            Up = detail.Children.Count > 0 ? new EPage.Crumb(detail.Name, links.Tag(library, detail.Id)) : UpOf(library, detail, links, texts),
            Books = page.Items,
            Next = page.Next is { } next ? links.TagBooks(library, detail.Id, next) : null,
            First = after is null ? null : links.TagBooks(library, detail.Id),
        });
    }

    /// <summary>Up from a tag: its field's values, or the plain tags.</summary>
    private static EPage.Crumb UpOf(int library, TagDetail detail, EReaderLinks links, ServerTexts texts) => detail.Field is { } field
        ? new EPage.Crumb(texts.FieldName(field, detail.FieldName ?? field, builtIn: true), links.Values(library, field))
        : new EPage.Crumb(texts["shell.nav.tags"], links.Values(library, null));
}
