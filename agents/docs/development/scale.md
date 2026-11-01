# Scale

2M+ books, 500k authors, 100k series, 50k tags in one library. No per-request work proportional to the catalog:

- Every list (books, authors, series, tags, search results, OPDS feeds) pages **by key** (`after` cursor on the sort
  columns + `Id`), never `OFFSET`; each sort has a matching index. OPDS `next` links carry the cursor.
- Counts on big sets come from denormalized columns (`TagCounts` for authors, series and every tag, …) or are
  capped ("1000+"); never a `COUNT(*)` over the catalog per request.
- Rights and content filters are part of the query (`Visible()`), never a filter after paging.
- Imports and scans write in batches (1000 rows, `ChangeTracker.Clear()` between), resume after a stop, and skip
  unchanged files by size + mtime (+ partial hash on doubt).
- Covers and thumbnails are made lazily into a sharded disk cache (`ab/cd/rest`), never at import for every book.
- Order every `Take`; a query on a big table that can't use an index is a bug.
- Keyset conditions are written `sort >= x AND (sort > x OR id > y)`: the redundant `>=` gives the index its start
  (the bare `OR` form made PostgreSQL walk the index from the beginning on deep pages).
- Query filters must be plain equalities (`TenantId = @t`): a filter with an `OR` on a parameter defeats every index
  ([tenants](../features/tenants.md#isolation-in-the-database)).
- Lists over every library of a tenant use `(TenantId, sort, Id)` indexes, one library's `(LibraryId, sort, Id)`.
- Long strings (paths) are indexed through a 64-bit hash column (`PathKeys`), compared after.
- Books with a tag (content filters, a field value's page) are joined by size: walk the sort index and test each book
  for a broad tag, start from the tag's links and sort for a narrow one (under √(page × books), from `TagCounts`);
  descendants listed first so a test is one seek (`BookQueries.NarrowAsync`, [catalog](../features/catalog.md)).
- PostgreSQL connections run without parallel workers (`LibrarionDatabase.WithoutParallelWorkers`): at 10M the
  planner overestimated small lookups and paid for starting workers on each of a page's queries.
- The benchmark (`tests/Librarion.Benchmarks/`, [tool](../../tools/benchmark/README.md)) runs the main queries over the generated 2M catalog and fails
  past the budgets in [foundation-plan](foundation-plan.md#scale-budgets-checked-by-the-benchmark-see-scale); a 10M
  catalog run before each release checks that times grow with the page, not with the catalog.

## Measured

Stage 2 benchmark (2026-11-01, this machine: 16 GB RAM, the PostgreSQL dev container of
[postgres-dev](../../tools/postgres-dev/start.sh)), warm, p95 in ms; budget 100 for lists, 50 for detail.

| Query | 2M SQLite | 2M PostgreSQL | 10M SQLite | 10M PostgreSQL |
|---|---|---|---|---|
| books by title / author / newest, first and middle pages | 5–9 | 11–16 | 3.5–9 | 11–18 |
| one library by title, middle page | 8 | 15 | 7 | 17 |
| most prolific author | 24 | 26 | 27 | 35 |
| author with 5–20 books | 4 | 18 | 4 | 21 |
| biggest series, by number | 8 | – | 7 | 18 |
| biggest series, by title | 81 | 50 | **219** | **111** |
| category Fiction with subcategories | 7 | 30 | 8 | 23 |
| authors A–Z, first and middle | 1–2 | 8 | 2 | 5–6 |
| book detail | 3 | 11 | 6.5 | 9 |
| filtered group 0.1 % / 1 % / 10 % | 48 / 42 / 12 | 33 / 62 / 22 | **160** / 72 / 20 | 68 / 31 / 19 |

- 2M: 15.4M book–tag links, 2.4M files, 660k tags. 10M: five times that.
- No full scan of a big table on either provider at either size.
- Plain lists, field values and detail don't grow with the catalog. Narrow-tag pages by title grow with
  √(page × books) (bold: past the budget at 10M); accepted by the user for now, fallbacks in
  [catalog](../features/catalog.md#known-limits-accepted-2026-11-01-measure-again-before-the-release).
- Generation: 2M SQLite 3.2 min, PostgreSQL 7.4 min (the `COPY` of `BookTags` checks its foreign keys); 10M SQLite
  18 min.
- What the benchmark found: the tenant filter as an `OR` (now two models), no index for sorting every library by
  author or date, fields found by scanning `BuiltIn`, tag pages joined one way for every size (before: Fiction 2.9 s, a
  0.1 % filter 0.8 s), parallel workers on PostgreSQL, a big series by number walked from its title.
