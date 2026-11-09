using System.Text.Json;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Merging;

/// <summary>
/// Books merged into one (<paramref name="TargetId"/> kept). Each single value comes from the book named for it (the
/// target when null): the title, the description, the rating, the cover.
/// </summary>
public sealed record BookMergeInput(int TargetId, IReadOnlyList<int> SourceIds, int? TitleFrom = null, int? DescriptionFrom = null, int? RatingFrom = null,
    int? CoverFrom = null);

/// <summary>
/// Duplicate books merged (stage 6 chunk 12, D19, agents/docs/features/merging.md#merging-books-built-stage-6-chunk-12):
/// the sources' files, links, identifiers and properties move to the kept book (a tag it has already stays as it is),
/// the single values chosen are copied and marked edited (re-reads keep them), counts move by delta, and each source
/// becomes a redirect (<see cref="Book.MergedIntoId"/>: its addresses answer with the kept book). Books of one library
/// only. Every source writes a <see cref="MergeLog"/> of kind <see cref="MergeKind.Book"/> with what moved and the kept
/// book's values before and after (the first source's log the parent of the others), for the undo (chunk 13,
/// <c>BookMerger.Undo.cs</c>). The duplicate groups follow.
/// </summary>
public sealed partial class BookMerger(TenantDb tenantDb, TimeProvider time)
{
    /// <summary>The most books merged at once: a duplicate group's size.</summary>
    public const int MaxBooks = DuplicateIndex.MaxGroup;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// What a book merge moved, what the kept book had before (<see cref="Target"/>) and after (<see cref="After"/>: a
    /// value the undo finds unchanged since goes back) (<see cref="MergeLog.Data"/>).
    /// </summary>
    public sealed record BookMergeData(
        IReadOnlyList<int> Files, IReadOnlyList<LinkData> Links, IReadOnlyList<ValueData> Identifiers, IReadOnlyList<ValueData> Properties, IReadOnlyList<int> Suppressed,
        TargetData Target, TargetData After);

    /// <param name="Moved">Given to the kept book; false when it had the tag already (dropped).</param>
    public sealed record LinkData(int TagId, TagSource Source, int? Ordinal, double? Number, bool Moved);

    /// <summary>An identifier (scheme, value) or a property (name, value) of the source; <paramref name="Moved"/>: the kept book lacked it.</summary>
    public sealed record ValueData(string Key, string Value, bool Moved);

    public sealed record TargetData(
        string? Title, string SortTitle, string SearchText, string? Description, byte? Rating, CoverSource CoverSource, string? CoverFile, CoverState CoverState,
        short? GeneratedTemplate, short? GeneratedVariant, BookEdits Edited);

    /// <summary>The libraries of the books, by id (rights are checked there); null when one isn't there, is merged or missing.</summary>
    public async Task<IReadOnlyDictionary<int, int>?> LibrariesAsync(int tenantId, IReadOnlyCollection<int> bookIds, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var ids = bookIds.Distinct().ToList();
        var books = await db.Books.Where(b => ids.Contains(b.Id) && b.MergedIntoId == null && b.MissingSince == null)
            .Select(b => new { b.Id, b.LibraryId }).ToDictionaryAsync(b => b.Id, b => b.LibraryId, cancellationToken);
        return books.Count == ids.Count ? books : null;
    }

    public async Task<AccessResult<bool>> MergeAsync(int tenantId, int? userId, BookMergeInput input, CancellationToken cancellationToken = default)
    {
        var sources = input.SourceIds.Distinct().ToList();
        if (sources.Count == 0 || sources.Count >= MaxBooks || sources.Contains(input.TargetId))
            return AccessResult<bool>.Invalid("sourceIds", "merge.books");
        var all = sources.Prepend(input.TargetId).ToList();
        if (new[] { input.TitleFrom, input.DescriptionFrom, input.RatingFrom, input.CoverFrom }.Any(from => from is { } id && !all.Contains(id)))
            return AccessResult<bool>.Invalid("choices", "merge.choice");

        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var books = await db.Books.Where(b => all.Contains(b.Id) && b.MergedIntoId == null && b.MissingSince == null).ToDictionaryAsync(b => b.Id, cancellationToken);
        if (books.Count != all.Count)
            return AccessResult<bool>.Missing();
        var target = books[input.TargetId];
        if (books.Values.Any(b => b.LibraryId != target.LibraryId))
            return AccessResult<bool>.Invalid("sourceIds", "merge.library");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var before = await TagLinks.SnapshotAsync(db, all, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var targetData = Values(target);
        var targetName = await NameAsync(db, target, cancellationToken);
        var moves = new List<(int SourceId, string SourceName, Func<TargetData, BookMergeData> Data, int Links)>();

        foreach (var sourceId in sources)
        {
            var source = books[sourceId];
            var sourceName = await NameAsync(db, source, cancellationToken);
            var files = await db.BookFiles.Where(f => f.BookId == sourceId).Select(f => f.Id).ToListAsync(cancellationToken);
            await db.BookFiles.Where(f => f.BookId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(f => f.BookId, input.TargetId), cancellationToken);

            // Links: a tag the kept book has stays as it is; the others move, after its own in ordered fields.
            var have = (await db.BookTags.Where(l => l.BookId == input.TargetId).Select(l => new { l.TagId, l.Ordinal }).ToListAsync(cancellationToken));
            var tags = have.Select(l => l.TagId).ToHashSet();
            var ordinal = have.Max(l => l.Ordinal) ?? -1;
            var links = await db.BookTags.Where(l => l.BookId == sourceId).Select(l => new { l.TagId, l.Source, l.Ordinal, l.Number }).ToListAsync(cancellationToken);
            var logged = links.Select(l => new LinkData(l.TagId, l.Source, l.Ordinal, l.Number, !tags.Contains(l.TagId))).ToList();
            await db.BookTags.Where(l => l.BookId == sourceId).ExecuteDeleteAsync(cancellationToken);
            db.BookTags.AddRange(logged.Where(l => l.Moved).Select(l => new BookTag
            {
                TenantId = tenantId, BookId = input.TargetId, TagId = l.TagId, Source = l.Source, Ordinal = l.Ordinal is { } o ? ordinal + 1 + o : null, Number = l.Number,
            }));

            var identifiers = await db.BookIdentifiers.Where(i => i.BookId == sourceId).Select(i => new { i.Scheme, i.Value }).ToListAsync(cancellationToken);
            var held = (await db.BookIdentifiers.Where(i => i.BookId == input.TargetId).Select(i => new { i.Scheme, i.Value }).ToListAsync(cancellationToken)).ToHashSet();
            var givenIds = identifiers.Select(i => new ValueData(i.Scheme, i.Value, !held.Contains(i))).ToList();
            await db.BookIdentifiers.Where(i => i.BookId == sourceId).ExecuteDeleteAsync(cancellationToken);
            db.BookIdentifiers.AddRange(givenIds.Where(i => i.Moved)
                .Select(i => new BookIdentifier { TenantId = tenantId, BookId = input.TargetId, Scheme = i.Key, Value = i.Value }));

            var properties = await db.BookProperties.Where(p => p.BookId == sourceId).Select(p => new { p.Name, p.Value }).ToListAsync(cancellationToken);
            var names = (await db.BookProperties.Where(p => p.BookId == input.TargetId).Select(p => p.Name).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
            var givenProperties = properties.Select(p => new ValueData(p.Name, p.Value, !names.Contains(p.Name))).ToList();
            await db.BookProperties.Where(p => p.BookId == sourceId).ExecuteDeleteAsync(cancellationToken);
            db.BookProperties.AddRange(givenProperties.Where(p => p.Moved)
                .Select(p => new BookProperty { TenantId = tenantId, BookId = input.TargetId, Name = p.Key, Value = p.Value }));

            var suppressed = await db.BookTagSuppressions.Where(s => s.BookId == sourceId).Select(s => s.TagId).ToListAsync(cancellationToken);
            await db.BookTagSuppressions.Where(s => s.BookId == sourceId).ExecuteDeleteAsync(cancellationToken);

            source.MergedIntoId = input.TargetId;
            source.Updated = now;
            // Saved before the next source: its links and identifiers are checked against what the kept book has now.
            await db.SaveChangesAsync(cancellationToken);
            moves.Add((sourceId, sourceName, after => new BookMergeData(files, logged, givenIds, givenProperties, suppressed, targetData, after), logged.Count(l => l.Moved)));
        }

        Choose(target, input.TitleFrom, books, (to, from) => { to.Title = from.Title; to.SortTitle = from.SortTitle; to.SearchText = from.SearchText; to.Edited |= BookEdits.Title; });
        Choose(target, input.DescriptionFrom, books, (to, from) => { to.Description = from.Description; to.Edited |= BookEdits.Description; });
        Choose(target, input.RatingFrom, books, (to, from) => { to.Rating = from.Rating; to.Edited |= BookEdits.Rating; });
        Choose(target, input.CoverFrom, books, (to, from) =>
        {
            // A cover in the books' files is looked for again among all of them now; an upload or a style is the user's.
            to.CoverSource = from.CoverSource;
            to.CoverFile = from.CoverFile;
            to.CoverState = CoverState.Unknown;
            to.GeneratedTemplate = from.GeneratedTemplate;
            to.GeneratedVariant = from.GeneratedVariant;
            to.Edited = from.CoverSource is CoverSource.Uploaded or CoverSource.Generated ? to.Edited | BookEdits.Cover : to.Edited & ~BookEdits.Cover;
        });
        target.Updated = now;
        // One log per source, the first the parent of the others: one undo puts the whole merge back.
        var afterData = Values(target);
        MergeLog? parent = null;
        foreach (var move in moves)
        {
            var log = new MergeLog
            {
                TenantId = tenantId, Kind = MergeKind.Book, ParentId = parent?.Id, UserId = userId, Created = now, SourceId = move.SourceId, SourceName = move.SourceName,
                TargetId = input.TargetId, TargetName = targetName, Links = move.Links, Completed = true, Data = JsonSerializer.Serialize(move.Data(afterData), Json),
            };
            db.MergeLogs.Add(log);
            if (parent is null)
            {
                await db.SaveChangesAsync(cancellationToken);
                parent = log;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var after = await TagLinks.SnapshotAsync(db, all, cancellationToken);
        await TagLinks.CountAsync(db, tenantId, before, after, cancellationToken);
        await db.Libraries.Where(l => l.Id == target.LibraryId).ExecuteUpdateAsync(s => s.SetProperty(l => l.BookCount, l => l.BookCount - sources.Count), cancellationToken);
        await TagLinks.SortColumnsAsync(db, [input.TargetId], cancellationToken);
        // The groups of every merged book computed again: the sources, without files or identifiers now, leave them.
        if (await DuplicateIndex.BuiltAsync(db, cancellationToken))
            await DuplicateIndex.RefreshAsync(db, all, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return AccessResult<bool>.Ok(true);
    }

    /// <summary>
    /// The book a merged one went into, followed through later merges (at most a few hops); the book itself when it
    /// wasn't merged; null when there is none.
    /// </summary>
    public static async Task<int?> CurrentAsync(LibrarionDbContext db, int bookId, CancellationToken cancellationToken)
    {
        var id = bookId;
        for (var hops = 0; hops < 10; hops++)
        {
            var from = id;
            var book = await db.Books.Where(b => b.Id == from).Select(b => new { b.MergedIntoId }).FirstOrDefaultAsync(cancellationToken);
            if (book is null)
                return null;
            if (book.MergedIntoId is not { } next)
                return id;
            id = next;
        }

        return null;
    }

    private static TargetData Values(Book book) => new(book.Title, book.SortTitle, book.SearchText, book.Description, book.Rating, book.CoverSource, book.CoverFile,
        book.CoverState, book.GeneratedTemplate, book.GeneratedVariant, book.Edited);

    private static void Choose(Book target, int? from, IReadOnlyDictionary<int, Book> books, Action<Book, Book> copy)
    {
        if (from is { } id && id != target.Id)
            copy(target, books[id]);
    }

    /// <summary>A book's name for the log: its title, else its first file's name, cut to the log's length.</summary>
    private static async Task<string> NameAsync(LibrarionDbContext db, Book book, CancellationToken cancellationToken)
    {
        var name = book.Title ?? await db.BookFiles.Where(f => f.BookId == book.Id).OrderBy(f => f.Id).Select(f => f.Path).FirstOrDefaultAsync(cancellationToken) ?? "";
        return name.Length > Tag.MaxNameLength ? name[..Tag.MaxNameLength] : name;
    }
}
