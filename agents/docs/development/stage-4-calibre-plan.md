# Stage 4: Calibre import (plan)

Roadmap: [foundation-plan](foundation-plan.md) (D1, D15, D18, D19; budget "Calibre `metadata.db` of 2M books < 30 min
on PostgreSQL, unchanged re-import < 5 min"). Goal: **Calibre users move in with their curation**, and keep using
Calibre if they want: re-imports pick up their edits. Version stays on `edge`; the merge into `main` makes it `0.5.0`.

Reference for Calibre's schema (behaviour only, GPL-3 code never copied): calibre-web `cps/db.py`
(`C:\workspace\calibre-web`), Calibre's own `resources/metadata_sqlite.sql` (read for the table list, not copied).

## Decisions for this stage (proposed; the first one is the user's to confirm)

- **A Calibre library is a root** (recommended): a library root holding `metadata.db` is a *Calibre root*. Its scan
  runs the Calibre importer instead of the folder walk: books come from `metadata.db`, their files from
  `<root>/<books.path>/<data.name>.<format>`, so the loose files are never imported a second time as bare books.
  Alternative: a separate "Import a Calibre library" action into any library (one more UI, and a folder scan of the
  same folder would double every book).
- **Read-only, never written:** `metadata.db` opened `Mode=ReadOnly` (Calibre may be running; its WAL is honoured);
  nothing is written back to Calibre (as [tags](../features/tags.md): writing back is not planned).
- **Identity:** a Calibre book is `(root, calibre id)`, kept as the identifier `calibre:<id>` plus `calibre-uuid`;
  files as loose `BookFiles` under the root (`RootId`, path `books.path/data.name.ext`), so downloads, covers and
  identical-file detection work as for folders.
- **Change detection:** `books.last_modified` (Calibre bumps it on every edit) against the value stored at the last
  import; unchanged books are skipped in batches by id; books gone from `metadata.db` leave like files gone from a
  folder; the whole root skipped when `metadata.db`'s size and time haven't changed.
- **What becomes what** (D18, through `ImportTagResolver` with source `Calibre` and the import mappings):
  authors (sort from `authors.sort`) → `author`; series + `series_index` → `series` with number; publishers →
  `publisher`; languages (ISO 639-2/3 → our normalised codes) → `language`; tags → plain tags, a dotted name
  (`Fiction.Fantasy.Epic`, Calibre's hierarchy) becomes a parent chain; `pubdate` year → `year`; ratings (0–10) →
  `Books.Rating`; comments (HTML) → description, text only; identifiers → `BookIdentifiers` (isbn, asin, goodreads…
  as they are named).
- **Custom columns** (`custom_columns`, `custom_column_<n>`, `books_custom_column_<n>_link`): `text` (single or
  multiple), `enumeration`, `series` (with its extra number) and `rating` → a **custom field** per column (its
  `label` as key, its `name` shown), values as field tags; `comments`, `bool`, `int`, `float`, `datetime` and
  `composite` → kept per book in a small key/value table for display (no tags: not browsable values). The custom
  field table may need a migration (both providers).
- **Covers:** `has_cover` → `CoverSource.Calibre`, the picture read from `<path>/cover.jpg` on first view into the
  same cover cache (a new `CoverService` source); without one, the generated cover as for every book.
- **Curation kept on re-import:** only `Source = Imported` links of a re-read book are recomputed (as folder scans);
  manual links, merges and edits made in Librarion stay. Covered by tests with a Librarion edit before a re-import.
- **Titles:** `books.title`, `books.sort` as the sort title when present (Calibre's article rules), else ours.

## Work items

### 4.1 Reader of `metadata.db`

- [ ] `Core/Calibre/CalibreLibrary`: opens read-only, checks the schema it needs (tables and columns present; a
      version it doesn't know reads what it can and reports the rest), reads books in id order in batches of 1000 with
      their links (one query per link table per batch, never one per book).
- [ ] Samples from several Calibre versions (made by the test from SQL, as Calibre's schema of each version: 3.x,
      5.x, 7.x), custom columns of every type, a WAL-mode database, a locked one.

### 4.2 Importer

- [ ] Calibre root detection in `LibraryScanner` (root holds `metadata.db`), `CalibreImporter` writing through
      `CatalogWriter` (a `ScannedFile` per format, metadata from the database instead of the heads).
- [ ] Mapping above; custom fields; hierarchical tags; ratings; identifiers; change detection by `last_modified`;
      books gone; counts by differences.
- [ ] Identical files and formats: a Calibre book with EPUB + MOBI is one book with two files (it is, in Calibre).

### 4.3 Covers

- [ ] `CoverSource.Calibre` from `cover.jpg`, cache key from its size and time; tests.

### 4.4 Generator, benchmark

- [ ] A generated 2M-book `metadata.db` (from the catalog generator's shapes: authors, series, tags with dotted
      hierarchy, custom columns), import < 30 min on PostgreSQL, unchanged re-import < 5 min; SQLite recorded.

### 4.5 UI, docs, e2e

- [ ] The library panel shows a Calibre root as such (badge, "Calibre library"), its runs with counts.
- [ ] User docs (`docs/administration/libraries.md`: adding a Calibre library), CHANGELOG.
- [ ] e2e: a small Calibre library (written by the test: `metadata.db` from SQL, two books, a cover) added as a
      folder, scanned, a book downloaded, its cover from Calibre.

## Exit criteria

Generated 2M `metadata.db` imported within budget, unchanged re-import within budget; re-import keeps every curation
(manual tags, merges, edits); samples of several Calibre versions read; e2e: import a Calibre library.
