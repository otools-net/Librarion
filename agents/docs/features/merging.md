# Duplicates and merging: books and tags (planned)

Code (planned): `src/Librarion.Core/Merging/` (`DuplicateFinder`, `BlockingKeys`, `BookMerger`, `BookSplitter`,
`TagDuplicateFinder`, `MergeLog`), entities `DuplicateGroups`, `DuplicateMembers`, `NotDuplicates`, `MergeLog`;
client `app/curation/duplicates/`, the duplicates panel of `app/books/book-page`. Builds on [tags](tags.md) (tag merge),
[metadata-tags](metadata-tags.md) (fields) and [archives](archives.md) (file identity). Decision D19 in
[foundation-plan](../development/foundation-plan.md) (decided 2026-10-31).

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
| **Same document** | FB2 `<document-info><id>`, EPUB `dc:identifier` (UUID), Calibre `uuid`, ISBN | suggested, high confidence |
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
  `books.merge` get **Compare and merge** and **Not a duplicate**.
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

Tag merge itself is in [tags](tags.md) (links moved in batches, names become aliases, mappings follow, re-imports
respect it). This adds **finding** them and doing it from one page:

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

## Rights (planned)

`books.merge` (Write: merge, split, undo, mark not duplicate), `tags.manage` covers tag and field merges. Seeing
duplicates and copies comes with browsing.

## Tests to have

- Each blocking level finds its duplicates (same file in two archives, FB2 id, ISBN, title + author with different
  file names, transliterated titles) and not the noise; `NotDuplicates` hides a pair across re-imports; merge moves
  files, links, progress and shelves, keeps chosen values, redirects the old id (web, API, OPDS); a re-import keeps the
  merge; undo and split restore; bulk merge in batches; tag duplicates by name forms and transliteration, field merge
  keeps `genre:` working; hidden or filtered books never appear as someone's duplicates. e2e: book page shows copies
  and possible duplicates, compare and merge on phone and desktop, merge two authors from the suggestions.
