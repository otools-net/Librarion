# E-reader in the app (planned)

Code (planned): `src/Librarion.Web/src/app/reader/` (one viewer per format behind a common reader shell),
`Api/Endpoints/ReaderEndpoints` (book bytes, inner entries, progress), `Core/Reading/` (`BookStreams`, `ExtractCache`,
`ReadingProgress`). Reference for the behaviour (not the code: GPL-3): calibre-web (`cps/web.py` `read_book`,
templates `read*.html`, `ub.Bookmark`); SFE's viewers for the bundling (`agents/docs/features/epub-reader.md`,
`pdf-viewer.md` there). Decision D16 in [foundation-plan](../development/foundation-plan.md).

Books are read **in the browser, inside the app**, on phones and desktops; everything the reader needs is bundled
(no CDN, no web fonts from elsewhere: [privacy](../development/privacy.md)).

## Formats

| Format | Viewer (candidates, chosen in stage 10) | Notes |
|---|---|---|
| EPUB 2/3 | foliate-js (MIT, vendored at a pinned commit) or epub.js (BSD, as calibre-web / SFE) | paginated or scrolled, CFI positions |
| FB2, FBZ | foliate-js (reads FB2 natively) or our FB2 → HTML renderer | the most common format of archive libraries |
| MOBI, AZW3 | foliate-js | calibre-web can't read these in the browser |
| CBZ (CBR, CB7 later) | foliate-js comic book or our page viewer over the inner zip's images | pages served one by one from the inner zip |
| PDF | pdf.js (Apache-2.0), worker, cmaps and fonts bundled | as SFE: no wasm under the CSP, dark pages |
| TXT | our text view (encoding detection, as SFE's text viewer) | |
| DJVU | later (djvu.js is GPL: separate decision) | download meanwhile |

A format without a viewer offers **Download** only. The choice per format is recorded in this note when made.

## Reading from where the book lives

- Loose files: served as they are (ranges). Inside container zips: stored entries through a window, deflated ones
  decompressed once into the **extract cache** then served with ranges ([archives](archives.md)). EPUB/CBZ inner
  entries (chapters, images) come from `api/books/{id}/files/{format}/entries/{path}` so the browser never downloads a
  whole 300 MB comic to show page 1.
- Book content is a stranger's: viewers render it in a sandboxed frame or through sanitised HTML (EPUB/FB2 scripts never
  run on the app's origin; SFE's rule for library files), with a reader CSP.

## Reader UI (responsive, compact)

- Full screen on phones, page turns by tap zones and swipes, keyboard on desktop; table of contents, progress bar
  and position, search in book (where the viewer supports it).
- Reading preferences per user: theme (light, sepia, dark), font family (bundled fonts), size, line height, margins,
  paginated / scrolled; kept per user on the server (follow the user to another device), last used per browser.
- Opens from the book page (**Read**), from a list's item menu, and from the last-read shelf ("Continue reading").

## Progress and bookmarks

- **Position per user, book and format** (calibre-web keeps `Bookmark(user, book, format, key)`): an EPUB CFI, a page
  for PDF/CBZ, a fraction otherwise; saved on page change (debounced) and on leaving; the book opens where it was left.
- Read state (unread / reading / read) follows the progress (≥ 98 % offers "mark as read").
- KOReader sync (kosync API) in the same stage maps to the same position store, so a book continued on an e-ink device
  opens there in the browser too (by the book's file hash, as KOReader identifies books). That partial MD5 is computed
  **lazily** (on download or first sync, then stored on the `BookFile`), never for every file at import: a deflated
  archive entry needs a full decompression.

## Rights

`library.read` (Read: open in the reader; separate from `library.download`, so an administrator can allow reading without
downloading), progress is the user's own data.

## Tests to have

- Backend: entry routes (also inside container zips), ranges, extract cache hits, sandbox/CSP headers on book content,
  progress saved and read back per user/format, rights. Client: reader shell, preferences, position restore.
- e2e (important flow): open EPUB, FB2, PDF, CBZ from the book page on phone and desktop, turn pages by tap/swipe/keys,
  leave and come back to the same place, a book inside a container zip opens; no request leaves the origin.
