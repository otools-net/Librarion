using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Catalog;

/// <summary>A book's columns as a user sets them; each that differs from the book's is written and marked edited.</summary>
public sealed record BookColumnsEdit(string? Title, string? Description, byte? Rating);

/// <summary>A tag a field is to have: an existing one by <paramref name="Id"/>, or by <paramref name="Name"/> (found in the field, else made).</summary>
public sealed record BookTagEdit(int? Id = null, string? Name = null, double? Number = null);

/// <summary>Every tag of one field of the book (<paramref name="Field"/> null: its plain tags), in order (authors).</summary>
public sealed record BookFieldEdit(string? Field, IReadOnlyList<BookTagEdit> Tags);

/// <summary>What a save of the book editor changes; a part left null stays as it is.</summary>
public sealed record BookEdit(BookColumnsEdit? Columns = null, IReadOnlyList<BookFieldEdit>? Fields = null);

/// <summary>
/// A book edited on its page (stage 6, chunk 6, D-6.3): its title, description and rating, and the tags of a field or its
/// plain tags set as a whole, in order. What a user sets stays through re-reads and re-imports: edited columns are
/// flagged (<see cref="Book.Edited"/>), a saved field's links become <see cref="TagSource.Manual"/>, a tag taken off
/// is suppressed (<see cref="BookTagSuppression"/>). Counts move by delta, the sort columns and the work key follow
/// (<see cref="TagLinks.SortColumnsAsync"/>), the search text with the title. Nothing is written into the book's files.
/// </summary>
public sealed class BookEditor(TenantDb tenantDb, TagService tags, TimeProvider time)
{
    public const int MaxTags = 100;
    public const int MaxDescriptionLength = 20_000;

    /// <summary>The library of the book (its rights are checked there), or null when there is none.</summary>
    public async Task<int?> LibraryOfAsync(int tenantId, int bookId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        return await db.Books.Where(b => b.Id == bookId && b.MergedIntoId == null).Select(b => (int?)b.LibraryId).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AccessResult<bool>> SaveAsync(int tenantId, int bookId, BookEdit edit, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var book = await db.Books.FirstOrDefaultAsync(b => b.Id == bookId && b.MergedIntoId == null, cancellationToken);
        if (book is null)
            return AccessResult<bool>.Missing();
        if (edit.Columns is { } columns && Check(columns) is { } invalid)
            return invalid;

        // Each field's tags resolved first: names found in the field or made (with the tag service's rules).
        var fieldIds = new Dictionary<string, int>();
        var wanted = new List<(int? FieldId, List<(int TagId, double? Number)> Tags)>();
        foreach (var field in edit.Fields ?? [])
        {
            if (field.Tags.Count > MaxTags)
                return AccessResult<bool>.Invalid("tags", "tags.tooMany");
            int? fieldId = null;
            if (field.Field is { } key)
            {
                if (!fieldIds.TryGetValue(key, out var id))
                {
                    id = await db.Tags.Where(t => t.FieldKey == key).Select(t => t.Id).FirstOrDefaultAsync(cancellationToken);
                    if (id == 0)
                        return AccessResult<bool>.Invalid("field", "field.unknown");
                    fieldIds[key] = id;
                }

                fieldId = id;
            }

            var resolved = new List<(int, double?)>();
            foreach (var tag in field.Tags)
            {
                if (tag.Number is { } number && (double.IsNaN(number) || double.IsInfinity(number) || number < 0 || number > 1_000_000))
                    return AccessResult<bool>.Invalid("number", "number.invalid");
                int tagId;
                if (tag.Id is { } id)
                {
                    if (!await db.Tags.AnyAsync(t => t.Id == id && t.FieldId == fieldId && t.FieldKey == null, cancellationToken))
                        return AccessResult<bool>.Invalid("tags", "tag.unknown");
                    tagId = id;
                }
                else
                {
                    var name = tag.Name?.Trim() ?? "";
                    if (name.Length == 0)
                        return AccessResult<bool>.Invalid("tags", "name.required");
                    var normalized = TagNames.Normalize(name);
                    var found = await db.Tags.Where(t => t.FieldId == fieldId && t.FieldKey == null && t.NormalizedName == normalized)
                        .OrderBy(t => t.Id).Select(t => (int?)t.Id).FirstOrDefaultAsync(cancellationToken);
                    if (found is null)
                    {
                        var made = await tags.CreateAsync(tenantId, new TagInput(name, FieldId: fieldId), cancellationToken);
                        if (made.Status != AccessStatus.Ok)
                            return made.As<bool>();
                        found = made.Value!.Id;
                    }

                    tagId = found.Value;
                }

                if (resolved.All(r => r.Item1 != tagId))
                    resolved.Add((tagId, tag.Number));
            }

            wanted.Add((fieldId, resolved));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var before = await TagLinks.SnapshotAsync(db, [bookId], cancellationToken);
        if (edit.Columns is { } set)
            Columns(book, set, await db.BookFiles.Where(f => f.BookId == bookId).OrderBy(f => f.Id).Select(f => f.Path).FirstOrDefaultAsync(cancellationToken));
        foreach (var (fieldId, list) in wanted)
            await FieldAsync(db, tenantId, bookId, fieldId, list, cancellationToken);
        // Changed: its cover's address changes too (a generated cover shows the title and authors).
        book.Updated = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        var after = await TagLinks.SnapshotAsync(db, [bookId], cancellationToken);
        await TagLinks.CountAsync(db, tenantId, before, after, cancellationToken);
        await TagLinks.SortColumnsAsync(db, [bookId], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AccessResult<bool>.Ok(true);
    }

    private static AccessResult<bool>? Check(BookColumnsEdit columns)
    {
        if (columns.Title?.Trim().Length > Book.MaxTitleLength)
            return AccessResult<bool>.Invalid("title", "title.tooLong");
        if (columns.Description?.Length > MaxDescriptionLength)
            return AccessResult<bool>.Invalid("description", "description.tooLong");
        if (columns.Rating is > 10)
            return AccessResult<bool>.Invalid("rating", "rating.invalid");
        return null;
    }

    /// <summary>The columns that differ from what is set, marked edited; the title's sort key and search text with it.</summary>
    private static void Columns(Book book, BookColumnsEdit set, string? path)
    {
        var title = set.Title?.Trim() is { Length: > 0 } t ? t : null;
        if (title != book.Title)
        {
            var stem = FileTitles.FromPath(path ?? "");
            book.Title = title;
            book.SortTitle = BookTitles.SortKey(title ?? stem);
            book.SearchText = Search.SearchText.ForBook(title, stem);
            book.Edited |= BookEdits.Title;
        }

        var description = set.Description?.Trim() is { Length: > 0 } d ? d : null;
        if (description != book.Description)
        {
            book.Description = description;
            book.Edited |= BookEdits.Description;
        }

        var rating = set.Rating is 0 ? null : set.Rating;
        if (rating != book.Rating)
        {
            book.Rating = rating;
            book.Edited |= BookEdits.Rating;
        }
    }

    /// <summary>
    /// The book's tags of the field set to <paramref name="wanted"/>, in order: every link of the field becomes manual
    /// (the user owns it now, its order and numbers kept by re-imports); a tag taken off is suppressed, one given back
    /// is not anymore.
    /// </summary>
    private static async Task FieldAsync(LibrarionDbContext db, int tenantId, int bookId, int? fieldId, List<(int TagId, double? Number)> wanted,
        CancellationToken cancellationToken)
    {
        var current = await db.BookTags.Where(l => l.BookId == bookId && l.Tag!.FieldId == fieldId && l.Tag.FieldKey == null && l.Source != TagSource.Rule)
            .Select(l => l.TagId).Distinct().ToListAsync(cancellationToken);
        var keep = wanted.Select(w => w.TagId).ToHashSet();
        var removed = current.Where(id => !keep.Contains(id)).ToList();

        await db.BookTags.Where(l => l.BookId == bookId && l.Tag!.FieldId == fieldId && l.Tag.FieldKey == null && l.Source != TagSource.Rule)
            .ExecuteDeleteAsync(cancellationToken);
        var ordered = fieldId is not null;
        db.BookTags.AddRange(wanted.Select((w, i) => new BookTag
        {
            TenantId = tenantId, BookId = bookId, TagId = w.TagId, Source = TagSource.Manual, Ordinal = ordered ? i : null, Number = w.Number,
        }));

        var given = wanted.Select(w => w.TagId).ToList();
        await db.BookTagSuppressions.Where(s => s.BookId == bookId && given.Contains(s.TagId)).ExecuteDeleteAsync(cancellationToken);
        var already = await db.BookTagSuppressions.Where(s => s.BookId == bookId && removed.Contains(s.TagId)).Select(s => s.TagId).ToListAsync(cancellationToken);
        db.BookTagSuppressions.AddRange(removed.Except(already).Select(id => new BookTagSuppression { TenantId = tenantId, BookId = bookId, TagId = id }));
    }
}
