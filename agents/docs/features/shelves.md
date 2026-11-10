# Shelves

Code: `Core/Catalog/Shelves.cs` (the service), `Core/Data/Entities/Catalog/Shelf.cs` (`Shelf`, `ShelfBook`),
`Api/Endpoints/ShelfEndpoints.cs`; shelf rows in merges: `Core/Merging/BookMerger.cs` / `BookMerger.Undo.cs`
(`ShelfData`, `ShelvesBackAsync`). Tests: `ShelfTests` (both providers), `TenancyTests`. Decisions: [stage 7
plan](../development/stage-7-opds-plan.md) (D-7.9, the user's Q1 and Q10). Right: `account.shelves` ([rights](rights.md)).

## Built (stage 7 chunk 9): Core and routes

- **Personal:** a shelf belongs to an account in the tenant it was made in (`Shelf.TenantId`, `UserId`; a server
  administrator's in the tenant it works in), with the tenant filter on both tables. Only its owner reaches it:
  another account's shelf answers 404 everywhere (read, add, rename…). Names are its own among the owner's shelves
  (case not counted: `shelf.nameTaken`), 1–100 characters.
- **Caps** (cost bounds, D-7.9): shelves per account and tenant = the setting `shelves.maxPerAccount` (server, then
  tenant, never the account; default 100, 1–1,000 since the list of shelves is never paged; the user's answer,
  2026-11-10; lowering it removes no shelf, only refuses new ones: `shelf.tooMany`, `Shelves.MaxShelvesAsync`);
  `MaxBooks` 10,000 per shelf (`shelf.full`); add or remove at most `BookTagger.MaxSelection` (1,000) at once.
- **Order:** `ShelfBook.Position` (long), spaced by `Shelves.Gap` (1,024). Books go on after the last, a selection in
  the order given; one already there stays where it is (`ShelfAdded.Already`). A move (`PUT …/books/{id}/position`,
  `before` = the book to go before, none = the end) writes one row: the midpoint of its new neighbours; when they
  touch the shelf is renumbered (`Gap`, `2·Gap`…, at most `MaxBooks` rows), then the move is made.
- **Counts:** `Shelf.BookCount` kept as rows change (add, remove, merges and undo). Books deleted with their files
  (`CatalogWriter.RemoveEmptyBooksAsync`) or with their library (`AccessService.DeleteLibraryAsync`) count out first
  (`Shelves.BooksLeavingAsync`); the database's cascade then deletes the rows. The count is of rows: the owner may not
  see every one of them now.
- **Visibility:** only books the owner may see now are put on (others skipped as not there; none seen: 404) and
  listed (`BookQueries.VisibleAsync`: merged and missing books left out, libraries and content filters as on the
  library page). A book that becomes visible again shows again.
- **Listing by key** (`GET api/shelves/{id}/books`, `sort`): `shelf` (own order) walks `(ShelfId, Position, BookId)`,
  `added` (newest first) `(ShelfId, Added, BookId)`, each row tested against the visible books; `title` and `author`
  join the shelf's rows to the visible books and sort them (at most `MaxBooks`: never the library's title index).
  `library` narrows to one library (the feeds, chunk 11). Items through `BookQueries.ItemsOfAsync`.
- **Merges** (every account's shelves): the kept book takes a merged book's place on each shelf (same position and
  date added), or the place is dropped where the shelf had the kept book already (its count one fewer). Logged in
  `BookMergeData.Shelves` (null in older logs). **Undo** puts the merged book back in each place: a place the kept
  book took goes back (unless the kept book was taken off since), a dropped place comes back (count one more), a shelf
  deleted since is skipped. **Split:** the book keeps its shelves; the new book is on none.
- **Routes** (`account.shelves`, the tenant the session works in): `GET/POST api/shelves`, `GET/PUT/DELETE
  api/shelves/{id}`, `GET/POST api/shelves/{id}/books`, `POST api/shelves/{id}/books/remove`, `PUT
  api/shelves/{id}/books/{bookId}/position`, `GET api/books/{id}/shelves` (the owner's shelves holding a book).
- The test helper that used to be called `Shelf` (a test library on disk) is `TestLibrary` now: the entity took the name.

## Planned (stage 7)

- The pages (chunk 10): Add to shelf on the book page and in the selection bar, Shelves in the sidebar, a shelf's
  page (reorder by drag, remove, rename, delete).
- The feeds (chunk 11): the shelf list and each shelf's feed, in the shelf's order.
- Shared or public shelves: [next-version plan](../development/next-version-plan.md).
