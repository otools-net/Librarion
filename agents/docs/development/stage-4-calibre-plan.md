# Stage 4: imports from other sources, Calibre `metadata.db` and INPX (plan)

Roadmap: [foundation-plan](foundation-plan.md) (D1, D11 as revised, D18, D19). Goal: **people move in with the
catalogs they already have**. Version stays on `edge`; the merge into `main` makes it `0.5.0`.

**The user's rule (2026-11-01):** a Calibre library and an INPX catalog are imported by **a manual import of data
from a different source**: an administrator starts it, it reads the source once, and **what it reads is stored in
our database**. Librarion never reads the source live, a folder scan never reads it, and nothing is written back.
Running the import again is how changes in the source come in.

References (behaviour only): calibre-web `cps/db.py` for Calibre's tables (`C:\workspace\calibre-web`, GPL-3: never
copied); MyHomeLib for INPX (`C:\workspace\MyHomeLib`, MIT: `Program/ImportImpl/unit_ImportInpxThread.pas`,
`unit_Consts.pas`).

## Decisions for this stage (confirmed by the user, 2026-11-01)

Confirmed explicitly: imported metadata wins over a later scan's head reads; INPX `DEL=1` records are skipped and
counted; the files' folder is added as a root automatically (with the checks of adding a root by hand). The rest was
accepted as proposed.

- **One import pipeline, one class per source** (as [book-formats](../features/book-formats.md)): `IImportSource`
  reads records `(source id, title, sort title, field values, plain tags, identifiers, description, rating, year,
  files)` in batches; `CatalogImporter` writes them through `ImportTagResolver` (source `Calibre` or `Inpx`, the
  import mappings) and `CatalogWriter`. A future source (a CSV, another program's database) is one more class.
- **A background task** like a scan ([tasks](../features/tasks.md)): `TaskRuns` kind `import`, progress, cancel,
  counts (added, updated, matched to books already there, skipped, problems), one per library at a time, never
  scheduled. Started from the library panel: "Import from another source" → source kind, the path of the source on
  the server (`metadata.db`, or the `.inpx` file), and where its books' files are (default: the source's folder).
  Paths validated as roots are (allowed folders for tenant administrators).
- **Right `library.import`** (library scope, built-in Administrators): importing writes a library's metadata in bulk.
- **Files:** imported books point at real files, as `BookFiles` under a root of the library: the files' folder is
  added as a root when it isn't inside one already. Calibre: loose files `<path>/<data.name>.<format>`. INPX: entries
  of the zips next to it (`<name>.inp` ↔ `<name>.zip`, or the record's `FOLDER`); the import lists each zip's directory
  (73 ms per 100k entries, measured) to store locators and CRCs, and writes the `Archives` rows as done, so a later
  scan skips the archives unchanged. A record whose file isn't found is skipped and counted (never a book without a
  file).
- **Matching what is there:** a record whose file is already a book's (the folder was scanned before) updates that
  book instead of making another; else, a book with the same source id (a re-import); else a new book. Identical
  files across records follow [merging](../features/merging.md).
- **Who owns a book's metadata:** a new column `Books.MetadataSource` (`Heads`, `Calibre`, `Inpx`, later `Edited`).
  A folder scan that sees an imported book's file changed updates the file (size, time, CRC) but **does not replace
  the imported metadata** (it re-reads heads only for `Heads` books). A re-import recomputes only its own source's
  `Imported` links; manual links, merges and edits stay. Migration on both providers.
- **Nothing is deleted without the user** (the user's rule, 2026-11-01, as SFE's missing files): a re-import never
  removes a book; books gone from the source stay while their files exist (counted as "not in the source any more");
  books whose files are gone become missing books, kept with their metadata, found again when the file comes back,
  removed only when a user accepts ([missing-books](../features/missing-books.md)).
- **Calibre** (`metadata.db` opened `Mode=ReadOnly`, WAL honoured; the schema checked, unknown columns ignored):
  authors (sort from `authors.sort`) → `author`; series + `series_index` → `series` with number; publishers →
  `publisher`; languages → `language`; tags → plain tags, a dotted name (`Fiction.Fantasy`) → a parent chain;
  `pubdate` → `year`; ratings 0–10 → `Books.Rating`; comments (HTML) → description as text; identifiers →
  `BookIdentifiers`; `books.title` / `books.sort`; source id `calibre` (+ `calibre-uuid`). Custom columns: `text`,
  `enumeration`, `series`, `rating` → a custom field per column, values as field tags; `comments`, `bool`, `int`,
  `float`, `datetime`, `composite` → a small per-book key/value table shown on the book page. Covers: `has_cover` →
  `CoverSource.Calibre`, `cover.jpg` read on first view into the cover cache.
- **INPX** (a zip of `.inp` files, lines of `\x04`-separated fields, layout from `structure.info` else
  `AUTHOR;GENRE;TITLE;SERIES;SERNO;FILE;SIZE;LIBID;DEL;EXT;DATE;LANG;LIBRATE;KEYWORDS`; `collection.info` names the
  collection): authors `last,first,middle:` → `author` (sort "Last, First Middle"); genres `code:` → `category`
  through `Fb2Genres`; series + number; language; keywords → plain tags; `LIBRATE` → rating; `LIBID` → identifier
  `libid` (with the collection's name); `DEL=1` records skipped (an option to import them hidden later). Encoding
  UTF-8 (older files: the code page declared or windows-1251).
- **Budgets:** Calibre `metadata.db` of 2M books < 30 min on PostgreSQL, unchanged re-import < 5 min (roadmap); INPX
  of 1M records < 15 min, unchanged re-import < 5 min (proposed). SQLite recorded.

## Work items

### 4.0 Missing books (done before the imports: it changes what scans do)

- [x] Files not found are marked missing, books whose files are all gone become missing (hidden, kept, counts down),
      found again at the same place or relinked when moved, removed only after the user accepts in the library panel;
      retention `library.keepMissingDays` (30) only decides what is offered first ([missing-books](../features/missing-books.md)).

### 4.1 Import pipeline

- [ ] `Core/Imports/`: `IImportSource`, `CatalogImporter` (matching, `MetadataSource`, batches of 1000, counts by
      differences), task kind `import` in `BackgroundTasks`, right `library.import`, routes, the migration.
- [ ] The scanner leaves imported metadata alone (`MetadataSource != Heads`); tests for scan-after-import and
      import-after-scan.

### 4.2 Calibre

- [ ] `CalibreSource`: schema check, books in id order with their links (one query per link table per batch), custom
      columns of every type; samples made by the test from SQL for several Calibre versions, a WAL database, a locked
      one.
- [ ] `CoverSource.Calibre` from `cover.jpg`.

### 4.3 INPX

- [ ] `InpxSource`: `structure.info`, `collection.info`, `.inp` lines, archive name rules, deleted records; samples
      written by the test (an INPX and its zips), broken lines reported, never fatal.

### 4.4 UI, docs

- [ ] Library panel: "Import from another source" (kind, source path, files' folder), the run with its counts; texts.
- [ ] `docs/administration/libraries.md` (importing from Calibre and INPX), CHANGELOG, rights reference.

### 4.5 Benchmark, e2e

- [ ] Generators: a 2M-book `metadata.db` and a 1M-record INPX with its zips (from the catalog generator's shapes);
      `import` runs timed, unchanged re-imports timed.
- [ ] e2e: a small Calibre library and a small INPX collection (written by the test) imported from the library
      panel, a book of each downloaded.

## Exit criteria

Both sources imported within budget, unchanged re-imports within budget; a re-import keeps every curation (manual
tags, merges, edits) and a later folder scan keeps imported metadata; samples of several Calibre versions and INPX
layouts read; e2e: import a Calibre library and an INPX collection from the library panel.
