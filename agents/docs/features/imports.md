# Imports from other sources: Calibre, INPX

Code: `Core/Imports/` (`ImportSource.cs`: `ImportRecord`, `ImportFile`, `IImportSource`, `ImportFormat`, `ImportFormats`,
`ImportReport`; `CatalogImporter`), `Core/Imports/Calibre/CalibreSource.cs`, `Core/Imports/Inpx/InpxSource.cs`, `CatalogWriter.ImportAsync` /
`RefreshAsync`, `Books.MetadataSource` / `ImportHash`, `BookProperties`, `LibraryRoots.RootForAsync`, task kind
`Import` (`BackgroundTasks.StartImportAsync`), route `POST api/libraries/{id}/import` (`Api/Endpoints/TaskEndpoints.cs`),
Calibre covers in `CoverService.CalibreAsync`. Tests: `ImportTests` (the pipeline, with a test source and a test kind),
`CalibreImportTests` (sample `metadata.db` files written from SQL), `InpxImportTests` (INPX files and zips written by
the test); client `admin/library-panel.ts` (the Import tab's form, import runs' counts on Runs), `admin.spec.ts`.

**The user's rule (2026-11-01):** a Calibre library or an INPX catalog comes in by a manual import of data from a
different source. An administrator starts it, the source is read once, and what it says is stored in our database.
The source is never read live and never written; importing again is how its changes come in.

## Pipeline

- One class per kind of source (as [book-formats](book-formats.md)): an `ImportFormat` (key, `Check(path)`, `Open(path, filesFolder)`)
  registered in the services (`AddSingleton<ImportFormat, …>`; a test adds its own), opening an `IImportSource` that
  yields `ImportRecord`s (source id, `BookMetadata`, files, cover flag) front to back and reports what it can't use to
  `ImportReport` (skipped, the first 100 problems), never throwing.
- `CatalogImporter` writes batches of 1000. For each record, its book is: the book a file of it already belongs to (the
  folder was scanned or imported before), else the book with its source id (identifier scheme `IImportSource.IdScheme`,
  e.g. `calibre-uuid`; a moved book), else a new book with all its files. A merged book's target is used.
- Files are absolute paths inside the import's root: loose files stat'ed only when the library has no row for them;
  archive entries from the zip's directory, listed once per archive (two listings kept). An archive the library didn't
  have gets its row (fingerprint, entries) and is written **done** at the end of the import, so a later scan skips it
  while unchanged; an import cancelled before its end leaves it `Scanning` and the next scan reads it whole.
- A record with no file found (or outside the root) is **skipped and counted**: never a book without a file. A missing
  file of a matched book that is there again is present again and the book comes back.
- The same record again (an xxHash64 of its metadata, files and cover flag, kept in `Books.ImportHash`) is counted
  unchanged and nothing is written: an unchanged re-import is reads only.
- No copy detection (no hashing): the source says which files are one book.
- Counts as a scan's (`ScanResult`): `Added`, `Changed` (matched books written), `Unchanged`, `Skipped`, `Problems`;
  progress `FilesSeen` = records read.

## Who owns a book's metadata

- `Books.MetadataSource`: `Heads` (a scan reads the file's head), `Calibre`, `Inpx`. An import sets it; the record's
  metadata replaces title, sort title, description, rating, identifiers, properties and every `Imported` link (manual
  and rule links stay, counts move by differences).
- A scan that sees an imported book's file changed updates the file row only (`CatalogWriter.RefreshAsync` skips the
  metadata of books not owned by `Heads`): **imported metadata wins** (the user's decision, 2026-11-01).
- Nothing is removed by an import: books no longer in the source stay; books whose files are gone become missing books
  through the scans ([missing-books](missing-books.md)).

## Starting one

- `POST api/libraries/{id}/import` `{ kind, source, files? }`, right **`library.import`** (library scope, built-in
  Administrators). Checks: a known kind (`kind.unknown`); the source an absolute existing file (`source.notAbsolute`,
  `source.missing`), for tenant administrators under `Librarion:Libraries:Folders` like roots (`source.notAllowed`);
  the kind's own check (`source.invalid`, the reason in the server's log). `GET …/import/kinds` lists the kinds.
- `files` (default: the source's folder) must lie in a root of the library, or becomes one with the checks of adding a
  root by hand (`files.notAllowed`, `files.overlaps`…; logged as a root added). The user's decision (2026-11-01).
- A background run (`TaskRuns` kind `Import`): waits for the library's run before it (a scan), one per library, never
  scheduled; logged in the security log (`LibraryChanged`).

## Calibre

- `metadata.db` opened `Mode=ReadOnly`, unpooled (the file isn't held afterwards), WAL honoured (an open write of
  Calibre's isn't seen; what is committed is). Required: tables `books`, `authors`, `books_authors_link`, `data` and
  `books.id/title/path`; every other table and column is looked up first (`sqlite_master`, `pragma_table_info`), so old
  versions (an `isbn` column, no `identifiers`, `languages`, `uuid` or custom columns) and newer ones (unknown tables)
  read. A database error mid-way keeps what was read and reports the rest.
- Books in id order, 1000 at a time, one query per link table per batch (ids inlined, never user text).
- Mapping: authors in link order, `|` back to `,`, sort from `authors.sort` → `author`; series + `series_index` →
  `series` with number; publishers → `publisher`; `lang_code` → `language` (ISO 639-1 where one exists); tags → plain
  tags under "Imported: Calibre"; `pubdate` → `year` (Calibre's undefined date, year 101, is none); ratings 0–10 →
  `Books.Rating`; comments (HTML) → description as text; identifiers (ISBN normalized, others by type, at most 20
  characters) → `BookIdentifiers`; `books.title` / `books.sort`; source id `books.uuid` (`id:<n>` without one).
- Files `<library>/<books.path>/<data.name>.<format lower case>`.
- Custom columns (not `mark_for_delete`, not `composite`): `text`, `enumeration`, `series` (number from the link's
  `extra`), `rating` (in stars, `4.5`) → field values of a field per column, keyed by the label (`my_series` →
  `my-series`); `comments`, `bool`, `int`, `float`, `datetime` → `BookProperties` (name as Calibre shows it, value as
  text: `true`/`false`, invariant numbers, `yyyy-MM-dd`), returned in the book's details (`properties`).
- Cover: `has_cover` → `CoverSource.Calibre`; `cover.jpg` in the folder of the book's first loose file, read on first
  view into the cover cache (same limits as extracted covers); not there → the generated cover; a broken picture marks
  the book `Failed` until a re-import resets it.

## INPX

- The `.inpx` is a zip read with `ZipDirectory` (not unpacked): `structure.info` gives the fields' order (else
  `AUTHOR;GENRE;TITLE;SERIES;SERNO;FILE;SIZE;LIBID;DEL;EXT;DATE;LANG;LIBRATE;KEYWORDS`), the first line of
  `collection.info` the collection's name (else the file's name); every `.inp` but `extra.inp` (an online collection's
  books that aren't local) is read line by line, streamed. Layout learnt from MyHomeLib (MIT:
  `Program/ImportImpl/unit_ImportInpxThread.pas`).
- A line: fields split by `\x04`; authors `last,first,middle` separated by `:` → `author` ("First Middle Last", sort
  "Last, First Middle"); genres `code:` → `category` through `Fb2Genres`; `SERIES` + `SERNO` → `series`; `LANG` →
  `language`; `KEYWORDS` split by `,` → plain tags under "Imported: INPX" (20 at most); `LIBRATE` 1–5 → rating 2–10;
  `LIBID` → identifier `libid`. `DATE` (when the library added it) isn't used.
- The book: entry `FILE.EXT` of the zip `FOLDER` (relative to the files' folder), else of the zip named as its `.inp`.
  A format Librarion doesn't know, or a line without `FILE`/`EXT`, is skipped and reported (`name.inp:line`).
- `DEL` = 1 (anything sorting at or after `"1"`, as MyHomeLib) is skipped and counted (the user's decision,
  2026-11-01); since the zip is written done, a later scan doesn't add its entry either.
- Source id `<collection>:<LIBID>` (scheme `inpx`), else `<collection>:<zip>/<entry>`.
- `.inp` text is UTF-8; a file that isn't valid UTF-8 is read as windows-1251 (older catalogs). Checking costs one
  extra read of each `.inp`.

## Scale

Measured 2026-11-01 (`import` command, [benchmark](../../tools/benchmark/README.md#what-import-measures),
`agents/tools/benchmark/import.sh`), NVMe SSD, PostgreSQL in Docker:

| Source | Import | Unchanged re-import | Budget |
|---|---|---|---|
| Calibre, 2M books (one EPUB each, 500k authors) | 27.1 min (1,232 a second) | 2.6 min | 30 min / 5 min |
| INPX, 1M records (100 zips, 1 % deleted) | 8.9 min (1,845 a second) | 38 s | 15 min / 5 min |
| SQLite, 100k: Calibre / INPX | 92 s / 75 s | 15 s / 10 s | recorded |

What it took (each found by the benchmark):
- New books' rows past EF: the books saved by EF only on SQLite; on PostgreSQL their ids are taken from the sequence
  (`BulkRows.ReserveIdsAsync`) and books, files, links, identifiers and properties copied (`BulkRows.InsertAsync`,
  binary `COPY`; SQLite: one prepared insert per row in a transaction). EF's per-row cost was 80 % of the time
  (580 → 1,566 records a second on PostgreSQL).
- `TagCounts` moved by one upsert per 1000 tags (`BulkRows.AddCountsAsync`), not a row per tag through EF: with 500k
  authors nearly every author of a batch is a different tag, and the per-row updates made 2M books take over an hour.
  Scans use the same flush.
- The source-id lookup is two plain queries (identifiers by `(TenantId, Scheme, Value)`, then books by key): as a join,
  PostgreSQL's statistics lagged the growing tables and planned a walk over every book per identifier (a 30 s timeout
  at 9,000 books).
- Calibre's own indexes on its link tables (`books_authors_link_bidx`…) are what makes reading by book fast; a
  database without them (the first generated one) reads each link table whole per batch (2.4 min per 100k books).

## Limits, later

- A Calibre tag with dots (`Fiction.Fantasy`) is one plain tag, not a parent chain yet.
- A custom column's field is named by its key; Calibre's display name isn't used for the field yet.
- An option to import INPX records marked deleted as hidden books.
