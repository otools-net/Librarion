# How a book is shown: cover and title (planned)

Code (planned): `Core/Catalog/BookTitles`, `Core/Covers/` (`CoverExtractor`, `CoverCache`, `Thumbnails`,
`Generated/` one class per template), route
`api/books/{id}/cover/{size}`; client `app/books/book-card`, `book-cover`. Reference for the behaviour (not the code:
GPL-3): calibre-web (`C:\workspace\GIT\reference-projects\calibre-web`: `cps/helper.py` `get_book_cover*`,
`cps/tasks/thumbnail.py`, `cps/static/generic_cover.jpg`). Decision D15 in [foundation-plan](../development/foundation-plan.md).

Every list, grid, search result, book page and OPDS entry shows a book the same way: **its cover and its title**.

## Title

- **The book's title from its metadata** (FB2 `<book-title>`, EPUB `dc:title`, MOBI EXTH 503 / PDB name, PDF Info
  `Title`, Calibre `title`, CBZ `ComicInfo.xml` `Title`), trimmed; blank or a placeholder ("Unknown", "untitled",
  "Без названия"…, list in code) counts as none.
- **Without one, the file name:** the last segment of the path (an archive entry's own name, not the archive's), its
  book extensions removed (`.fb2`, `.fb2.zip`, `.epub`…; double extensions too), `_` and `.` between words as spaces.
  A name that is only digits (lib.rus.ec archives: `123456.fb2`) is still shown: it is all there is.
- Stored: `Books.Title` (nullable: what the metadata said) and `Books.SortTitle` (never null: the title, else the file
  name, normalised for sorting: case-folded, leading articles per language moved, digits padded). The UI shows
  `Title ?? FileTitle` and marks a file-name title subtly (italic, hint "title from the file name") so curators see what
  metadata is missing; search matches both.
- An edited title (stage 6+, `metadata.edit`) wins over both and survives re-imports (same rule as tags' link
  sources: imported values are recomputed, edited ones kept).

## Cover

- **From the book itself**, found at import (head reads only, [archives](archives.md)) but **extracted lazily** on first
  view, then cached:
  - EPUB: the OPF `cover-image` item (EPUB 3) or `<meta name="cover">` (EPUB 2), else the first image of the first
    spine item;
  - FB2: `<coverpage><image l:href="#id">` → the `<binary id>` (base64, near the end: the whole entry is read once);
  - MOBI/AZW3: EXTH 201 (cover offset) → the image record;
  - CBZ/CBR: the first image in natural name order (`ComicInfo.xml` `FrontCover` page when set);
  - PDF, DJVU: no page renderer on the server in v1 → a generated cover (below); a first-page render can come later.
  - Calibre import: `cover.jpg` of the book's folder.
- **Thumbnails** at fixed sizes, made from the cover once and kept in a sharded disk cache (`covers/ab/cd/<hash>-<size>.webp`,
  as SFE's thumbnail cache): `s` (list rows, ~64 px high), `m` (grid cards, ~240 px), `l` (book page, ~600 px), plus
  the original. Served with a long `Cache-Control` and an `ETag` from the cover hash, so scrolling 2M books costs one
  small request per visible card, then none.
- Bad images (corrupt, huge dimensions, decompression bombs) are refused with limits on pixels and bytes; the book
  then gets a generated cover (below) and its source cover is retried after a re-import.

## Generated covers (books without one)

Every book has a cover: when the book provides none (or it can't be read), **the app generates one and assigns it to
the book** (decided 2026-10-31).

- **Templates:** several cover designs, each a small renderer drawing the title (or file name), the authors and the
  series with number on a background: e.g. *classic* (cloth color, framed title), *typographic* (large title, thin
  rules), *geometric* (shapes from a palette), *band* (series band at the top), *minimal* (title and author only).
  More can be added as code; each has palettes (variants). Templates are listed in the administration, where an
  administrator turns them on or off per tenant (at least one stays on).
- **Assignment is stored, the image is not made up front:** at import a book without a cover gets
  `Books.CoverSource = Generated` with `GeneratedTemplate` and `GeneratedVariant` chosen deterministically from the
  book's identity (a hash: the same book always looks the same, neighbours differ, templates spread evenly). Two
  columns per row: free at 2M books. The image is rendered on first view and cached with the thumbnails like any cover
  (cache key: template, variant, title, authors, series, size), so it changes when the metadata changes.
- **Stable:** turning a template off doesn't reassign existing books unless the administrator asks ("reassign books of
  this template", a background task in batches); a book page offers **Change cover style** (`metadata.edit`) to pick
  another template/variant, kept across re-imports. A real cover found later (re-import, upload) replaces the
  generated one; removing it falls back to the assigned template.
- Covers' sources: `Book` (extracted), `Calibre`, `Uploaded` (stage 6+), `Generated`; `Books.CoverState` (unknown /
  present / failed) only for extracted ones. The UI can mark generated covers subtly (a corner glyph, for curators).
- **Text on covers in any script:** bundled fonts (Noto families or a subset strategy) cover Latin, Cyrillic, Greek,
  and later Arabic/Hebrew (right to left) and CJK; text is shaped (HarfBuzz) and fitted (size steps, line breaks,
  ellipsis); a title in an uncovered script falls back to the file-name style without breaking the image. Font size
  versus package size is weighed in stage 3.
- Served exactly like extracted covers (thumbnail sizes, ETag, OPDS image links): clients and reader apps can't tell
  the difference and never get a "no image".
- The client shows a neutral box of the cover's aspect ratio only while an image loads; there is no client-drawn
  placeholder.

## Image library (D17)

Decided in stage 3. It must decode JPEG/PNG/GIF/WebP covers, resize, encode WebP/JPEG, **draw shapes and shaped text
with bundled fonts** (generated covers), send nothing anywhere, and run in Docker arm64 and the portable builds.
Leading candidate: SkiaSharp + HarfBuzzSharp (MIT; native assets per platform); alternative ImageSharp + Fonts (Six
Labors split licence: check terms).

## Where it shows

- Grid card: cover `m`, title (2 lines, ellipsis), first author (1 line). List row: cover `s`, title, authors, series,
  format badges. Book page: cover `l`, full metadata, formats, **Read** and **Download**.
- OPDS: `http://opds-spec.org/image` (cover `l`) and `…/image/thumbnail` (`m`) links; title as above.
- Lazy loading (`loading="lazy"`, only visible cards in the virtual grid) and fixed aspect boxes (no layout shift).

## Tests to have

- Title rules per format, placeholders treated as none, file-name fallback from loose files and archive entries,
  double extensions, sort title; cover extraction per format from samples (also inside container zips), thumbnail
  sizes and cache hits, ETag 304, hostile images refused; generated covers: every template renders (long titles, no
  author, series numbers, Cyrillic and other scripts) within bounds, assignment deterministic and evenly spread,
  stable when a template is turned off, re-rendered when the title changes, replaced by a real cover found later;
  e2e: grid and book page show covers and titles, a book without a cover shows a generated one, a book without
  metadata shows its file name.
