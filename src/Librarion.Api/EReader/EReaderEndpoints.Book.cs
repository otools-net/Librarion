using System.Text.RegularExpressions;
using Librarion.Api.EReader.Pages;
using Librarion.Api.Endpoints;
using Librarion.Api.Opds;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Reading;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Librarion.Api.EReader;

/// <summary>
/// A book's page and its downloads (stage 8 chunk 6): the same <see cref="BookQueries.GetAsync"/> as the app's page,
/// fields as links to their values' books, the description as plain text, the files with Download (an FB2 also
/// zipped, as OPDS). Downloads through the app's <see cref="BookEndpoints.Download"/> (Range, ETag, loose files and ZIP
/// entries, an ASCII name beside the UTF-8 one: <see cref="DownloadNames"/>).
/// </summary>
public static partial class EReaderEndpoints
{
    /// <summary>The description's length at most on a book page (cut at a word, "…"): one or two screens of e-ink.</summary>
    public const int MaxDescription = 2000;

    private static void MapBook(RouteGroupBuilder library)
    {
        library.MapGet("/books/{id:int}", BookAsync);
        library.MapGet("/books/{id:int}/files/{fileId:int}", (int library, int id, int fileId, HttpContext context, CurrentAccess current, BookVisibilities visibilities,
            BookDownloads downloads, ServerTexts texts) => DownloadAsync(library, id, fileId, zipped: false, context, current, visibilities, downloads, texts)).RequireRight(Rights.Download);
        library.MapGet("/books/{id:int}/files/{fileId:int}/zip", (int library, int id, int fileId, HttpContext context, CurrentAccess current, BookVisibilities visibilities,
            BookDownloads downloads, ServerTexts texts) => DownloadAsync(library, id, fileId, zipped: true, context, current, visibilities, downloads, texts)).RequireRight(Rights.Download);
    }

    /// <summary>
    /// A book's page. A merged book's address goes to the book it is in now (302: a merge can be undone); a book of
    /// another library the account browses, to its own address; a hidden book, or one of a library it doesn't, 404.
    /// </summary>
    private static async Task<IResult> BookAsync(int library, int id, HttpContext context, CurrentAccess current, AccessService accounts,
        BookVisibilities visibilities, BookQueries books, TagQueries tags, ServerTexts texts)
    {
        var state = await current.GetAsync();
        if (await LibraryNameAsync(state, library, accounts, context.RequestAborted) is not { } name)
            return Results.NotFound();
        var tenantId = state.TenantId!.Value;
        var visibility = await visibilities.GetAsync(new AccessSubject(tenantId, state.User!.Id), context.RequestAborted);
        var links = new EReaderLinks(context);
        if (await books.GetAsync(tenantId, visibility, id, context.RequestAborted) is not { } book)
            return Results.NotFound();
        if (book.MergedIntoId is { } kept)
            return Results.Redirect(links.Book(library, kept));
        if (book.LibraryId != library)
            return Opens(state, book.LibraryId) ? Results.Redirect(links.Book(book.LibraryId, id)) : Results.NotFound();

        var fields = await tags.FieldsAsync(tenantId, context.RequestAborted);
        var canDownload = state.Access.HasIn(Rights.Download, library);
        return new RazorComponentResult<BookPage>(new
        {
            Frame = await EReaderFrame.ForAsync(context),
            Up = new EPage.Crumb(name, links.Library(library)),
            Book = book,
            Fields = (IReadOnlyList<BookPage.FieldValues>)FieldsOf(book, fields, library, links, texts),
            Description = (IReadOnlyList<string>)Paragraphs(book.Description),
            Files = (IReadOnlyList<BookPage.FileRow>)book.Files.Select(f => new BookPage.FileRow(f,
                canDownload && !f.Missing ? links.File(library, id, f.Id) : null,
                canDownload && !f.Missing && OpdsFeed.Zipped(f.Format) ? links.ZippedFile(library, id, f.Id) : null)).ToList(),
            CanDownload = canDownload,
        });
    }

    /// <summary>
    /// The book's tags grouped by field in the tenant's order of fields (authors, series…), each value a link to its
    /// books (a series with the book's number), the plain tags last.
    /// </summary>
    private static List<BookPage.FieldValues> FieldsOf(BookDetail book, IReadOnlyList<FieldInfo> fields, int library, EReaderLinks links, ServerTexts texts)
    {
        var order = fields.Select((f, i) => (f.Key, i)).ToDictionary(p => p.Key, p => p.i, StringComparer.Ordinal);
        return book.Tags
            .GroupBy(t => t.Field)
            .OrderBy(g => g.Key is null ? int.MaxValue : order.GetValueOrDefault(g.Key, int.MaxValue - 1))
            .Select(g =>
            {
                var field = g.Key is null ? null : fields.FirstOrDefault(f => f.Key == g.Key);
                var title = g.Key is null ? texts["tags.title"] : field is null ? g.Key : texts.FieldName(field.Key, field.Name, field.BuiltIn);
                var values = g.Select(t => new BookPage.Value(
                    t.Number is { } n && g.Key == "series"
                        ? texts.Format("ereader.book.seriesNumber", ("series", t.Name), ("number", n.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)))
                        : t.Name,
                    links.Tag(library, t.Id))).ToList();
                return new BookPage.FieldValues(title, values);
            })
            .ToList();
    }

    /// <summary>
    /// The description in paragraphs, as the app shows it: plain text already (the importers strip markup, a
    /// paragraph a line), so a line is a paragraph and nothing is read as markup ("&lt;desert&gt;" in a text stays);
    /// spaces collapsed, cut at <see cref="MaxDescription"/> at a word. Razor encodes it.
    /// </summary>
    internal static List<string> Paragraphs(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return [];
        var paragraphs = new List<string>();
        var length = 0;
        foreach (var line in description.Split('\n'))
        {
            var paragraph = Spaces().Replace(line, " ").Trim();
            if (paragraph.Length == 0)
                continue;
            if (length + paragraph.Length > MaxDescription)
            {
                var room = MaxDescription - length;
                var cut = paragraph.LastIndexOf(' ', Math.Max(0, Math.Min(room, paragraph.Length - 1)));
                if (room > 40)
                    paragraphs.Add(paragraph[..(cut > room / 2 ? cut : room)].TrimEnd() + "…");
                else if (paragraphs.Count > 0)
                    paragraphs[^1] += "…";
                break;
            }
            paragraphs.Add(paragraph);
            length += paragraph.Length;
        }
        return paragraphs;
    }

    /// <summary>A file as it lies, or an FB2 zipped while it is sent (<paramref name="zipped"/>; other formats: 404). A merged book's old address still gives it.</summary>
    private static async Task<IResult> DownloadAsync(int library, int id, int fileId, bool zipped, HttpContext context, CurrentAccess current,
        BookVisibilities visibilities, BookDownloads downloads, ServerTexts texts)
    {
        var state = await current.GetAsync();
        if (!Opens(state, library))
            return Results.NotFound();
        var visibility = await visibilities.GetAsync(new AccessSubject(state.TenantId, state.User!.Id), context.RequestAborted);
        if (await downloads.FindAsync(state.TenantId!.Value, visibility, id, fileId, context.RequestAborted) is not { } target || target.LibraryId != library
            || (zipped && !OpdsFeed.Zipped(target.Format)))
            return Results.NotFound();
        if (!state.Access.HasIn(Rights.Download, library))
            return Results.Forbid();
        IResult result;
        if (zipped)
        {
            var (content, problem) = BookDownloads.Open(target);
            result = content is null ? BookEndpoints.FileProblem(problem) : new ZippedDownload(content, target.Name, target.Modified, target.FileId);
        }
        else
        {
            result = BookEndpoints.Download(context, target);
        }

        // The file isn't as the scan saw it: a plain page saying why (the app's texts), not the API's problem JSON.
        return result is ProblemHttpResult { ProblemDetails.Type: { } type } problemResult
            ? EReaderGuard.Page(await EReaderFrame.ForAsync(context), texts["ereader.file.title"], texts["error." + type], problemResult.StatusCode)
            : result;
    }

    [GeneratedRegex(@"[ \t\r\f\v\xA0]+")]
    private static partial Regex Spaces();
}
