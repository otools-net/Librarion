# Books inside archives (planned)

Code (planned): `src/Librarion.Core/Containers/` (`ZipDirectory`, `ArchiveReader`, `BoundedStreams`,
`ArchiveEntryNames`: ported from SFE `src/Sfe.Core/Files/Compression/`, see its `archive-reading.md`),
`src/Librarion.Core/Import/ArchiveScanner`, `src/Librarion.Core/Reading/BookStreams`, `ExtractCache`. Reference for
the library side: MyHomeLib (`C:\workspace\GIT\reference-projects\MyHomeLib`: `unit_MHLArchiveHelpers.pas`,
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
- **Download / OPDS acquisition:** the entry is streamed decompressed straight to the response, with
  `Content-Length` from the directory and `ETag` from the CRC; no temp file. FB2 also offered as `fb2.zip`, zipped on
  the fly (many readers want it). Range requests: served for stored entries (a seek), for deflated ones only from
  the extract cache.
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

## Scale budgets (benchmark, [scale](../development/scale.md))

- List a zip of 100k entries: < 2 s; skip an unchanged one: < 50 ms.
- Index 100k FB2 entries from heads: SSD < 3 min; HDD / NAS measured too (the seeks dominate there), reported with
  the benchmark.
- First byte of a book from a 100k-entry zip: < 50 ms; a deflated EPUB opened in the reader (cold, 5 MB): < 500 ms.

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
