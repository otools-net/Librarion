# Scale benchmark

`tests/Librarion.Benchmarks`: generates real-shaped catalogs of millions of books and measures the main queries
against the budgets ([scale](../../docs/development/scale.md), [catalog](../../docs/features/catalog.md)).
Generated databases and reports go to `D:\Temp\librarion-bench` on this machine (never the repo).

```sh
dotnet build tests/Librarion.Benchmarks -c Release
bench=tests/Librarion.Benchmarks/bin/Release/net10.0/Librarion.Benchmarks.dll

# SQLite: a fresh file (deleted first)
dotnet $bench generate --provider sqlite --connection "Data Source=D:/Temp/librarion-bench/sqlite-2m.db" --books 2000000

# PostgreSQL: start Docker Desktop, then the database named in the string is dropped and made fresh
cs="$(agents/tools/postgres-dev/start.sh | sed 's/Database=postgres/Database=librarion_bench_2m/')"
dotnet $bench generate --provider postgres --connection "$cs" --books 2000000
# afterwards: docker stop librarion-pg, quit Docker Desktop, wsl --shutdown (little memory)
```

| Option | |
|---|---|
| `--books` | catalog size (default 2,000,000); the rest scales with it (authors ÷ 4, series ÷ 20, publishers ÷ 200, tags ÷ 40 up to 50k) |
| `--seed` | same seed, same catalog (default 1) |
| `--work` | the work folder (setup-code file, reports); default `<tmp>/librarion-bench` |

## What `generate` makes

- One tenant "Bench" with its library "Books", migrated by Core's own migrations, then bulk-loaded: PostgreSQL binary
  `COPY`, SQLite prepared inserts in transactions of 200k rows; the big tables' secondary indexes dropped before and
  rebuilt after, `ANALYZE` at the end.
- Per book (planned from its id and the seed: `CatalogGenerator.Plan`): a title (3 % none: the file name shows),
  0–3 authors (Zipf 0.8), a series in 30 % with numbers (some .5), 1–3 categories under 30 genres, a language, a
  publisher (70 %), a year (80 %), 0–5 messy plain tags (Zipf 1.0), ISBN / FB2 id (40 % each), files: 60 % FB2 in
  container archives (100k entries each), the rest EPUB loose, 20 % a second PDF.
- Planted duplicates: same file (size + CRC) every 100th book, same ISBN, same work (title + author) every 50th.
- Filter tags on 0.1 %, 1 % and 10 % of the books, one group with an account (`filter-0`, `filter-1`, `filter-2`,
  password `benchmark-password`) limited to each (D22's narrow allow-lists).
- `TagCounts` (direct, parents, fields once per book) and `Libraries.BookCount`.
- Small-size tests: `CatalogGeneratorTests` (3,000 books, both providers).

## What `run` measures

```sh
dotnet $bench run --provider sqlite --connection "Data Source=D:/Temp/librarion-bench/sqlite-2m.db" [--iterations 20]
```

- 20 queries through Core's own services (`BookQueries`, `TagQueries`, `BookVisibilities`): first and middle pages
  by title, author and newest first (all libraries and one), the books of the most prolific author, of an author
  with 5–20 books, of the biggest series, of category Fiction with its subcategories, authors A–Z (first and
  middle), book detail on random ids, and the pages of the three filtered groups (0.1 %, 1 %, 10 %).
- Each: 3 warm-up runs, then `--iterations` timed; p50, p95, max against the budget (list 100 ms, detail 50 ms).
- Every SQL command a query runs is captured (EF's diagnostic events) and explained with its parameters:
  PostgreSQL `EXPLAIN` (a `Seq Scan` on a big table fails), SQLite `EXPLAIN QUERY PLAN` (a bare `SCAN` of a big table
  fails; `SCAN … USING INDEX` is the keyset walk).
- Report `bench-<provider>-<books>-<time>.json` and every plan in `…-plans.txt` in the work folder. Exit 1 on a full
  scan, or on PostgreSQL past a budget (SQLite's times are recorded, not enforced).
- Run nothing else heavy at the same time: the timings are the machine's.
