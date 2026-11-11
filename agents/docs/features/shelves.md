# Shelves

Code: `Core/Catalog/Shelves.cs` (the service), `Core/Data/Entities/Catalog/Shelf.cs` (`Shelf`, `ShelfBook`),
`Api/Endpoints/ShelfEndpoints.cs`; the client: `core/shelf-store.ts`, `app/shelves/` (pages, `ShelfPicker`),
`browse/book-list.ts` with `shelf`, `browse/shelf-order.ts`, `browse/shelf-move.ts`; shelf rows in merges: `Core/Merging/BookMerger.cs` / `BookMerger.Undo.cs`
(`ShelfData`, `ShelvesBackAsync`). Tests: `ShelfTests` (both providers), `TenancyTests`, `shelves/shelves.spec.ts`, e2e `shelves.spec.ts`. Decisions: [stage 7
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
  or a selection at once, in the order given: `POST …/books/move` with `bookIds`; `before` = the book to go before,
  not among them, none = the end) writes one row per book, spread over the gap before `before`; when the gap is
  narrower than the books moved the shelf is renumbered (`Gap`, `2·Gap`…, at most `MaxBooks` rows; `Gap` is wider
  than a selection, so one renumbering always makes room), then the move is made.
- **Counts:** `Shelf.BookCount` kept as rows change (add, remove, merges and undo). Books deleted with their files
  (`CatalogWriter.RemoveEmptyBooksAsync`) or with their library (`AccessService.DeleteLibraryAsync`) count out first
  (`Shelves.BooksLeavingAsync`); the database's cascade then deletes the rows. The count is of rows: the owner may not
  see every one of them now.
- **Visibility:** only books the owner may see now are put on (others skipped as not there; none seen: 404) and
  listed (`BookQueries.VisibleAsync`: merged and missing books left out, libraries and content filters as on the
  library page). A book that becomes visible again shows again.
- **Listing by key** (`GET api/shelves/{id}/books`, `sort`): `shelf` (own order) walks `(ShelfId, Position, BookId)`
  from where the cursor's book **is now** (its encoded position only when it was taken off since), so a move or a
  renumbering between two pages repeats nothing and skips nothing (the client drops a book it has already),
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

## Built (stage 7 chunk 10): the pages

- **`ShelfStore`** (root): the account's shelves for the sidebar, the Shelves page and Add to shelf; loaded once per
  account and tenant (the shell, when the session has `account.shelves`; cleared without it), kept in step by each
  change made in the client (`put` / `remove`); the Shelves page asks again (counts move with merges, other sessions).
- **Add to shelf** (`ShelfPicker`, a `Sheet`): a box per shelf; for one book ticked from `api/books/{id}/shelves`, for
  a selection none ticked. A box adds or removes at once (no Save), toasts the result; **New shelf** makes one and
  puts the books on it. On the book page (`book.shelves`) and in the selection bar.
- **Selection** in `BookList` now opens with `library.tag` **or** `account.shelves` (`canSelect`); Tags shows only
  with `library.tag`, Add to shelf only with `account.shelves`. The bar's labels are icons only under 480 px (the
  `tool-label` rule of the list's tools) so it fits 360 px.
- **The shelf's page** (`/shelves/<id>/<name>`, `shelves` reserved in `RESERVED_KEYS`; the name follows renames by a
  `replaceUrl`) is `BookList` with `shelf` (no library: books of every library; no Tag all, no Refine), sorts
  `shelf`, `title`, `author`, `added` (texts `shelf.sort.*`, remembered per browser as the scope `shelf`), Rename (a
  `Sheet`), Delete (`ConfirmService`, then `/shelves`). Its count reads the store, so it follows Remove from shelf.
- **Reordering** (only in the `shelf` sort, `ordering`): a grip button per card (`.book-move`, top right, always
  shown: touch has no hover). **Drag** (`ShelfDrag`): pointer events for mouse, pen and finger alike, `touch-action:
  none` on the grip; the window listens while dragging (a grip drawn out of the list's window ends nothing), the drop
  place is the half of the card under the pointer (left / right in the grid, top / bottom in the list), the page
  scrolls near the window's edges, Escape cancels; the click that ends a drag opens nothing (`consumeClick`).
  **Keys** on the grip: arrows one place, Home, End. **Click** on the grip: Move (`ShelfMoveDialog`: start, up,
  down, end, its place read out; WCAG 2.5.7's single-pointer way). A selected book moves with its selection, in the
  list's order (`movingWith`).
- **A move shows at once** (`movedLocally`), then `POST …/books/move`; a refusal puts the list back (toast). Books
  moved to the end leave a list not yet whole (they come with the last page); moving after the last loaded book
  loads the next page first, to find the book to go before. An announcement (`role="status"`) says the new place.
- **Gotcha:** the snapshot key of `BookList` (`books|library|shelf|tag|sort|q|folder`) gained the shelf: a test
  reading a kept list must use the new key.

## Planned (stage 7)

- The feeds (chunk 11): the shelf list and each shelf's feed, in the shelf's order.
- Shared or public shelves: [next-version plan](../development/next-version-plan.md).
