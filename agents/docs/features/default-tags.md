# Default tags of a library and its folders

Code: `DefaultTag` (`Data/Entities/Catalog/Book.cs`, table `DefaultTags`), `Core/Tagging/DefaultTags.cs` (list, add,
remove, the walk over a folder's books), `CatalogWriter` (`DefaultsAsync`, `GiveDefaults`, `MovedAsync`: at import and
on relinks), `Folders.PruneAsync` (keeps folders holding one), `TagMerger` (carries them, undo brings them back), routes
in `Api/Endpoints/FolderEndpoints.cs`; client `app/folders/default-tags.ts` (the dialog), the defaults line and row
badges in `app/folders/folder-page.ts`, icon `tag-auto`. Tests: `DefaultTagTests` (both providers),
`folders.spec.ts` (client), e2e `specs/folders.spec.ts` (every device, axe on the dialog). Built in stage 6 chunk 8c; a port of SFE's folder default tags
(`C:\workspace\SimpleFileExplorer\agents\docs\features\default-tags.md`).

## Decisions

- Plain tags only (fields are a book's metadata, edited per book), at most 20 per library or folder. Set on the
  library (`FolderId` null: every book of it) or a folder, **inherited** by the folders under it (ZIPs and the
  directories inside them included: a ZIP is a folder, [folders](folders.md)).
- Given as **`Rule` links**, once: when a **new book** comes in (scan or Calibre / INPX import: the library's and its
  files' folders' defaults), or when a book is **moved** (a relink) to a folder whose chain gives tags the old one
  didn't (SFE: a rename gives nothing). Books already there are never tagged again by a scan.
- A tag the book has from any source, or had taken off (a suppression), is not given. Taking a tag off a book from
  the selection or the book page leaves its rule link (chunk 7's rule); removing the default with "take it off them" is
  how rule links go.
- **Adding** asks "new books only" or "also the N files already there" (the folder's `TotalFileCount`, or the
  library's books): the second walks them (below). **Removing** asks whether the books keep it; "take it off them"
  deletes only the tag's **rule** links of the books there (not those given by hand or by an import, unlike SFE which
  removes the tag), and does nothing when the library or a folder above still gives the tag.
- **A folder holding a default tag is never pruned** (the user's answer, 2026-11-06), so its defaults stay for the
  books that come back (SFE keys its rows by path for the same reason).
- Rights: reading with `library.folders` or `library.defaultTags`; changing with `library.defaultTags` (Write,
  Administrators and Curators).

## How it works

- Rows `(TenantId, LibraryId, FolderId?, TagId, Created)`, unique `(LibraryId, FolderId, TagId)`; cascades from the
  library, the folder and the tag.
- At import the writer reads the library's rows once (a few) and, only when some are on folders, the folders above
  each batch's files into the folder cache: a library without default tags costs one small read per scan or import.
- The walk ("also those there", "take it off them"): the library's books by id, or the folder and every folder under
  it a level at a time, each level's books through `BookFiles (FolderId, BookId)` by `BookId > last`, 1,000 at a time,
  each batch one transaction with counts by delta (`TagLinks`). Past `TagService.InlineLinks` (10,000, counted as the
  files there) a library run of kind `ScopeTags` (the progress panel follows it).
- A merge moves the source's rows to the target where the target isn't one already (`TagData.DefaultTags`); its undo
  moves them back.
- Routes: `GET api/libraries/{id}/default-tags?folder` (own, then inherited nearest first, each with where it is set),
  `POST api/libraries/{id}/default-tags` (`folder`, `tag` by id or name, `existing`), `DELETE
  api/libraries/{id}/default-tags/{tagId}?folder&books` (200, or 202 with a run). `FolderItem.DefaultTags` (own count)
  for the badges.
- Client: the folder page's header shows "Default tags" with its own chips and the inherited ones (dashed, "From …")
  and, with the right, a button opening the dialog (own with Remove asking the question in place, inherited read
  only, the shared picker with Create, then the two choices). At the top of the folder view the library's own are
  shown and changed. A folder row with its own defaults has the `tag-auto` badge (and their count past one); no panel
  on the row (SFE's popover): the folder's page lists them.

## Known limits, next

- A copy (another file of a book already there) or a new file of an imported book gives nothing: only new books and
  moves.
- A scan or import reads the defaults when it starts: one changed while it runs counts from the next.
- No admin list of every folder having default tags (SFE neither), no "apply again" on demand.
