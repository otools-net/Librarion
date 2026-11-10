# Format conversion (planned)

Code (planned): `src/Librarion.Core/Conversion/` (`IConverter`, `ConversionMatrix`, `ConversionQueue`,
`ConversionCache`; built-in converters `Fb2ToEpub`, `EpubToKepub`, `EpubToAzw3`; `CalibreConverter`),
`Api/Endpoints/ConversionEndpoints`. Decision D24 in [foundation-plan](../development/foundation-plan.md) (options
weighed and chosen by the user on 2026-11-01).

A book is downloaded **in the format the device reads**, even when the library only has another one: an FB2 from an
archive library as AZW3 for a Kindle, an EPUB as KEPUB for a Kobo. Converted **on demand**, never up front.

## Engines: built-in first, Calibre optional

| Engine | Paths | Notes |
|---|---|---|
| **Built-in** (C#, always there) | FB2 / FBZ / fb2.zip → EPUB 3; EPUB → KEPUB; EPUB → AZW3; FB2 → AZW3 / KEPUB (through EPUB) | the common cases of archive libraries and Kindle / Kobo owners; same on every platform and the portable build |
| **Calibre `ebook-convert`** (optional, installed by the administrator; included in the "full" Docker image) | anything Calibre reads (PDF, DOCX, RTF, MOBI, AZW3, LIT, CBZ…) → EPUB / AZW3 / KEPUB / FB2 / TXT, and **MOBI output** | broadest coverage; ~300 MB with Qt; amd64 and arm64 |

- **Licences:** the project is MIT. Built-in converters are written by us; the AZW3 writer follows
  [leotaku/mobi](https://github.com/leotaku/mobi) (Go, MIT, KF8 writer) and the KEPUB step
  [kepubify](https://github.com/pgaskin/kepubify) (Go, MIT): ported with attribution in the source and in
  `THIRD-PARTY-NOTICES`. Calibre (GPL-3) is **only run as a separate program**, never linked or copied; the full image
  ships its licence and the pointer to its source.
- **Choice per path:** built-in when it covers the path, Calibre otherwise; a server setting "prefer Calibre when
  installed" for administrators who want its output for paths both cover. The matrix of what is offered is built
  at start from the converters present (Calibre found by `Librarion:Conversion:Calibre:Path` or on `PATH`, version
  checked).

## Amazon formats

- **AZW3 (KF8)**: the Kindle target; every Kindle since 2011 opens it from USB or the Kindle browser
  ([ereader-mode](ereader-mode.md)). Built-in writer; Calibre when preferred.
- **MOBI** (old, deprecated by Amazon): only for Kindles before KF8 (Kindle 1, 2, DX, Keyboard); **Calibre only**,
  offered when it is installed.
- **KFX: not offered.** Its writers are Kindle Previewer (closed, Windows / macOS, under Wine on Linux: fails
  [privacy](../development/privacy.md)) or boko (GPL-3 Rust, first release 2026, external program); reconsidered if
  a Kindle stops opening AZW3.
- **Send to Kindle** (email to Amazon): not planned; it would be outbound and opt-in by an administrator.

## When and where

- **On demand:** a format a book lacks is offered where its files are listed (book page "Download as", OPDS
  acquisition links, e-reader mode), when the matrix has a path from one of its files; nothing is converted until
  someone asks.
- **Queue:** conversions run as background tasks with a concurrency limit (built-in: half the cores, Calibre: 1 by
  default), a per-user limit and the same request merged (two users asking AZW3 of the same file wait on one job).
  The app shows "Preparing…" then downloads; OPDS and the no-script e-reader mode wait up to a few seconds, then
  answer with a "being prepared, try again" page (e-reader mode) or `503` + `Retry-After` (OPDS).
- **Cache:** results in a size-capped LRU on disk (beside the extract cache, [archives](archives.md)), keyed by the
  source file (id + size + mtime / CRC), target format, converter and its version, options and the metadata
  stamp; cap and age set by the server administrator.
- **Keep as a format of the book** (optional action, edit right): a converted file is written into the library's
  converted folder and becomes a `BookFile` with its source recorded, so duplicates ([merging](merging.md)) know it is
  derived.
- **Our metadata goes in:** title, authors, series and number, language, tags and cover come from the catalog (the
  curated values, [metadata-tags](metadata-tags.md)), not only from the source file.
- Device profiles from the e-reader mode (Kindle → AZW3, Kobo → KEPUB, PocketBook → EPUB / FB2) choose the default
  target; images resized to the device limits with the image library (D17).

## Rights

- `library.convert` (Read kind: downloading a converted copy; costs CPU, so separate from `library.download`, which it
  requires). Keeping the result as a book file needs `library.edit` too ([rights](rights.md)).

## Safety and cost

- Inputs are strangers' files: built-in converters use the bounded readers of the import (DTDs off, size and entry
  caps, timeouts); Calibre runs as a child process with an argument list (no shell), a temp folder per job, its config
  folder set to that temp folder, a timeout, a memory limit where the platform allows and low priority.
- Nothing proportional to the catalog: the offered formats come from the book's own files and the matrix; CPU is
  bounded by the queue, disk by the cache cap.

## Tests to have

- Built-in: golden samples per path (FB2 with notes, images, poems, tables; EPUB 2 and 3; non-Latin scripts), output
  read back by our own readers (EPUB, MOBI / AZW3 header and records), metadata and cover from the catalog;
  hostile inputs refused within the caps.
- Calibre: run when installed (skipped otherwise, like Postgres runs): path found, version checked, timeout kills
  the process, MOBI offered only with it.
- Queue and cache: same request merged, limits hold, cap evicts, a changed source misses the cache; rights;
  OPDS `503` + `Retry-After`.
- e2e (important flow): download an FB2 from a container zip as AZW3 from the book page and from the e-reader mode;
  as KEPUB. By hand: the files open on a Kindle and a Kobo.
