# Stage 3: import from folders and ZIP containers (plan)

Roadmap: [foundation-plan](foundation-plan.md) (D1, D10, D11, D15, D17, D18, D19; archive budgets). Goal: **real
books in, at scale, without unpacking**. Version stays on `edge`; the merge into `main` makes it `0.4.0`.

## Decisions for this stage

- **ZIP reader ported from SFE** (`C:\workspace\SimpleFileExplorer\src\Sfe.Core\Files\Compression\`): `ZipDirectory`
  (central directory read record by record, Zip64, prepended data, UTF-8 / code page 437 names), `WindowStream`,
  `DeclaredSizeStream`, CRC check at the end, `ArchiveEntryNames`. Encryption is not ported: an encrypted entry is
  listed as unreadable (books are not encrypted). Methods: stored and deflate built in, Deflate64 and BZip2 / LZMA /
  others through SharpCompress (MIT, as SFE; also reads the per-book `.7z` / `.rar` wrappers).
- **Image library (D17): SkiaSharp + HarfBuzzSharp** (MIT, no telemetry, native assets for win / linux / osx on x64
  and arm64): decode, resize, encode WebP / JPEG thumbnails, draw the generated cover templates with shaped text.
- **Libraries get roots** (`LibraryRoots`: absolute folder; several per library); paths of books are relative to a
  root; a root that is missing at scan time is not a deletion (its books stay, marked unreachable).
- **Background tasks** as SFE (`TaskRuns` history, one run per library scan at a time, progress, cancel, interrupted
  at start-up; schedules: per library, daily / weekly / every n hours).
- **Scanner**: walks the roots (no symlink loops), loose books by extension, ZIP containers by content; batches of
  1000 rows; unchanged file = same size + mtime; unchanged archive = same size + mtime + directory fingerprint; a
  changed archive diffed by (entry name, CRC-32, size).
- **Metadata from heads**: FB2 (`<description>`, bounded), EPUB (OPF), CBZ (`ComicInfo.xml`), MOBI / AZW3 (EXTH),
  PDF (info dictionary when small), others name only; XML with DTDs and entity expansion off; values go through
  the field resolver into tags (D18) and plain tags through `ImportTagMappings`.
- **Covers**: extracted lazily on first view into the sharded cover cache (`s` / `m` / `l` WebP), a generated
  template (template, variant) assigned at import to every book; rendered on first view when the book has none.
- **Duplicates at import** (D19): identical files (size + CRC-32, then content hash) become one book with several
  copies; same identifier or same `WorkKey` merged only when the formats differ (an EPUB and an FB2 of the same work);
  everything else is left for stage 6.
- **Download** `GET api/books/{id}/files/{fileId}`: streamed from the file or the archive entry (no temp file),
  `Content-Length`, `ETag` from the CRC, Range for loose files and stored entries; right `books.download`.

## Work items

### 3.1 Containers

- [x] `Core/Containers/`: `ZipDirectory`, bounded streams, `ArchiveEntryNames`, `ArchiveException`; SharpCompress for
      other methods and wrappers.
- [x] Tests with zips written in the test (`System.IO.Compression`, hand-made bytes for Zip64, prepended data, lying
      and truncated directories, code page 437 names, > 65 535 entries).

### 3.2 Libraries, roots, tasks

- [x] `LibraryRoots` (validated: exists, not inside or around another root, allowed folders for tenant administrators).
- [x] `TaskRuns`, `BackgroundTasks` (scan per library), schedules; API ([tasks](../features/tasks.md)).
- [x] Tasks UI: roots, scan now with progress, history, schedules in the library panel (`admin/library-panel.ts`).

### 3.3 Scanner

- [x] Folder walk, loose files, containers listed and diffed, batched writes, resumable, deletions of missing files
      (`Core/Import/LibraryScanner`, `CatalogWriter`, `ImportTagResolver`, `BookFormats`; `LibraryScannerTests`).
- [x] Titles with the file-name fallback, `WorkKey`, `TagCounts` and `BookCount` kept by differences.

### 3.4 Metadata readers

- [x] FB2, EPUB, CBZ, MOBI / AZW3, PDF; hostile samples (XML bombs, huge heads) ([metadata-readers](../features/metadata-readers.md)).
- [x] Field resolver (`author`, `series` + number, `category`, `language`, `publisher`, `year`), `ImportTagMappings`.

### 3.5 Covers

- [x] Extraction per format, cover cache, thumbnails, generated templates (long titles, other scripts, RTL), route
      `GET api/books/{id}/cover/{size}` ([book-display](../features/book-display.md); CJK and Indic fonts added).

### 3.6 Duplicates at import, download

- [x] Download route with Range and ETag (`Core/Reading/BookDownloads`, `BookDownloadTests`; [archives](../features/archives.md)).
- [x] Identical files merged at import (`CatalogWriter.CopiesAsync`; [merging](../features/merging.md)).
- [ ] Formats of one work joined (same identifier or `WorkKey`): optional in this stage, else stage 6.

### 3.7 Benchmark, e2e, notes

- [x] Generated 100k-entry zip: list < 2 s, unchanged skip < 50 ms, index from heads < 3 min, first byte < 50 ms
      (`archive` command; SSD: 73 ms, 4.6 ms, 141 s, 0.2 ms).
- [ ] HDD / NAS measured for D11: this machine has only an NVMe SSD (C: and D: are partitions); needs a disk from the
      user.
- [x] e2e: add a library with a root, scan, see the run, download from a zip, get a cover (`specs/library.spec.ts`;
      the book page is stage 5, the API stands in; a 2-book scan ends before its progress can be seen).
- [ ] Feature notes (archives, book-display, tags, metadata-tags, merging), docs (libraries, tasks), CHANGELOG.

## Exit criteria

100k-entry zip indexed within budget, unchanged archive skipped, changed one diffed; hostile samples and images
refused; every template renders long titles and other scripts; HDD / NAS measured (closes D11); e2e: add library,
scan with progress, download from a zip.

## Handover (2026-11-01, end of a session)

**State.** `main` = 0.3.0 (stage 2 merged and pushed). `edge` is 7 commits past it, the last `1f6465b`, **not
pushed** (edge is pushed when the stage ends; push earlier if you want a backup). Working tree clean. Last runs: 101
backend tests and 47 client tests green on SQLite. PostgreSQL was last run after the scanner commit (`bd87f70`), not
since: run it before the next commit (commands below). Docker and WSL are stopped.

**Done in stage 3** (details in the linked notes):

- 3.1 ZIP reader `Core/Containers/` ([archives](../features/archives.md)).
- Schema: `LibraryRoots`, `Archives.RootId`, `BookFiles.RootId`, `TaskRuns`, `TaskSchedules`, `ImportTagMappings`
  (with `FieldId`). Initial migrations regenerated with [regen-migrations](../../tools/regen-migrations/README.md).
- 3.3 scanner, 3.4 metadata readers ([metadata-readers](../features/metadata-readers.md)), 3.2 roots, background
  runs, schedules, API and the library panel UI ([tasks](../features/tasks.md)).
- Rights `library.download` (for 3.6) and `library.scan`. CHANGELOG `Unreleased` and `docs/administration/libraries.md`,
  `docs/configuration.md` (`Librarion:Libraries:*`) already cover what is built.

**Next, in this order:**

Done since (2026-11-01, second session): items 1 and 2 below (download, identical files), and a scanner fix: entries
after the first of a zip lost their metadata because `OpenEntry`'s stream closed the archive (now `leaveOpen`).
Then item 3 (covers), the format registry (`Core/Formats/`, [book-formats](../features/book-formats.md): the user
wants formats added gradually) and one configurable cache folder (`Librarion:Cache:Directory`, the user's rule).
Then item 4 (the benchmark on the SSD) and the e2e spec `library.spec.ts`. Start at item 6.

1. ~~**3.6 download**~~ (done) (small, needed by the e2e): `GET api/books/{id}/files/{fileId}`, right `library.download`, the
   book visible to the caller (`BookVisibilities`, else 404). Loose file: from root + path, check it is still inside
   the root. Archive entry: open the archive (root + `Archives.Path`), build an `ArchiveEntry` from the row (locator,
   method, packed size, CRC; as `ScannedLibrary.ReadEntry` in `LibraryScannerTests` does) and
   `ZipDirectory.OpenEntry`. A local header that doesn't match means a rebuilt archive: answer 409/410 and let the
   next scan fix it. `Content-Length` from the size, `ETag` from the CRC, `Content-Disposition` with the file name,
   Range for loose files and stored entries (`StoredRange`). Never a temp file.
2. ~~**3.6 identical files at import**~~ (done, same library only, archive entries only) (D19, [merging](../features/merging.md)): in `CatalogWriter.AddAsync`, an
   archive entry whose (size, CRC) matches a file of the tenant (index `BookFiles (TenantId, Size, Crc32)`) is
   compared by SHA-256 (`ContentHash`, computed only then) and added as a second file of the existing book instead
   of a new book. Joining different formats of one work (same identifier or `WorkKey`) is optional in this stage.
3. ~~**3.5 covers**~~ (done) ([book-display](../features/book-display.md)): add SkiaSharp + HarfBuzzSharp to
   `Directory.Packages.props` (check the NuGet cache in `~/.nuget/packages` first; check the native assets for
   linux-x64/arm64; privacy: no telemetry). Cover extraction per format (FB2 `<coverpage>` → `<binary>`, read to the
   end of the file this time; EPUB cover from the OPF manifest; CBZ first image; PDF none in v1), a sharded disk cache
   under the data folder (`ab/cd/rest`, sizes s/m/l, WebP), generated templates (`GeneratedTemplate`,
   `GeneratedVariant` assigned at import in `CatalogWriter.Apply`, which leaves them null today), a route
   `GET api/books/{id}/cover/{size}` with long cache headers. Hostile images: a size limit before decoding,
   dimension limits.
4. ~~**3.7 benchmark**~~ (done on the SSD; HDD / NAS waits for a disk): a generator of a 100k-entry FB2 zip (in `tests/Librarion.Benchmarks`, output to `D:\Temp`),
   timed list, unchanged skip, index from heads, first byte. Run it on C: (SSD) and D:. **Ask the user** whether D:
   is an HDD/NAS that may stand in for D11, or which disk to use.
5. **e2e** (`tests/e2e`, see [testing-e2e](testing-e2e.md)): add a folder under the e2e work folder, scan, see the
   progress and the run, download a book from a zip. The e2e build needs a ZIP made at test time: Node has no zip
   writer, so write the sample from the server side or commit a tiny zip under `tests/e2e/samples/`.
6. **Close the stage**: update the feature notes (archives, book-display, tags, metadata-tags, merging: they still say
   "planned" in parts), move what stays true out of this plan, delete it with its AGENTS.md line, run check-links, run
   both providers, `npm test`, the e2e grep set; merge into `main` as **0.4.0**, then merge `main` back into `edge`
   and push both ([versioning-and-changelog](versioning-and-changelog.md)). Then write the stage 4 plan (Calibre
   import) from the [foundation-plan](foundation-plan.md).

**Decisions taken this session (the user may revise):**

- Only server administrators add folders unless `Librarion:Libraries:Folders` lists allowed ones.
- At most 2 scans at once (`MaxParallelScans`).
- Image library: SkiaSharp + HarfBuzzSharp (D17's leading option, not installed yet).
- FB2 genres are named in English (`Fb2Genres`: written from memory, then completed from MyHomeLib's
  `genres_fb2_en.glst` once it was found in `C:\workspace\MyHomeLib`; 295 codes).
- An archive marked `Incomplete` (broken) is skipped until its size or time changes.
- New plain tags go under the "Imported: <source>" category; field values go straight under their field.

**Gotchas met this session:**

- XmlReader loops: `x ??= F(SafeXml.Text(xml))` doesn't move the reader once `x` is set and hangs. Read first, then
  assign. Use `--blame-hang-timeout`, and run long suites as `timeout 550 dotnet test ... > log` ([testing-backend](testing-backend.md)).
- Angular templates have no bitwise operators: use a method.
- `rm` of a glob under the repo is stopped by the agent's safety check: use `git rm` of listed names (the
  regen-migrations tool does).
- Bash heredocs containing apostrophes break inside `python - <<'EOF'` when the command also holds other quoting:
  write the script with the Write tool into the scratchpad and run it.
- Error codes sent to the client are `field.code` (`path.notAllowed`); `ClientI18nTests` reads them from the access
  services and `TaskEndpoints.cs`. A new endpoint file with codes must be added there.
- `ZipDirectory.OpenEntry` disposes the archive stream with the entry's stream unless `leaveOpen: true`: anything
  reading several entries through one handle must pass it.
- PostgreSQL keeps microseconds: times compared with stored ones are cut to microseconds (`LibraryScanner.Moment`).

**Commands** ([commands](../environment/commands.md), [dev-machine](../environment/dev-machine.md)):

```sh
timeout 550 dotnet test --blame-hang-timeout 60s > /d/Temp/all-test.log 2>&1; grep -E "Passed!|Failed!" /d/Temp/all-test.log
# PostgreSQL: start Docker Desktop first (PowerShell: Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe", wait for docker info)
LIBRARION_TEST_POSTGRES="$(agents/tools/postgres-dev/start.sh)" dotnet test --no-build
# then stop Docker Desktop's processes and run wsl --shutdown (the machine has little memory)
cd src/Librarion.Web && timeout 400 npm test > /d/Temp/npm-test.log 2>&1
node agents/tools/screenshots/screenshots.mjs /d/Temp/screens admin/libraries#open   # look at a page by eye
```
