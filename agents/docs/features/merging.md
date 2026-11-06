# Duplicates and merging: books and tags

Built (stage 2): `Books.MergedIntoId`, `WorkKey`, the `(TenantId, Size, Crc32)` file index and `BookIdentifiers`
([catalog](catalog.md)). Built (stage 3): **identical files at import** (`CatalogWriter.CopiesAsync`, tests in
`LibraryScannerTests`), below. The rest of detection and merging is planned.

## Identical files at import (built)

- A new archive entry whose size and CRC-32 match a file **of the same library** (or an earlier entry of the same
  batch) is hashed (SHA-256) with it; same hash = another file of that book, no new book, no tag counts moved.
  Different bytes under the same CRC stay a book of their own.
- Hashes are computed only on a size + CRC match, then kept in `BookFiles.ContentHash` (a stored file is hashed once,
  read through `BookDownloads.Open`); a file that can't be read (damaged, gone) is never called a copy.
- Same library only: a book belongs to one library, and download rights are checked in the file's library.
- Loose files have no CRC at import, so they are not matched yet (hashing every loose file would read whole books).
- A removed copy leaves the book with its other files; a changed copy re-reads the book's metadata from that file.
- Always on for now: the tenant setting to turn it off, and undo / split, come with stage 6. `ScanResult.Added`
  counts books; `CatalogWriter.Copies` the files that joined a book.

Code (planned): `src/Librarion.Core/Merging/` (`DuplicateFinder`, `BlockingKeys`, `BookMerger`, `BookSplitter`,
`TagDuplicateFinder`, `MergeLog`), entities `DuplicateGroups`, `DuplicateMembers`, `NotDuplicates`, `MergeLog`;
client `app/curation/duplicates/`, the duplicates panel of `app/books/book-page`. Builds on [tags](tags.md) (tag merge),
[metadata-tags](metadata-tags.md) (fields) and [archives](archives.md) (file identity). Decision D19 in
[foundation-plan](../development/foundation-plan.md) (decided 2026-10-31).

Built (stage 5): the book page's **read-only** panel of possible duplicates, computed per book on demand (same file
by size + CRC, same identifier, same work key; capped, no tables yet): [book-page](book-page.md). Compare, merge, Not a
duplicate and the duplicate groups below are still planned.

Big collections are full of the same book twice: the same file in two archives, an FB2 and an EPUB of one work, two
scans with different titles or file names. Tags repeat too: `author:"Steven King"` and `author:"Stephen King"`,
`genre:Horror` and `category:Horror`. The app **finds them, shows them, and merges them in a click**; every merge can
be undone.

## A book holds several files

- A book is the work as shown (cover, title, fields); its `BookFiles` are its copies and formats (loose files or archive
  entries). Merging books moves files into one book; splitting moves them out. A file belongs to exactly one book.
- A file's identity (path, or archive + entry name + CRC + size, [archives](archives.md)) maps to its `BookFile`, so a
  re-import finds the file where it is now: **merges survive re-imports**, a rescan never splits a merged book.
- Imported field and tag links of a book are recomputed from **all its files** (union; the first file by preference
  order for single values such as the title); edited values, manual links and suppressions win as in [tags](tags.md).

## Finding duplicate books

Never pairwise over 2M books: each book gets a few **blocking keys**, indexed; books sharing a key are candidates,
compared only within the key's group.

| Level | Key | Action |
|---|---|---|
| **Same file** | content hash (SHA-256 of the bytes, computed lazily: candidates first by size + CRC32, which zip entries give for free) | merged automatically into one book on import (tenant setting, on by default), the copies kept as files of it |
| **Same document** | FB2 `<document-info><id>` (weak: with the same title or first author only), EPUB `dc:identifier` (UUID), Calibre `uuid`, ISBN | suggested, high confidence |
| **Same work** | normalized title (case-folded, punctuation and articles removed, transliterated) + first author's normalized name; also file name stem + author when there is no title | suggested, medium confidence (different formats or editions show here) |
| **Similar** | small edit distance on the title | **on demand only** (D21): never a catalog-wide pass; computed when a curator opens one book, one author or one field page, capped (200 candidates); Postgres `pg_trgm`, SQLite in memory over the capped set |

- Keys are computed at import and on edits; a background task (batches keyed on `Books.Id`, resumable) builds
  `DuplicateGroups (Id, Level, Key)` and `DuplicateMembers (GroupId, BookId)` incrementally: a new book joins or opens
  groups by its keys only.
- **Not duplicates:** a user can mark books as different (`NotDuplicates`, pairs or "this book is unique"); they are
  never suggested together again, also after re-imports.
- Rights and content filters apply: a user only sees duplicates among books they can see.

## Shown on the book page

- **Copies and formats** of the book (its files): format, size, library, archive or path, source; Read / Download each.
- **Possible duplicates** (other books in a group with this one, not yet merged): a panel "This book may also be in
  the library as: …" with each candidate's cover, title, authors, formats and the reason (same file, same ISBN, same
  title and author). Everyone who can see them gets the list (pick the better copy to read); users with
  `library.merge` get **Compare and merge** and **Not a duplicate**.
- Grids and lists show a merged book once; for curators a small badge marks books with open duplicate suggestions, and
  search has `special:duplicates`.

## Merging books

- **Compare:** side by side on desktop, stacked on phones: covers, titles, every field, files, sizes, sources,
  differences highlighted. The user picks the book to keep (default: the one with most metadata, else the oldest) and,
  per single value (title, cover, description, rating), which side wins; multi-value fields and tags are unioned
  (sources kept, duplicates dropped).
- **Merge:** files, links, identifiers, shelves, reading progress (latest position per user and format), download
  history and ratings move to the kept book; the other becomes a **redirect** (`Books.MergedIntoId`): its URLs, OPDS
  ids and API calls answer with the kept book, so reader apps' links keep working.
- **Bulk:** the duplicates page merges selected groups, or every "same file" group, as a background task in batches
  with progress, using the default choices.
- **Undo / split:** every merge writes a `MergeLog` row (what moved, chosen values); undo restores it while no later
  change conflicts. **Split** moves chosen files into a new book (metadata re-read from those files), for wrong merges
  found later.

## Duplicate tags

**Built (stage 6, chunks 2-3): tag and field merges with undo** (`Core/Merging/TagMerger.cs`, routes in
[tags](tags.md#the-tag-service-built-stage-6), tests `TagCurationTests`):

- **Tag merge** (source into target, plain tags or values, fields may differ): links moved a batch of 1,000 books at a
  time (a target link of the same book and source kept, the source's dropped), counts by delta, `SortAuthor` /
  `SeriesId` refreshed; then the source's aliases and its name become the target's aliases, its import mappings,
  content filters (the target's own one for a group wins), disambiguation references, children and field-key aliases
  go to the target, the source is deleted, the children's closure rebuilt. Refused: a field's own row
  (`target.field`), a built-in source (`tag.builtIn`), a target under the source (`target.under`).
- **Field merge** (`genre` into `category`; never a built-in source; not with a content filter on it): each value
  merged into the target's value of the same normalized name (a child `MergeLog` each), the others moved under the
  target field (`FieldId`, closure, sort columns when the target is `author` or `series`); the field's import mappings
  follow, its key becomes a `FieldKeyAliases` row: `genre:` keeps working in searches (`BookSearch.KeysAsync`),
  suggestions and the importer (a Calibre `#genre` column lands in `category`).
- **`MergeLog`** (D-6.5): kind, source and target (ids and names, no foreign keys), links, `Completed`, `Data` (JSON:
  the source's row and everything that moved), `Undone`; `MergeLogLinks` (book, source, ordinal, number, kept) for
  each link taken. Undo makes the source again **with its id when free**, puts back its parents, aliases, mappings,
  filters, children and links (counts and sort columns again), and deletes the log's links. Refused once undone
  (`merge.undone`), when the target is gone (`merge.targetGone`), while a later merge into or of the target stands
  (`merge.later`). A merge stopped half-way (a cancelled run) has `Completed` false: undo moves its links back only.
- Above 10,000 links a merge, field merge or undo is a tenant run (kinds `TagMerge`, `FieldMerge`, `MergeUndo`).

Still planned here: **finding** duplicate tags and doing it from one page:

- Suggestions per field (and among plain tags), by blocking keys on the normalized name: same name after NFKC,
  case and spacing; transliteration (Стивен Кинг / Stephen King); name forms ("King, Stephen", "S. King", "King
  Stephen"); same name in two fields (`genre:Horror` / `category:Horror`). Ranked by book count, so the merges that
  fix most books come first.
- **Spelling distance** ("Steven" / "Stephen") is **on demand only** (D21): on one tag's page or one field page,
  capped, as the books' Similar level; no catalog-wide fuzzy pass over 650k values.
- **Merge fields:** a whole field into another (`genre:` → `category:`): its values merged by name into the target
  field's, the rest moved, the field becomes an alias key (`genre:` keeps working in search and in import mappings).
- The tag page and the tag picker show "Possible duplicates" for a tag; **Not the same** is remembered as for books.
- Undo through `MergeLog` like books.

## Scale

- Fuzzy matching (edit distance) has no ordinary index: only on demand within one scope, capped (D21).
- Blocking keys are columns or small tables with indexes; a group above a size cap (e.g. 50 books with "Untitled" by
  "Unknown") is dropped as noise, never compared.
- Content hashes only for size + CRC collisions (rare), read in the background with I/O limits; never a hash of every
  file up front.
- Merging a book is a handful of keyed updates; moving the links of a big tag (an author with 5k books) runs in batches
  of 1000; counts (`TagCounts`, book counts per library) are adjusted, not recomputed.

## Rights

`library.merge` (planned use) (Write: merge, split, undo, mark not duplicate), `tenant.tags` covers tag and field merges. Seeing
duplicates and copies comes with browsing.

## Tests to have

- Each blocking level finds its duplicates (same file in two archives, FB2 id, ISBN, title + author with different
  file names, transliterated titles) and not the noise; `NotDuplicates` hides a pair across re-imports; merge moves
  files, links, progress and shelves, keeps chosen values, redirects the old id (web, API, OPDS); a re-import keeps the
  merge; undo and split restore; bulk merge in batches; tag duplicates by name forms and transliteration, field merge
  keeps `genre:` working; hidden or filtered books never appear as someone's duplicates. e2e: book page shows copies
  and possible duplicates, compare and merge on phone and desktop, merge two authors from the suggestions.
