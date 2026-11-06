# Folders

Code: `Folder` and `BookFile.FolderId` (`Data/Entities/Catalog/Book.cs`, configured in `LibrarionDbContext.Catalog.cs`),
`Core/Import/Folders.cs` (find or make, counts, prune), used by `CatalogWriter` (every file it writes), `LibraryScanner`
(missing, found again, relinks), `MissingBooks` (removals); the view: `Core/Catalog/FolderQueries.cs`,
`BookQueries.InFolderAsync`, `Api/Endpoints/FolderEndpoints.cs`, client `app/folders/folder-page.ts` (styles in
`styles/browse.css`); the benchmark's `CatalogGenerator.Folders` and its folder scenarios. Tests: `FolderTests`
(counts against a recount after each step), `FolderRouteTests`, `InpxImportTests`, `folders.spec.ts` (client), e2e
`specs/folders.spec.ts` (every device, layout, axe in both themes). Built in stage 6, chunks 8a (folders) and 8b (the
view), for folder default tags (8c): [stage 6 plan](../development/stage-6-curation-plan.md).

## Model (the user's answers, 2026-11-06)

- One row per folder books are found in: a **root's own folder** (`Path` empty, no parent), each **directory** under
  it, each **ZIP** (its own folder, `ArchiveId` set: a ZIP is a folder) and each directory **inside a ZIP**
  (`lib/a.zip/sub`). `Path` is relative to the root with `/`; `Name` is its last part, cut to 255 (it sorts lists).
- Every `BookFile` has its `FolderId` (required): "the books of a folder" and "the books under a folder" are joins
  on `BookFiles (FolderId, BookId)`, not reads of a root's file rows (paths aren't indexed, only hashed).
- Indexes: `(RootId, PathKey)` (found by the hash of the path, then the path compared, as files and archives),
  `(ParentId, Name, Id)` (a folder's folders paged by key), `ArchiveId`.
- Cascades: a root's removal (or its library's) takes its folders, a parent its children, a folder its files (only
  reached through those). An archive's removal only clears `ArchiveId` (`SetNull`): the folder goes when it is empty.

## How they are kept

- **Made when a file needs one** (`Folders.AssignAsync`, called by `CatalogWriter` for scanned and imported files,
  Calibre and INPX alike): the file's folder and every folder above it, looked up by path key a batch at a time,
  the missing ones made a depth at a time (parents first, so each knows its parent's id). A cache per writer of the
  folders seen, cleared past 100,000: memory stays flat. One writer per library at a time (scans and imports wait for
  each other), so two can't make the same folder.
- A ZIP made again (removed, then back with a new archive row) gives its folder the new `ArchiveId`.
- **Moved files** (relink): the old row takes the new file's folder; the folders it left are pruned.
- **Pruned** (`Folders.PruneAsync`, from the folders some files left): a folder with neither files nor folders goes,
  then its parent the same way; a root's own folder stays, and so does a folder holding default tags
  ([default-tags](default-tags.md)). Called after relinks and after removing missing books.
- A **missing** file keeps its folder (the book may come back): a folder whose books are all missing stays.

## Counts (the user's answer, 2026-11-06: stored, exact)

- `FileCount` (present files directly in it), `TotalFileCount` (with every folder under it), `FolderCount` (folders
  directly under it). Files, not books: two formats of one book in one folder count twice (the book list there shows
  the book once).
- Kept by deltas in `Folders` (`Count`, then `FlushAsync` with the writer's tag counts): +1 for each file written
  (scans, imports, copies), -1 for a present file going missing or removed (`CatalogWriter.CountFilesAsync`, before
  the update), +1 for a missing one found again; a relink moves nothing (the new row's -1 and the old row's +1 in the
  same folder); a removal of missing books moves nothing (their files were already missing). A made folder adds 1 to
  its parent's `FolderCount`, a pruned one takes 1 away.
- The flush walks each changed folder's ancestors (a parent cache, cleared past 100,000) and writes one
  `UPDATE … WHERE Id IN (…)` per distinct change, not one per folder.
- No recount exists: a drift (a crash between a write and its flush) stays until the files move again.

## The view (stage 6 chunk 8b)

- Right `library.folders` ("Browse by folder", Read, Administrators and Curators; [rights](rights.md)). Folder names
  and counts aren't narrowed by content filters (the user's answer, 2026-11-06); the books of a folder are.
- Routes: `GET api/libraries/{id}/folders` (the roots holding books, named after the root's folder on disk: its
  last part, never the server's path), `GET api/folders/{id}` (counts and the folders above it; a library's only root
  left out of the path: it is the library), `GET api/folders/{id}/folders?after&limit` (by `(ParentId, Name, Id)`),
  `GET api/books?library&folder` (books with a present file directly in it), `api/books/scope` and `scope/tags` with
  `folder` (Tag all). A folder of a library without the right is not found.
- A folder's books: `BookQueries.InFolderAsync` picks as tag pages do: from the folder's files (index
  `(FolderId, BookId)`, then sorted) when `FileCount` < √(page × library books), else the sort index walked and each
  book tested by its files (index `BookId`).
- Client: `/folders` (the library's top: its only root's folders, or its roots), `/folders/<id>/<name>` (the name only
  for reading, put right with `replaceUrl`; `folders` is a reserved first segment in `tag-link.ts`); the path as
  links, the folders by name with their total files (100 at a time, "Show more folders"), then `BookList` with the
  folder when it has files of its own. A folder of another library switches the library. Navigation item Folders
  for the right (`shell.ts`). Icon `zip` for ZIPs.

## Known limits, next

- Empty folders aren't made (a directory without books has no row) and the scanner doesn't remove a folder whose
  files went missing: only removals and relinks prune.
- A folder of 100,000 folders is shown 100 at a time with a button, all kept in the page (no windowing yet).
