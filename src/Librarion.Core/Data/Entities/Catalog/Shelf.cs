namespace Librarion.Core.Data;

/// <summary>
/// A personal shelf (agents/docs/features/shelves.md, D-7.9): an account's named list of books in its own order, in
/// the tenant it was made in (a server administrator's, in the tenant it worked in). Only its owner sees it; a book
/// shows in it only while the owner may see the book.
/// </summary>
public sealed class Shelf
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public int UserId { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime Created { get; set; }

    /// <summary>Its name or its books last changed.</summary>
    public DateTime Updated { get; set; }

    /// <summary>Rows in <see cref="ShelfBook"/>, kept as they change (a list of shelves never counts them).</summary>
    public int BookCount { get; set; }
}

/// <summary>A book on a <see cref="Shelf"/>, at a place in its order.</summary>
public sealed class ShelfBook
{
    public int ShelfId { get; set; }

    public int BookId { get; set; }

    public int TenantId { get; set; }

    /// <summary>The shelf's own order, ascending; spaced (<see cref="Catalog.Shelves.Gap"/>) so a move writes one row.</summary>
    public long Position { get; set; }

    /// <summary>When it was put on the shelf (the order by date added).</summary>
    public DateTime Added { get; set; }
}
