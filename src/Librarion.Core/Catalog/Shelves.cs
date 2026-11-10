using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Catalog;

/// <summary>A shelf as listed: its stored count of books (the owner may not see every one of them now).</summary>
public sealed record ShelfInfo(int Id, string Name, int BookCount, DateTime Created, DateTime Updated);

/// <summary>A shelf's books in an order.</summary>
public enum ShelfSort
{
    /// <summary>The shelf's own order (as its owner arranged it; also its feed's).</summary>
    Shelf,
    Title,
    Author,
    /// <summary>Put on the shelf last, first.</summary>
    Added,
}

/// <summary>What adding books did: those put on the shelf, and those it had already.</summary>
public sealed record ShelfAdded(ShelfInfo Shelf, int Added, int Already);

/// <summary>
/// Personal shelves (agents/docs/features/shelves.md, D-7.9): an account's own named lists of books, in the tenant
/// they were made in, at most the tenant's setting <c>shelves.maxPerAccount</c> each; only the owner reaches them (another's shelf is not found). A shelf holds at most
/// <see cref="MaxBooks"/> books in its own order (spaced positions: a move writes one row, the whole shelf renumbered
/// only when two neighbours touch), listed by key in that order, by title, author or date added, always only the
/// books the owner may see now. Merges move shelf rows to the kept book and their undo moves them back (<c>BookMerger</c>).
/// </summary>
public sealed class Shelves(TenantDb tenantDb, Settings.SettingsService settings, TimeProvider time)
{

    /// <summary>Books a shelf may hold: what a renumbering or a sort by title reads at most.</summary>
    public const int MaxBooks = 10_000;

    /// <summary>The space between neighbours when books are added or renumbered.</summary>
    public const long Gap = 1024;

    public async Task<IReadOnlyList<ShelfInfo>> ListAsync(int tenantId, int userId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var shelves = await db.Shelves.Where(s => s.UserId == userId).Select(s => new ShelfInfo(s.Id, s.Name, s.BookCount, s.Created, s.Updated))
            .ToListAsync(cancellationToken);
        return shelves.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id).ToList();
    }

    public async Task<ShelfInfo?> GetAsync(int tenantId, int userId, int shelfId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        return await db.Shelves.Where(s => s.Id == shelfId && s.UserId == userId)
            .Select(s => new ShelfInfo(s.Id, s.Name, s.BookCount, s.Created, s.Updated)).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AccessResult<ShelfInfo>> CreateAsync(int tenantId, int userId, string? name, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var names = await db.Shelves.Where(s => s.UserId == userId).Select(s => s.Name).ToListAsync(cancellationToken);
        if (names.Count >= await MaxShelvesAsync(tenantId, cancellationToken))
            return AccessResult<ShelfInfo>.Invalid("name", "shelf.tooMany");
        if (Problem(name, names) is { } problem)
            return AccessResult<ShelfInfo>.Invalid("name", problem);

        var now = time.GetUtcNow().UtcDateTime;
        var shelf = new Shelf { TenantId = tenantId, UserId = userId, Name = name!.Trim(), Created = now, Updated = now };
        db.Shelves.Add(shelf);
        await db.SaveChangesAsync(cancellationToken);
        return AccessResult<ShelfInfo>.Ok(Info(shelf));
    }

    public async Task<AccessResult<ShelfInfo>> RenameAsync(int tenantId, int userId, int shelfId, string? name, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (await db.Shelves.FirstOrDefaultAsync(s => s.Id == shelfId && s.UserId == userId, cancellationToken) is not { } shelf)
            return AccessResult<ShelfInfo>.Missing();
        var names = await db.Shelves.Where(s => s.UserId == userId && s.Id != shelfId).Select(s => s.Name).ToListAsync(cancellationToken);
        if (Problem(name, names) is { } problem)
            return AccessResult<ShelfInfo>.Invalid("name", problem);
        shelf.Name = name!.Trim();
        shelf.Updated = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return AccessResult<ShelfInfo>.Ok(Info(shelf));
    }

    /// <summary>The shelf goes, with its rows (never the books).</summary>
    public async Task<bool> DeleteAsync(int tenantId, int userId, int shelfId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        return await db.Shelves.Where(s => s.Id == shelfId && s.UserId == userId).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// Puts books on the shelf, after its last, in the order given; only books the owner may see now (others are
    /// skipped as not there), at most <see cref="BookTagger.MaxSelection"/> at once; those it has already stay where they are.
    /// </summary>
    public async Task<AccessResult<ShelfAdded>> AddAsync(int tenantId, int userId, BookVisibility visibility, int shelfId, IReadOnlyCollection<int> bookIds,
        CancellationToken cancellationToken = default)
    {
        var ids = bookIds.Distinct().ToList();
        if (ids.Count == 0 || ids.Count > BookTagger.MaxSelection)
            return AccessResult<ShelfAdded>.Invalid("bookIds", "shelf.selection");
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (await db.Shelves.FirstOrDefaultAsync(s => s.Id == shelfId && s.UserId == userId, cancellationToken) is not { } shelf)
            return AccessResult<ShelfAdded>.Missing();

        var visible = await BookQueries.VisibleAsync(db, visibility, null, cancellationToken, ids.Count);
        var found = (await visible.Where(b => ids.Contains(b.Id)).Select(b => b.Id).ToListAsync(cancellationToken)).ToHashSet();
        var already = (await db.ShelfBooks.Where(s => s.ShelfId == shelfId && ids.Contains(s.BookId)).Select(s => s.BookId).ToListAsync(cancellationToken)).ToHashSet();
        var fresh = ids.Where(id => found.Contains(id) && !already.Contains(id)).ToList();
        if (fresh.Count == 0)
            return found.Count == 0 ? AccessResult<ShelfAdded>.Missing() : AccessResult<ShelfAdded>.Ok(new ShelfAdded(Info(shelf), 0, already.Count));
        if (shelf.BookCount + fresh.Count > MaxBooks)
            return AccessResult<ShelfAdded>.Invalid("bookIds", "shelf.full");

        var now = time.GetUtcNow().UtcDateTime;
        var last = await db.ShelfBooks.Where(s => s.ShelfId == shelfId).MaxAsync(s => (long?)s.Position, cancellationToken) ?? 0;
        db.ShelfBooks.AddRange(fresh.Select((id, i) => new ShelfBook { TenantId = tenantId, ShelfId = shelfId, BookId = id, Position = last + Gap * (i + 1), Added = now }));
        shelf.BookCount += fresh.Count;
        shelf.Updated = now;
        await db.SaveChangesAsync(cancellationToken);
        return AccessResult<ShelfAdded>.Ok(new ShelfAdded(Info(shelf), fresh.Count, already.Count));
    }

    /// <summary>Takes books off the shelf (those it doesn't have are ignored).</summary>
    public async Task<AccessResult<ShelfInfo>> RemoveAsync(int tenantId, int userId, int shelfId, IReadOnlyCollection<int> bookIds, CancellationToken cancellationToken = default)
    {
        var ids = bookIds.Distinct().ToList();
        if (ids.Count == 0 || ids.Count > BookTagger.MaxSelection)
            return AccessResult<ShelfInfo>.Invalid("bookIds", "shelf.selection");
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (await db.Shelves.FirstOrDefaultAsync(s => s.Id == shelfId && s.UserId == userId, cancellationToken) is not { } shelf)
            return AccessResult<ShelfInfo>.Missing();
        var removed = await db.ShelfBooks.Where(s => s.ShelfId == shelfId && ids.Contains(s.BookId)).ExecuteDeleteAsync(cancellationToken);
        if (removed > 0)
        {
            shelf.BookCount = Math.Max(0, shelf.BookCount - removed);
            shelf.Updated = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
        }
        return AccessResult<ShelfInfo>.Ok(Info(shelf));
    }

    /// <summary>
    /// Moves a book of the shelf to just before <paramref name="beforeBookId"/> (another of its books), or to the end
    /// (null): one row written, unless its neighbours touch (then the shelf is renumbered first).
    /// </summary>
    public async Task<AccessResult<bool>> MoveAsync(int tenantId, int userId, int shelfId, int bookId, int? beforeBookId, CancellationToken cancellationToken = default)
    {
        if (beforeBookId == bookId)
            return AccessResult<bool>.Ok(true);
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (await db.Shelves.FirstOrDefaultAsync(s => s.Id == shelfId && s.UserId == userId, cancellationToken) is not { } shelf)
            return AccessResult<bool>.Missing();
        var rows = db.ShelfBooks.Where(s => s.ShelfId == shelfId);
        if (await rows.FirstOrDefaultAsync(s => s.BookId == bookId, cancellationToken) is not { } moving)
            return AccessResult<bool>.Missing();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            long? position;
            if (beforeBookId is not { } before)
            {
                position = (await rows.MaxAsync(s => (long?)s.Position, cancellationToken) ?? 0) + Gap;
            }
            else
            {
                if (await rows.Where(s => s.BookId == before).Select(s => (long?)s.Position).FirstOrDefaultAsync(cancellationToken) is not { } next)
                    return AccessResult<bool>.Missing();
                var previous = await rows.Where(s => s.Position < next && s.BookId != bookId).MaxAsync(s => (long?)s.Position, cancellationToken);
                position = previous is null ? next - Gap : next - previous.Value >= 2 ? previous.Value + (next - previous.Value) / 2 : null;
            }

            if (position is { } found)
            {
                moving.Position = found;
                shelf.Updated = time.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
                return AccessResult<bool>.Ok(true);
            }

            // The neighbours touch: spread the shelf out again (at most MaxBooks rows), then place it.
            var ordered = await rows.OrderBy(s => s.Position).ThenBy(s => s.BookId).ToListAsync(cancellationToken);
            for (var i = 0; i < ordered.Count; i++)
                ordered[i].Position = Gap * (i + 1);
            await db.SaveChangesAsync(cancellationToken);
        }

        return AccessResult<bool>.Conflict("shelf.order");
    }

    /// <summary>
    /// A page of the shelf's books the owner may see now (in <paramref name="libraryId"/> only, when given), by key: the
    /// shelf's own order and date added walk its rows' indexes; title and author sort the shelf's books (at most
    /// <see cref="MaxBooks"/>). Null when the shelf isn't the account's.
    /// </summary>
    public async Task<Page<BookListItem>?> BooksAsync(int tenantId, int userId, BookVisibility visibility, int shelfId, ShelfSort sort, string? after, int limit,
        CancellationToken cancellationToken = default, int? libraryId = null)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (!await db.Shelves.AnyAsync(s => s.Id == shelfId && s.UserId == userId, cancellationToken))
            return null;
        limit = Math.Clamp(limit, 1, BookQueries.MaxLimit);
        if (libraryId is { } only && !visibility.Libraries.ContainsKey(only))
            return new Page<BookListItem>([], null);
        var visible = await BookQueries.VisibleAsync(db, visibility, libraryId, cancellationToken, limit);
        var rows = db.ShelfBooks.Where(s => s.ShelfId == shelfId);
        var cursor = KeysetCursor.Decode(after);

        List<int> ids;
        string? next = null;
        switch (sort)
        {
            case ShelfSort.Shelf:
            {
                if (cursor?.Number is { } position)
                    rows = rows.Where(s => s.Position > position || (s.Position == position && s.BookId > cursor.Id));
                var page = await rows.OrderBy(s => s.Position).ThenBy(s => s.BookId).Where(s => visible.Any(b => b.Id == s.BookId))
                    .Select(s => new { s.BookId, s.Position }).Take(limit + 1).ToListAsync(cancellationToken);
                if (page.Count > limit)
                    next = new KeysetCursor(null, page[limit - 1].Position, page[limit - 1].BookId).Encode();
                ids = page.Take(limit).Select(p => p.BookId).ToList();
                break;
            }
            case ShelfSort.Added:
            {
                if (cursor?.Number is { } ticks)
                {
                    var added = new DateTime(ticks, DateTimeKind.Utc);
                    rows = rows.Where(s => s.Added < added || (s.Added == added && s.BookId < cursor.Id));
                }
                var page = await rows.OrderByDescending(s => s.Added).ThenByDescending(s => s.BookId).Where(s => visible.Any(b => b.Id == s.BookId))
                    .Select(s => new { s.BookId, s.Added }).Take(limit + 1).ToListAsync(cancellationToken);
                if (page.Count > limit)
                    next = new KeysetCursor(null, page[limit - 1].Added.Ticks, page[limit - 1].BookId).Encode();
                ids = page.Take(limit).Select(p => p.BookId).ToList();
                break;
            }
            default:
            {
                // From the shelf's rows (a few thousand at most), never the library's title index.
                var books = rows.Join(visible, s => s.BookId, b => b.Id, (s, b) => b);
                var order = sort == ShelfSort.Author ? BookSort.Author : BookSort.Title;
                var page = await BookQueries.Order(books, order, cursor).Select(b => new { b.Id, b.SortTitle, b.SortAuthor }).Take(limit + 1).ToListAsync(cancellationToken);
                if (page.Count > limit)
                {
                    var last = page[limit - 1];
                    next = new KeysetCursor(order == BookSort.Author ? last.SortAuthor : last.SortTitle, null, last.Id).Encode();
                }
                ids = page.Take(limit).Select(p => p.Id).ToList();
                break;
            }
        }

        var items = (await BookQueries.ItemsOfAsync(db, ids, cancellationToken)).ToDictionary(i => i.Id);
        return new Page<BookListItem>(ids.Where(items.ContainsKey).Select(id => items[id]).ToList(), next);
    }

    /// <summary>The account's shelves holding the book (the book page ticks them).</summary>
    public async Task<IReadOnlyList<int>> ShelvesOfAsync(int tenantId, int userId, int bookId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        return await db.ShelfBooks.Where(s => s.BookId == bookId && db.Shelves.Any(x => x.Id == s.ShelfId && x.UserId == userId))
            .Select(s => s.ShelfId).OrderBy(id => id).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Before books are deleted (their rows then go with them, by the database): the shelves holding them count them
    /// out. <paramref name="leaving"/> are the rows that will go.
    /// </summary>
    public static async Task BooksLeavingAsync(LibrarionDbContext db, IQueryable<ShelfBook> leaving, CancellationToken cancellationToken)
    {
        var counts = await leaving.GroupBy(s => s.ShelfId).Select(g => new { ShelfId = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        foreach (var count in counts)
            await db.Shelves.Where(s => s.Id == count.ShelfId).ExecuteUpdateAsync(s => s.SetProperty(x => x.BookCount, x => x.BookCount - count.Count), cancellationToken);
    }

    /// <summary>The tenant's <see cref="Settings.SettingCatalog.MaxShelves"/> (the server's when the tenant sets none).</summary>
    public async Task<int> MaxShelvesAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        var states = await settings.GetAsync(new Settings.SettingOwner(SettingLevel.Tenant, tenantId), tenantId, cancellationToken);
        return states?.FirstOrDefault(s => s.Key == Settings.SettingCatalog.MaxShelves)?.Value is { ValueKind: System.Text.Json.JsonValueKind.Number } value
               && value.TryGetInt32(out var max)
            ? max
            : 100;
    }

    private static string? Problem(string? name, IReadOnlyList<string> others)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > LibrarionDbContext.MaxNameLength)
            return "shelf.nameInvalid";
        return others.Any(o => string.Equals(o, trimmed, StringComparison.CurrentCultureIgnoreCase)) ? "shelf.nameTaken" : null;
    }

    private static ShelfInfo Info(Shelf shelf) => new(shelf.Id, shelf.Name, shelf.BookCount, shelf.Created, shelf.Updated);
}
