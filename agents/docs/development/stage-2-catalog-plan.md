# Stage 2: catalog core and scale proof (plan)

Roadmap and decisions: [foundation-plan](foundation-plan.md) (D3, D5, D15, D18, D19, D20, D22; budgets). Goal:
**prove priority 2 before anything builds on the schema**: the catalog tables on both providers, a deterministic
generator of 2M / 10M real-shaped catalogs, a benchmark with budgets and plan checks, and the first keyset list
APIs. Version stays on `edge`; the merge into `main` makes it `0.3.0`.

## Decisions for this stage

- **Tables** (all tenant-owned, query-filtered, `NoQueryCrossesTenants` covers them):
  - `Books`: tenant, library, `Title` (null = none in the metadata), `SortTitle` (never null), `SortAuthor`
    (first author's sort name, D18), `SeriesId` + `SeriesNumber` (first series), `Description`, `Rating`, `Added`,
    `Updated`, cover columns (`CoverSource`, `GeneratedTemplate`, `GeneratedVariant`, `CoverState`: D15),
    `MergedIntoId` (redirect, D19), blocking keys `WorkKey` (normalized title + first author, hashed) and
    `FileStemKey`.
  - `BookFiles`: book, library, `ArchiveId` (null = loose file), `Path` (relative to the root, or entry name),
    `Format`, `Size`, `Crc32`, `Modified`, `Locator`, `Method`, `PackedSize`, `ContentHash` (lazy), preference order.
  - `Archives`: library, path, size, mtime, fingerprint, entry count, scan state (filled in stage 3).
  - `BookIdentifiers`: book, scheme (`isbn`, `uuid`, `fb2-id`, `calibre`), value; index (tenant, scheme, value).
  - `Tags` (D18: plain tags, fields, field values in one table), `TagAliases`, `TagParents`, `TagClosure`
    (ancestor, descendant, depth), `BookTags` (book, tag, source, `Ordinal`, `Number`; key (BookId, TagId, Source),
    index (TagId, BookId)), `TagCounts` (library, tag, book count), `Libraries.BookCount`.
  - `GroupTagFilters` (group, tag, allow / deny): content filters (D5), the narrow allow-list case of D22.
- **Keyset everywhere**: book lists sorted by title, author, series, added; each sort an index ending in `Id`; the
  cursor is an opaque base64 of the sort values + id. Tag lists per field by `SortName`.
- **Visibility** (`Visible()`): library rights (`library.browse` per library), not merged away, and the groups'
  content filters through `TagClosure` (allow: `EXISTS`, deny: `NOT EXISTS`); a group without filters admits every
  book of its libraries; several groups = union. Hidden tags: later with search (stage 5).
- **Generator** (`tests/Librarion.Benchmarks`, command `generate`): seeded and deterministic; writes straight to the
  database (PostgreSQL binary `COPY`, SQLite prepared multi-row inserts in big transactions), indexes built after the
  load; shapes from the budgets: 2M books, 500k authors (Zipf: few with thousands of books, most with one), 100k series,
  50k plain tags with messy variants, categories, languages, publishers, years, identifiers, 2.5M files (some in
  archives), planted duplicates (same file, same id, same work), groups with narrow allow-lists (0.1 %, 1 %, 10 %).
- **Benchmark** (same project, command `run`): warm runs of the main queries, p50 / p95 per query, budgets from the
  foundation plan; plan checks (PostgreSQL `EXPLAIN`: no `Seq Scan` on big tables; SQLite `EXPLAIN QUERY PLAN`: no
  `SCAN` of a big table without an index); a JSON + text report into a temp folder; exit 1 past a budget.
- **This machine** (16 GB RAM; C: ~26 GB free): generated SQLite catalogs and reports under
  `D:\Temp\librarion-bench`; Docker's storage is on D: too (moved 2026-11-01), so 2M and 10M fit on both providers.
  Docker Desktop started for a PostgreSQL run and stopped right after (little memory).

## Work items

### 2.1 Schema

- [x] Entities in `Core/Data/Entities/Catalog/` and `Tags/`, model config (`LibrarionDbContext.Catalog.cs`), query
      filters, the two `Initial` migrations regenerated ([database](database.md)); `ModelsOfBothProvidersAgree` green.
- [x] Built-in fields per tenant at tenant creation (`author`, `translator`, `editor`, `illustrator`, `series`,
      `category`, `publisher`, `language`, `year`) and built-in tags (Meta Tags, Archived); closure rows kept.
- [x] Tests: tenant filters on the new sets (`NoQueryCrossesTenants` + `CatalogRows`), cascade on tenant delete,
      closure for a value and a parent chain (`CatalogSchemaTests`).
- Deleting a library or tenant with millions of books by cascade is one huge transaction: stage 3 deletes in batches
  first (a task), the cascade then only finishes the job.

### 2.2 Generator

- [x] `tests/Librarion.Benchmarks` console (`generate --provider --connection --books 2000000 --seed`), writing
      through provider-specific bulk writers; counts and `TagCounts` computed on the way; `agents/tools/benchmark/`
      README.
- [x] Small-size tests in `Librarion.Tests` (`CatalogGeneratorTests`): deterministic for a seed, shapes (Zipf,
      duplicates, filters).

### 2.3 Catalog queries and API

- [x] `Core/Catalog/BookQueries` (`VisibleAsync`, keyset pages per sort, book detail), `BookVisibilities`,
      `TagQueries` (values of a field by sort name, with counts), `KeysetCursor`.
- [x] Routes `GET api/books?library&sort&after&tag&limit`, `GET api/books/{id}`, `GET api/fields/{key}/values`,
      `library.browse`.
- [x] Tests (`CatalogQueryTests`, both providers): paging walks the whole set once in order, filters never leak,
      the routes answer what the caller may browse. A value prefix search waits for stage 5 (collations).

### 2.4 Benchmark

- [x] Queries: first and middle pages per sort, a field value's books (most prolific author, a small author, the
      biggest series, category Fiction with subcategories), authors A–Z, book detail, filtered pages for each
      allow-list size.
- [x] Plan checks per provider; budgets; report.
- [ ] Runs: 2M and 10M on PostgreSQL and SQLite, recorded in this note.

### 2.5 Notes, docs

- [ ] [scale](scale.md) with the measured numbers and the indexes; feature notes (tags, metadata-tags, merging,
      book-display) updated where the schema is now built; AGENTS.md index.

## Results

2M books, 15.4M book–tag links, 2.4M files, 660k tags (this machine, warm, p95 in ms; budget 100, detail 50):

| Query | SQLite | PostgreSQL |
|---|---|---|
| books by title / author / newest, first and middle pages | 5–13 | 11–16 |
| one library by title, middle page | 9 | 15 |
| most prolific author (37k books) | 31 | 26 |
| author with 5–20 books | 5 | 18 |
| biggest series | 77 | 50 |
| category Fiction with subcategories | 11 | 30 |
| authors A–Z, first and middle | 3–5 | 8 |
| book detail | 6 | 11 |
| filtered group 0.1 % / 1 % / 10 % | 53 / 63 / 18 | 33 / 62 / 22 |

No full scan of a big table on either. Generation: SQLite 3.2 min, PostgreSQL 7.4 min (`COPY` of `BookTags` checks
its foreign keys: 230 s).

What the benchmark found and fixed on the way (details in the notes):

- The tenant query filter `TenantScope == null || TenantId == TenantScope` reached the databases as
  `(@p OR TenantId = @s)`: every book list scanned and sorted all books. Now two models, filters as plain equalities
  ([tenants](../features/tenants.md#isolation-in-the-database)).
- Sorting every library by author or date had no index: `(TenantId, SortAuthor, Id)`, `(TenantId, Added, Id)`.
- Lookups of a built-in field by `BuiltIn` scanned the tags: by `FieldKey` (unique index).
- Books with a tag (a content filter's allowed tags, a field value's page) are joined **by size** (D22 stays
  query-time): a broad tag walks the sort index and tests each book, a narrow one (under √(page × books) books, from
  `TagCounts`) starts from its links and sorts them; the tag's descendants are listed first, so a test is one seek on
  `(BookId, TagId)`. Before: category Fiction 2.9 s, a 0.1 % filter 0.8 s, a 1 % filter 0.17 s.

## Exit criteria

p95 budgets met at 2M on PostgreSQL, recorded for SQLite; 10M shows no growth with catalog size; no sequential scan on
big tables; the narrow allow-list case measured (D22: if it breaks the budgets, the user decides on precomputed
visibility).
