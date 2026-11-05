# The book page

Code: `app/books/book-page.ts` (`BookPage`, route `books/:id`, loaded on first use), `styles/book.css`; `Api.book`,
`bookDuplicates`, `fileUrl` in `core/api.service.ts`; `I18n.size`, `date`. Server: `GET api/books/{id}`
(`BookQueries.GetAsync`, `canDownload`, `canTag`, `canEdit` set by the route), `PUT api/books/{id}` (below), `GET api/books/{id}/duplicates`
(`BookQueries.DuplicatesAsync`), downloads `GET api/books/{id}/files/{fileId}` ([archives](archives.md)). Tests
`book-page.spec.ts` (client), `BookPageTests` (both providers), e2e `book.spec.ts` (every device, `@smoke`),
`a11y.spec.ts` (axe). D-5.6 under [Decisions](#decisions-stage-5-confirmed-by-the-user-2026-11-01).

- Every card and row of `BookList` is a link (`a.book-link` inside `li.book`: the window measures the `li`).
- Shows: cover `l`; the library's name only when the session sees several; the title (italic with a hint when it is
  the file name); authors and series (with number) as links to their tag pages; the rating (0–10 stored, shown "4.5
  of 5"); the other fields grouped in the server's order as chips (field name singular for one value, plural for
  several; `language:` values by name), plain tags last; the description as plain text (`pre-line`: imports strip
  HTML); Files; Details (added, identifiers by `book.scheme.<key>` else the raw scheme, properties); possible
  duplicates.
- **Download:** a plain `<a href download>` (a GET needs no `X-Librarion-Request`): the head's button gives the first
  file not missing, each file row its own. Shown only with `canDownload` (`library.download` in the book's library);
  else a note says so. Missing files are listed, never offered.
- A merged book (`mergedIntoId`) goes to the kept one, replacing the address. 404 shows "Not found"; other errors
  Try again. The duplicates are asked after the book; their failure only leaves the panel out.
- **Back:** browser back when the page was opened from the app (`Router.navigated` when constructed), else the
  library. The list's place comes back from `BrowseState` ([browse](browse.md)).

## Editing a book (built, stage 6 chunk 6, D-6.3)

Code: `books/book-editor.ts` (`BookEditor`, a wide sheet), styles at the end of `styles/book.css`; `Api.editBook`;
server `Core/Catalog/BookEditor.cs`, `PUT api/books/{id}` (`library.edit` for columns and fields, `library.tag` for
plain tags, both in the book's library; `canTag` / `canEdit` on `GET`); tests `book-editor.spec.ts`, `BookEditTests`
(both providers), e2e `book-edit.spec.ts` (every device; dragged with a mouse, Move up on phones), axe on the open
editor.

- **Edit** on the page with either right; the editor shows what the caller may change: title, then Authors and
  Series always, each field the book has values of, **Add a field** for the others, the plain tags last, description,
  rating (0–10 as 0–5 stars by halves, 0 none).
- A field's values one per row: a handle to drag (pointer events with capture: mouse and touch alike), Move up /
  down (keyboard), Remove, a series' number in place; plain tags as chips. Values come from the shared
  [picker](tags.md#the-tag-picker-built-stage-6-chunk-5) scoped to the field, with Create (made on save by name: found
  in the field first, else `TagService.CreateAsync`).
- **Only what changed is sent:** a field whose list didn't change is left out, so its links stay `Imported`. A saved
  field's links all become `Manual` with `Ordinal` = their order (the order and numbers then survive re-imports);
  every tag taken off writes a `BookTagSuppression`; giving it back deletes it. `Rule` links are never touched.
- Columns that differ set `Books.Edited` flags (`Title`, `Description`, `Rating`); the title's sort key and search
  text follow (the FTS index by its triggers / generated column). Counts move by delta (`TagLinks.SnapshotAsync` /
  `CountAsync`), then `TagLinks.SortColumnsAsync` sets the sort author, series and now also the work key.
- `Books.Updated` is set, and list items and the book carry `coverVersion` (its ticks): covers are cached by the
  browser for a day at their address, so `coverUrl(id, size, version)` adds `?v=`; without it a generated cover kept
  the old title after a rename (seen in the e2e).
- Re-reads and re-imports (`CatalogWriter.ReplaceAsync` / `Apply`): edited columns kept, suppressed tags skipped,
  imported duplicates of manual links skipped (`seen`), and books with manual links get `SortColumnsAsync` after the
  batch (else the imported first author would win the sort author).
- Known limits: no "back to the imported value" yet (clear the flag by hand); merging a tag deletes its suppressions
  (cascade) instead of moving them to the target, so a re-import can bring a merged name back; nothing is written into
  the book's files or Calibre (D-6.3).
- Narrow head: `.book-main` is a size container; below 400 px of its own width a field's label goes above its values
  (`@container`, placed after the `.book-field` rule or the base rule wins). Beside the cover on a landscape phone it
  has ~200 px: the 9rem label left the values 44 px and "Fantasy" spilled past the screen with CI's wider Linux font
  (DejaVu Sans; Verdana with a little letter spacing reproduces it on Windows). Chips there also wrap inside
  (`white-space: normal`).
- e2e downloads: WebKit on Linux saves "Sand Messiah.fb2" as "Sand_Messiah.fb2"; `book.spec.ts` compares the name in the
  server's `Content-Disposition` and allows the browser's `_`.

## Possible duplicates (read only until merging, [merging](merging.md))

- Computed on demand per book, no tables: other visible books with a file of the same size and CRC-32 (`File`; another
  library's: copies in one library are merged at import), the same identifier (`Identifier` with its scheme; `inpx`
  left out, its numbers are per collection), the same `WorkKey` (`Work`). Strongest reason first, then title; at
  most 20.
- Each key is one seek on its index (`BookFiles (TenantId, Size, Crc32)`, `BookIdentifiers (TenantId, Scheme, Value)`,
  `Books (TenantId, WorkKey)`), at most 20 files and 20 identifiers per book, at most `MaxGroup` (50) rows read per key:
  a key shared by 50 books or more (this one counted) is noise ("Notes" by nobody) and ignored. Then the candidates
  go through the caller's visibility (libraries, content filters): bounded by the book, never by the catalog.
- Loose files have no CRC at import, so two identical loose files are only found by identifier or work.

## Decisions (stage 5, confirmed by the user 2026-11-01)

- **D-5.6 Book page:** cover, title, authors, series and number, fields as chips linking to their pages, description,
  properties (imported custom columns), copies and formats with **Download** (Read comes with the reader stage),
  possible duplicates (from the merge work, read only here).
