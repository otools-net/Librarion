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
- `TagCounts`: `BookCount` the direct links, `TotalCount` the tag with its parent and field once per book (as the
  importer keeps them, stage 6 4a), and `Libraries.BookCount`.
- `ImportTagMappings`: one automatic FB2 mapping per plain tag, category, language, publisher and year, as an import
  would have recorded them (stage 6 chunk 9; none for people and series).
- Small-size tests: `CatalogGeneratorTests` (3,000 books, both providers).

## What `run` measures

```sh
dotnet $bench run --provider sqlite --connection "Data Source=D:/Temp/librarion-bench/sqlite-2m.db" [--iterations 20] [--only "search:"]
```

- 49 queries through Core's own services, the ones the UI's routes call (`BookQueries`, `TagQueries`,
  `BookVisibilities`, `SearchSuggestions`, `SearchFacets`): first and middle pages by title, author and newest first
  (all libraries and one), the books of the most prolific author, of an author with 5–20 books, of the biggest
  series, of category Fiction with its subcategories, authors A–Z (first and middle, and from a letter as Go to), the
  field list, plain tags A–Z, a tag's page, book detail and possible duplicates on random ids, and the pages of the
  three filtered groups (0.1 %, 1 %, 10 %). Searches (stage 5, budget 300 ms): a word on about 2 % of the titles
  (first and middle page), two such words, a two-letter prefix, a file name (one book), an author's last name, an
  author by field and by word with a title word, a category less a format, books outside any series; suggestions for
  two letters and after `author:`; facets (Refine) of the library, of those searches and of each filtered group's
  library. The Imported tags page (stage 6 chunk 9, `--only "imported"`): plain tags by books (first page, the page
  after the 5,000th, a status none has: the walk's bound), languages and categories (small fields), the coverage
  line, the fields; the run also reports how much of the imported links the first 500 plain tags and categories hold.
- `--only <text>`: only the scenarios whose name contains it (a slow one measured with its plans, alone).
- Each: 3 warm-up runs, then `--iterations` timed; p50, p95, max against the budget (list 100 ms, detail 50 ms).
- Every SQL command a query runs is captured (EF's diagnostic events) and explained with its parameters:
  PostgreSQL `EXPLAIN` (a `Seq Scan` on a big table fails), SQLite `EXPLAIN QUERY PLAN` (a bare `SCAN` of a big table
  fails; `SCAN … USING INDEX` is the keyset walk).
- Report `bench-<provider>-<books>-<time>.json` and every plan in `…-plans.txt` in the work folder. Exit 1 on a full
  scan, or on PostgreSQL past a budget (SQLite's times are recorded, not enforced).
- Run nothing else heavy at the same time: the timings are the machine's.

## What `archive` measures (stage 3, [archives](../../docs/features/archives.md#scale-budgets-benchmark-scale))

```sh
dotnet $bench archive --provider sqlite --connection "Data Source=D:/Temp/librarion-bench/archive-sqlite.db" \
  --work D:/Temp/librarion-bench [--entries 100000] [--body-kb 30] [--samples 500]
```

- A zip of `--entries` FB2 books (real description heads, a body of `--body-kb` KB, deflated) in
  `<work>/archive-<entries>-<body>k/`, made once and kept (deterministic: same options, same file).
- Then, into a fresh database (the target is recreated): the zip's directory listed (`ZipDirectory`), the library
  scanned by the real `LibraryScanner` with the real metadata readers (index from heads), scanned again unchanged,
  the first byte of `--samples` random books (`BookDownloads.Open`, from the row as the route has it), and the cover of
  100 of them (none in the books: looked for, then generated).
- Budgets: list < 2 s and index < 3 min (only at 100k entries or more), unchanged scan < 50 ms, first byte p95
  < 50 ms; exit 1 past one. Report `archive-<provider>-<entries>-<time>.json` in the work folder.
- Without `--evict` the zip was just written or read: the system's file cache is warm. `--evict <file>` (`--evict-gb`,
  default 24: more than this machine's 16 GB) reads a scratch file before the list and before the first bytes, so the
  zip leaves the cache without administrator rights. Read it **without** `SequentialScan`: with it Windows recycles
  the scratch file's own pages and the zip stays cached (first byte 0.1 ms: not cold). Cold looks like list ~0.6 s and
  first byte ~10 ms on a hard disk. Delete the scratch file after (`D:\Temp\librarion-evict.bin`).
- Hard disk: `--work F:/claude-test` (the user's USB HDD, 2026-11-01) with the database on the SSD; copy the zip there
  rather than generating it (the disk writes ~10 MB/s).
- Small-size test: `ArchiveBenchTests` (500 entries, both providers).

## What `import` measures (stage 4, [imports](../../docs/features/imports.md#scale))

```sh
agents/tools/benchmark/import.sh postgres [inpx-records 1000000] [calibre-books 2000000] [work D:/Temp/librarion-bench]
# one source: dotnet $bench import --provider sqlite|postgres --connection "…" --source inpx|calibre --records N --work D:/Temp/librarion-bench
```

- The source is generated in `<work>/import-<source>-<records>/` from the catalog generator's book plans (same authors,
  series, tags, languages by Zipf), made once and kept: a Calibre `metadata.db` with one empty EPUB per book (a folder
  per thousand books: 2M files take a while to create, once), or an `.inpx` with `.inp` files of 10,000 records and
  their zips (stored one-byte entries; one record in a hundred `DEL=1`).
- Into a fresh database (the target is recreated): the source's folder added as a root, the real `CatalogImporter`
  run, then run again with the source unchanged (must write nothing).
- Budgets (checked only at their sizes): Calibre 2M < 30 min, INPX 1M < 15 min, each unchanged re-import < 5 min;
  exit 1 past one. Report `import-<source>-<provider>-<records>-<time>.json` in the work folder.
- `import.sh` builds the benchmark in Release, starts the PostgreSQL container for `postgres` (Docker Desktop must
  run), and runs INPX then Calibre (0 skips one).
