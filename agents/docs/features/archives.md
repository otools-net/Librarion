# Books inside archives

Built: `src/Librarion.Core/Containers/` (`ZipDirectory` with `Fingerprint`, `BoundedStreams`, `ArchiveEntryNames`,
`ArchiveEntry`, `ArchiveException`; tests `ZipDirectoryTests`), ported from SFE `src/Sfe.Core/Files/Compression/`
(see its `archive-reading.md`) without the ciphers: an encrypted entry is listed and refused when opened. Every entry
is CRC-checked at its end. Download: `src/Librarion.Core/Reading/BookDownloads.cs`, route in
`Api/Endpoints/BookEndpoints.cs` (tests `BookDownloadTests`). Planned: `ExtractCache`. Reference for
the library side: MyHomeLib (`C:\workspace\MyHomeLib`: `unit_MHLArchiveHelpers.pas`,
`unit_ImportFB2Thread.pas`, `TBookRecord.GetBookStream` in `unit_Globals.pas`).

Libraries are often a few hundred ZIP files each holding **100k+ books** (FB2 mostly, also EPUB, PDF, DJVU, MOBI…).
They are **indexed and read in place: never unpacked to disk**.

## Containers

- A book file is either a **plain file** or an **entry of a container archive**. `Archives` (library, path relative
  to the root, size, mtime, format, entry count, directory fingerprint, scan state) and `BookFiles` (archive id or
  null, entry name, locator = local header offset, method, packed size, size, CRC-32).
- **ZIP (Zip64 included) is the container format of v1.** 7z and RAR only as per-book wrappers (`book.fb2.7z`):
  a solid 7z means decompressing a whole block per book, and RAR has no directory to seek from (SFE measured both).
  Large 7z/RAR containers: refused with "repack as ZIP" (a tool can come later).
- One level of nesting is a **book format**, not a container: an EPUB or CBZ inside a container zip is a zip in a
  zip; deeper archives inside books are not followed.
- Per-book wrappers (`book.fb2.zip`, one entry) are read the same way, the wrapper being the archive.

## Indexing without unpacking

- **List** with our own central-directory reader (SFE `ZipDirectory`): one sequential read at the end of the file,
  no object per entry, memory flat whatever the count (SFE: 5M entries in 35 s, 1M in 11 s, ~50 MB peak).
- **Unchanged archive = skipped:** same size + mtime + directory fingerprint (hash of the central directory) means no
  entry is read. A changed one is diffed by (entry name, CRC-32, size): only new or changed entries are read.
- **Metadata from the head of each entry, not the whole book**, entries read in local-header order (the file is read
  forward, good for disks and NAS):
  - FB2: decompress only until `</description>` (bounded, e.g. 256 KB): title, authors, genres, series, language,
    annotation, ids. The cover (`<binary>` at the end) is **not** read at import.
  - EPUB / CBZ (zip in zip): stored inner entry → seek inside it directly; deflated (usual) → decompress the inner
    book in memory up to a limit (books are small), read its directory, OPF / `ComicInfo.xml`.
  - MOBI/AZW3: EXTH header at the start (head only). PDF: info at the end → whole entry if under a limit, else name
    only. DJVU, TXT, RTF, DOC: name and size only.
- Several archives in parallel, each archive read by one worker; batches of 1000 rows; resumable (an archive's scan
  state says how far it got); progress weighted by bytes, not entry counts (entries differ widely in size).
- A book's identity inside an archive is **(archive, entry name, CRC-32, size)**, never the entry's index
  (MyHomeLib's `InsideNo` breaks when an archive is rebuilt).

## Reading a book from an archive

- **Open = one seek:** the stored locator gives the local header; its name and sizes are checked against the row
  (a mismatch re-lists the archive, the archive's size + mtime are checked first). Data streams through SFE's
  `DeclaredSizeStream` / `WindowStream` (a lying directory or a zip bomb can't overrun).
- **Download** (built, `GET api/books/{id}/files/{fileId}`, right `library.download` in the book's library, the book
  visible to the caller or 404): the entry is streamed decompressed straight to the response, `Content-Length` from
  the directory (set by hand: the stream can't seek), `ETag` = CRC + size (loose files: size + time),
  `Content-Disposition` with the file's name; no temp file. Range for loose files and stored entries (`StoredRange`,
  a slice of the zip); a deflated entry answers a range with the whole file.
- **Checks at open:** an archive whose size differs from the scan's is rebuilt (stale locators): 409 `file.changed`,
  the next scan fixes it; same size (touched) is opened, the local header's signature checked, the CRC at the end
  catches the rest (the response is cut). A loose file of another size: 409 `file.changed`; missing, a link in its
  place, or a path leaving its root: 410 `file.gone`; a method or damage the reader refuses: 409 `file.unreadable`.
- **Planned:** OPDS acquisition (same code), FB2 also offered as `fb2.zip` zipped on the fly (many readers want it),
  Range on deflated entries only from the extract cache.
- **In-browser reading** needs random access into EPUB / CBZ (their own entries) and PDF (ranges): a deflated book is
  decompressed once into the **extract cache** (disk, sharded, LRU with a size cap, e.g. 2 GB, under the server's data
  folder), then served from there; stored entries are read through a window without it.
- **Covers** are made lazily on first view (FB2's `<binary>`, EPUB's cover, first CBZ page) into the cover cache, and
  a background task can pre-make them per archive; never all at import.
- Files are opened per request with `FileShare.Read` (no global lock); many users read the same archive at once.

## Hostile and broken archives (from SFE)

- Entry names through `ArchiveEntryNames` (`..`, roots, drives, control characters refused and listed, never moved
  elsewhere); `MaxEntries` per archive (10M); encrypted entries listed as unreadable; truncated archives keep what was
  read, marked incomplete.
- Head reads and in-memory inner books have size and time limits; XML readers with DTDs off and entity expansion off.
- An archive marked `Incomplete` (broken, cut off) is skipped by later scans until its size or time changes (decided
  2026-11-01): a broken zip isn't read again on every scan.
- `ZipDirectory.OpenEntry` disposes the archive's stream with the entry's unless `leaveOpen: true`: whatever reads
  several entries through one handle (the scanner, `InnerZip`) passes it. Forgetting it cost every entry after the
  first its metadata ("Cannot access a closed file", found 2026-11-01).

## Scale budgets (benchmark, [scale](../development/scale.md))

- List a zip of 100k entries: < 2 s; skip an unchanged one: < 50 ms.
- Index 100k FB2 entries from heads: SSD < 3 min; HDD / NAS measured too (the seeks dominate there), reported with
  the benchmark.
- First byte of a book from a 100k-entry zip: < 50 ms; a deflated EPUB opened in the reader (cold, 5 MB): < 500 ms.
- Measured (`archive` benchmark, [tool](../../tools/benchmark/README.md), SSD, warm): list 73 ms, index 141 s,
  unchanged 4.6 ms, first byte p95 0.2 ms; a 5 200 RPM USB hard disk, cold: list 647 ms, index 154 s, first byte
  p95 24 ms ([scale](../development/scale.md#measured)). Indexing is processor-bound on both; D11 closed.

## Tests to have

- Zip64, > 65 535 entries, code-page and UTF-8 names, every method SFE reads, prepended data, truncated and lying
  directories; a changed archive diffed, an unchanged one skipped; a rebuilt archive (entries moved) still opens the
  right book; download bytes and CRC equal the original; extract cache eviction; hostile names left out.
- Samples: small zips committed (`tests/samples/archives/`); the 100k-entry zips are **generated** by
  `agents/tools/catalog-generator/` for the benchmark, never committed.

## Why not external catalogs (INPX)

Lib.rus.ec-style collections ship an INPX file (a zip of `.inp` text lines with each book's metadata, read by
MyHomeLib) that would spare opening the books. **Not used:** our own indexing reads the books themselves, so it is
always in step with the archives, works for every format and every collection, and needs no third-party file with no
specification. What INPX adds is either in the books too (FB2 genres, series, language) or not needed (the source
library's id and deleted flags; our identity is entry name + CRC + size).

Reconsider only if the phase 2 benchmark shows head indexing too slow on real collections (HDD / NAS), and then as
an optional accelerator that fills the same rows, never a second source of truth.
