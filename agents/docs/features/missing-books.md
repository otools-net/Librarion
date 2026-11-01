# Missing books: kept, found again, removed only when a user accepts

Code: `Books.MissingSince`, `BookFiles.MissingSince`, `Archives.MissingSince` (`Data/Entities/Catalog/Book.cs`, partial
indexes in `LibrarionDbContext.Catalog.cs`), `Core/Import/LibraryScanner` (`GoneAsync`, `FoundAgainAsync`,
`RelinkAsync`), `CatalogWriter` (`MarkMissingAsync`, `ReturnAsync`), `Core/Import/MissingBooks` (list, retention,
removal), task kind `RemoveMissing`, routes in `Api/Endpoints/TaskEndpoints.cs`, client `admin/library-panel.ts`.
Tests: `MissingBooksTests`, `LibraryScannerTests`, `admin.spec.ts`. Ported from SFE's missing files and relink
(`C:\workspace\SimpleFileExplorer`: `agents/docs/features/missing-files.md`, `Sfe.Core/FileTracking/RelinkService.cs`)
with **the user's rule (2026-11-01): metadata stays for a while in case the file comes back, and is removed only when
a user accepts it** (SFE forgets on its own after its retention; Librarion never does).

## What a scan does with files it doesn't find

- A loose file or an archive entry not found is **marked missing** (`MissingSince` = when), never deleted; an archive
  not found is marked missing with its entries. When none of a book's files is present, the book is marked missing:
  kept with its metadata, tags (manual ones too), merges and edits; hidden from every list and search
  (`BookQueries.VisibleAsync`); its `TagCounts` and `Libraries.BookCount` go down.
- A missing file found again **where it was** is present again (loose: same path; an archive found again unchanged:
  all its entries without reading them; a changed one: entry by entry); its book comes back, counts go up.
- A missing file found **somewhere else** (moved, renamed, its archive renamed) is relinked after the scan, as SFE:
  among the files this scan added as new books, the only one with the same size and CRC-32 (archive entries), or the
  same size and file name (loose files: no CRC at scan time); the same name decides between several. Two possible
  places, or one place two missing files could claim: nothing is relinked (both stay as they are). The old rows take
  the new location (ids stay: links and curation follow), the new book goes, missing archives left empty go.
- A missing **root** (an unplugged disk) is not looked at at all: its books are neither missing nor removed.
- Results: `ScanResult.Missing` (books that went missing) and `Returned` (back by either way); `Removed` stays 0 for
  scans.

## Retention and removal

- Setting `library.keepMissingDays` (library level and above, 0–3650, default 30, as SFE's `Relink:RetentionDays`):
  books missing longer are **offered for removal first** ("ready"). Nothing happens when the days pass.
- `GET api/libraries/{id}/missing?after&limit` (right `library.scan`): oldest first, by key (`MissingSince`, `Id`), with
  `total` and `ready` counted on the partial index of missing books.
- `POST api/libraries/{id}/missing/remove` with exactly one of `bookIds` (up to 1000), `ready` (missing longer than the
  retention) or `all`: a background run (`RemoveMissing`, waits for the library's scan) removes the books still missing
  in batches (files, links, empty missing archives); a book that came back in the meantime stays. Counts don't move
  (they went down when the books went missing). Logged in the security log (`LibraryChanged`).
- The library's Missing books tab (`library-panel.ts`) lists them (title or file name, where the file was, since when, "ready to remove") and removes one,
  the ready ones or all, each after a confirmation that says it can't be undone.
- **Removing a folder** from a library (`DELETE …/roots/{id}`, confirmed by the user) still removes its books directly:
  the confirmation is the acceptance.

## Limits, later

- A present book with one missing copy keeps that file row as missing until the copy comes back; the removal only
  takes missing *books*.
- Imports from other sources ([imports](imports.md)) follow the same rule: books whose
  files are gone become missing, a re-import never removes.
