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
  descendants listed first so a test is one seek (`BookQueries.NarrowAsync`, measured in the
  [stage 2 plan](stage-2-catalog-plan.md#results)).
- The benchmark (`tests/Librarion.Benchmarks/`, [tool](../../tools/benchmark/README.md)) runs the main queries over the generated 2M catalog and fails
  past the budgets in [foundation-plan](foundation-plan.md#scale-budgets-checked-by-the-benchmark-see-scale); a 10M
  catalog run before each release checks that times grow with the page, not with the catalog.
