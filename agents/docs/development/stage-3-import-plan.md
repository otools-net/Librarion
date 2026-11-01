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
- [ ] Tasks UI: roots, scan now with progress, history, schedules in the library editor.

### 3.3 Scanner

- [x] Folder walk, loose files, containers listed and diffed, batched writes, resumable, deletions of missing files
      (`Core/Import/LibraryScanner`, `CatalogWriter`, `ImportTagResolver`, `BookFormats`; `LibraryScannerTests`).
- [x] Titles with the file-name fallback, `WorkKey`, `TagCounts` and `BookCount` kept by differences.

### 3.4 Metadata readers

- [x] FB2, EPUB, CBZ, MOBI / AZW3, PDF; hostile samples (XML bombs, huge heads) ([metadata-readers](../features/metadata-readers.md)).
- [x] Field resolver (`author`, `series` + number, `category`, `language`, `publisher`, `year`), `ImportTagMappings`.

### 3.5 Covers

- [ ] Extraction per format, cover cache, thumbnails, generated templates (long titles, other scripts, RTL), route
      `GET api/books/{id}/cover/{size}`.

### 3.6 Duplicates at import, download

- [ ] Identical files merged, formats of one work joined; download route with Range and ETag.

### 3.7 Benchmark, e2e, notes

- [ ] Generated 100k-entry zip: list < 2 s, unchanged skip < 50 ms, index from heads < 3 min, first byte < 50 ms;
      HDD / NAS measured (D: here) for D11.
- [ ] e2e: add a library with a root, scan with progress, open a book page, download from a zip.
- [ ] Feature notes (archives, book-display, tags, metadata-tags, merging), docs (libraries, tasks), CHANGELOG.

## Exit criteria

100k-entry zip indexed within budget, unchanged archive skipped, changed one diffed; hostile samples and images
refused; every template renders long titles and other scripts; HDD / NAS measured (closes D11); e2e: add library,
scan with progress, download from a zip.
