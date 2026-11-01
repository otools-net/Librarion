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
- **This machine** (16 GB RAM; C: ~26 GB free, **D:\Temp for generated data**, ~220 GB free): SQLite catalogs and
  reports under `D:\Temp\librarion-bench`; PostgreSQL's data sits in Docker's disk image on C:, fine at 2M,
  brought to the user before a 10M PostgreSQL run. Docker stopped after each PostgreSQL run.

## Work items

### 2.1 Schema

- [ ] Entities in `Core/Data/Entities/Catalog/` and `Tags/`, model config, query filters, the two `Initial`
      migrations regenerated ([database](database.md)); `ModelsOfBothProvidersAgree` green.
- [ ] Built-in fields per tenant at tenant creation (`author`, `translator`, `editor`, `illustrator`, `series`,
      `category`, `publisher`, `language`, `year`) and built-in tags (Meta Tags, Archived); closure rows kept.
- [ ] Tests: tenant filters on the new sets, cascade on library / tenant delete, closure for a value and a parent chain.

### 2.2 Generator

- [ ] `tests/Librarion.Benchmarks` console (`generate --provider --connection --books 2000000 --seed`), writing
      through provider-specific bulk writers; counts and `TagCounts` computed at the end; `agents/tools/benchmark/`
      README.
- [ ] Small-size tests (1k books) in `Librarion.Tests`: deterministic for a seed, shapes (Zipf, duplicates, filters).

### 2.3 Catalog queries and API

- [ ] `Core/Catalog/BookQueries` (`Visible`, keyset pages per sort), `TagQueries` (values of a field by sort name,
      with counts), cursor encoding.
- [ ] Routes `GET api/books?library&sort&after` and `GET api/fields/{key}/values?after&prefix`, `library.browse`.
- [ ] Tests: paging walks the whole set once in order (both providers), filters never leak, cursors survive inserts.

### 2.4 Benchmark

- [ ] Queries: first and deep pages per sort, a field value's books (author with 5k books, series by number),
      authors A–Z page, book detail, filtered pages for each allow-list size, `TagCounts` page.
- [ ] Plan checks per provider; budgets; report.
- [ ] Runs: 2M on PostgreSQL and SQLite recorded in this note; 10M as far as the disk allows.

### 2.5 Notes, docs

- [ ] [scale](scale.md) with the measured numbers and the indexes; feature notes (tags, metadata-tags, merging,
      book-display) updated where the schema is now built; AGENTS.md index.

## Exit criteria

p95 budgets met at 2M on PostgreSQL, recorded for SQLite; 10M shows no growth with catalog size; no sequential scan on
big tables; the narrow allow-list case measured (D22: if it breaks the budgets, the user decides on precomputed
visibility).
