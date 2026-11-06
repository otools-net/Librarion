# Folders

Code: `Folder` and `BookFile.FolderId` (`Data/Entities/Catalog/Book.cs`, configured in `LibrarionDbContext.Catalog.cs`),
`Core/Import/Folders.cs` (find or make, prune), used by `CatalogWriter` (every file it writes), `LibraryScanner`
(relinks), `MissingBooks` (removals); the benchmark's `CatalogGenerator.Folders`. Tests: `FolderTests`,
`InpxImportTests` (imported books in their ZIP's folder). Built in stage 6, chunk 8a, for the folder view (8b) and
folder default tags (8c): [stage 6 plan](../development/stage-6-curation-plan.md).

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
  then its parent the same way; a root's own folder stays. Called after relinks and after removing missing books.
- A **missing** file keeps its folder (the book may come back): a folder whose books are all missing stays.

## Known limits, next

- Empty folders aren't made (a directory without books has no row) and the scanner doesn't remove a folder whose
  files went missing: only removals and relinks prune.
- Costs to measure with the folder view (8b) at 2M: the lookup per batch is cached (the generated catalog has
  about 1,000 folders: the archives and `books/000`–`books/999`); the extra `BookFiles (FolderId, BookId)` index is
  one more index on 2.5M rows written at import.
