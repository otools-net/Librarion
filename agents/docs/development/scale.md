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
- The benchmark (`tests/Librarion.Benchmarks/`, planned) runs the main queries over the generated 2M catalog and fails
  past the budgets in [foundation-plan](foundation-plan.md#scale-budgets-checked-by-the-benchmark-see-scale); a 10M
  catalog run before each release checks that times grow with the page, not with the catalog.
