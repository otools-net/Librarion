# Scale

2M+ books, 500k authors, 100k series, 50k tags in one library. No per-request work proportional to the catalog:

- Every list (books, authors, series, tags, search results, OPDS feeds) pages **by key** (`after` cursor on the sort
  columns + `Id`), never `OFFSET`; each sort has a matching index. OPDS `next` links carry the cursor.
- Counts on big sets come from denormalized columns (`TagCounts` for authors, series and every tag, …) or are
  capped ("1000+"); never a `COUNT(*)` over the catalog per request.
- Rights and content filters are part of the query (`Visible()`), never a filter after paging.
- Imports and scans write in batches (1000 rows, `ChangeTracker.Clear()` between), resume after a stop, and skip
  unchanged files by size + mtime (+ partial hash on doubt).
- Rows by the thousand whose keys are known go past EF (`Data/BulkRows`: PostgreSQL binary `COPY`, ids from the
  sequence; SQLite prepared inserts in a transaction), and counters move by one upsert per batch, never a row per
  counter through EF ([imports](../features/imports.md#scale)).
- During a big write PostgreSQL's statistics lag behind: prefer plain indexed lookups to joins in per-batch queries.
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
- Archives (stage 3, `archive` command, 2026-11-01, NVMe SSD, warm file cache, SQLite): a 100k-entry FB2 zip
  (1 GB, 30 KB books) listed in 73 ms (budget 2 s), indexed from heads in 141 s, 709 books a second (budget 3 min:
  the closest to its limit), the unchanged library scanned in 4.6 ms (50 ms), first byte p95 0.2 ms (50 ms), a
  generated cover (m) p95 14 ms.
- Same zip on a **5 200 RPM USB hard disk** (WD20EZRX, 64 MB cache; about 10 MB/s written over its USB link), cold
  (`--evict`: a 24 GB file read before the cold phases), database on the SSD: list 647 ms, index 154 s (651 a second:
  processor-bound, the disk reads forward), unchanged 4.7 ms, first byte p50 9.6 ms / p95 24 ms (real seeks). All
  within budget: folders stay indexed from the books (D11); INPX and Calibre come in as manual imports (stage 4).
- Imports (stage 4, `import` command, 2026-11-01, PostgreSQL): Calibre 2M books 27.1 min, unchanged again 2.6 min;
  INPX 1M records 8.9 min, unchanged 38 s; all within budget ([imports](../features/imports.md#scale)).
- Search (stage 5, 2026-11-03, 2M, warm, p95 in ms, budget 300; how each plan was found:
  [search](../features/search.md#plans-that-went-wrong-at-2m-2026-11-03-and-the-rule-each-gave)):

  | Search | 2M SQLite | 2M PostgreSQL |
  |---|---|---|
  | a word on ~6 % of titles, first and middle page | 75–77 | 103–131 |
  | two such words | 151 | 188 |
  | two letters (a prefix of many words) | 86 | 129 |
  | a file name (one book) | 10 | 28 |
  | the most prolific author's last name | 32 | 69 |
  | an author with 5–20 books, by field | 8 | 22 |
  | an author by word and a title word | 188 | 118 |
  | category Fiction less PDF, by author | 6 | 27 |
  | books outside any series, newest | 3 | 17 |
  | suggestions: two letters / after `author:` | 20 / 24 | 17 / 22 |

- Facets (stage 5, 2026-11-05, 2M, warm, p95 in ms, budget 300; counted over at most 2,000 matches, see
  [search](../features/search.md#facets-searchfacets-refine-d-58)):

  | Facets of | 2M SQLite | 2M PostgreSQL |
  |---|---|---|
  | the library seen whole (stored counts) | 104 | 41 |
  | a word on ~2 % of titles / two such words | 148 / 145 | 158 / 272 |
  | the prolific author's last name | 184 | 247 |
  | an author with 5–20 books, by field | 12 | 31 |
  | category Fiction less PDF | 103 | 82 |
  | books outside any series | 188 | 138 |
  | the library for the 0.1 % / 1 % / 10 % groups | 188 / 228 / 223 | 128 / 108 / 131 |

  At 10,000 matches (D-5.8's first figure) SQLite took 480–780 ms for the broad ones, 5,000 290–420 ms. The 1 %
  group's library first took 305 ms on SQLite (its 20k books sorted by title to take 2,000): a filter read from its
  own tags is now sampled unordered (`BookQueries.StartsFromTagsAsync`).
- Stage 5 close (2026-11-05, 2M, warm, p95 in ms): every call of the UI's screens, 49 scenarios, within budget on
  both providers, no full scan. Besides the tables above:

  | Query | 2M SQLite | 2M PostgreSQL |
  |---|---|---|
  | filtered group 0.1 % / 1 % / 10 %, first and middle page | 38–39 / 40–54 / 10–17 | 25–29 / 31–39 / 13–18 |
  | the field list (All fields) | 0.2 | 2 |
  | plain tags A–Z, first page | 6 | 70 |
  | authors from a letter (Go to) | 4 | 16 |
  | a tag's page (an author, a category) | 1 | 6–7 |
  | possible duplicates | 3.5 | 12 |

- The **1 % filtered group** took 100–127 ms on PostgreSQL at the start of the stage close (62 at stage 2): on the
  freshly generated catalog PostgreSQL estimates its tag at 744 links (it has 20k) and turned the probe's `EXISTS`
  into a join started from the tag, sorting 20k books. The probe is now a count on PostgreSQL (a scalar subquery is
  not turned into a join), so `NarrowAsync`'s choice holds: 31–39 ms.
- Imports with the search index kept row by row (2026-11-03, PostgreSQL): INPX 1M 9.2 min (8.9 before), Calibre 2M
  28.2 min (27.1; budget 30), unchanged re-imports 37 s and 2.7 min: within budget.
- Imports with parents' totals kept (`TagCounts.TotalCount`, stage 6 4a, 2026-11-05, PostgreSQL): INPX 1M 9.0 min,
  Calibre 2M 28.4 min (budget 30), unchanged re-imports 38 s and 2.7 min: the same as before.
- The folder view (stage 6 chunk 8b, 2026-11-06, 2M PostgreSQL, warm, p95 in ms): a folder of 1,000 folders 3.9, a
  folder with its path 10.2 (budget 50), books of a ZIP of 100k entries 22.1 (middle page 25.5: the sort index walked,
  each book tested by its files), books of a folder of ~1,000 loose files by author 23.3 (from the folder's files,
  sorted): within budget, no full scans. Generation 15.3 min with `Folders` (other work ran meanwhile).
- Imports with folders and their counts (stage 6 chunk 8b, 2026-11-06, PostgreSQL): INPX 1M 9.1 min (9.0), Calibre
  2M 28.9 min (28.4; budget 30), unchanged re-imports 38 s and 2.7 min. Within budget, but Calibre 2M has about a
  minute left: the next change to the import path should be measured before it is kept.
- Imports with default tags wired in (stage 6 chunk 8c, 2026-11-06, PostgreSQL, a library without any): INPX 1M 9.1
  min, Calibre 2M 28.9 min, unchanged re-imports 38 s and 2.6 min: unchanged.
- Generation with the search index (2026-11-03): 2M SQLite 7.5 min (3.2 before: the FTS5 triggers on every insert),
  PostgreSQL 15 min (the GIN indexes rebuilt after the load; other work ran on the machine meanwhile).
- What the benchmark found: the tenant filter as an `OR` (now two models), no index for sorting every library by
  author or date, fields found by scanning `BuiltIn`, tag pages joined one way for every size (before: Fiction 2.9 s, a
  0.1 % filter 0.8 s), parallel workers on PostgreSQL, a big series by number walked from its title.
