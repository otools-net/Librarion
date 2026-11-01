# Changelog

User-visible changes per version (agents/docs/development/versioning-and-changelog.md).

## Unreleased

### Added

- Library folders: books are found in them as loose files or inside ZIP archives (read in place, never unpacked);
  tenant administrators add folders only under the ones the configuration allows (`Librarion:Libraries:Folders`).
- Scanning: new books come in with their metadata (title, authors, series, genres, language, publisher, year,
  description, identifiers) read from FB2, EPUB, CBZ, MOBI / AZW3 and PDF files; changed books are read again,
  removed ones leave, unchanged archives are skipped. Scans run in the background with progress, can be cancelled,
  are listed with what they did, and can be scheduled.
- Rights **Download** and **Scan**.
- Downloading a book's file (`api/books/{id}/files/{fileId}`): streamed from the folder or straight out of its ZIP
  archive, never unpacked, with resumable ranges where the file allows them.
- The same book file found again in an archive (the same bytes) becomes another copy of the book already there, not
  a second book.
- Book covers (`api/books/{id}/cover/{s|m|l}`): the cover inside FB2, EPUB, CBZ, MOBI and AZW3 books, extracted the
  first time it is shown and kept in the cache; every other book gets a generated cover (five designs, eight palettes,
  titles in Latin, Greek, Cyrillic, Arabic and Hebrew scripts), the same for a book every time.
- `Librarion:Cache:Directory` sets where cache files are kept (default `data/cache`).

## 0.3.0

### Added

- The catalog in the database: books with their files, identifiers and tags (authors, series, categories and other
  fields as tags), built for libraries of millions of books.
- Book lists by title, author, date added or series number, a page at a time, showing only the books the reader may
  browse (library rights and the groups' content filters); a book's details; the values of a field (authors,
  series…) with their book counts (`api/books`, `api/books/{id}`, `api/fields/{key}/values`).

### Changed

- PostgreSQL connections run without parallel workers unless the connection string sets its own `Options`.

## 0.2.0

### Added

- A new logo and favicon: a temple front whose columns are book spines, its colours following the light or dark
  theme.
- First setup: the server prints a setup code; with it, the first server administrator and the first tenant (with
  its library) are created.
- Accounts, tenants, libraries and groups with rights (server, tenant, library), managed in the administration
  pages (rights can be switched off per tenant); sign-in with lockout and a per-address limit; server administrators
  recover their account with a one-time code shown where the server runs.
- Sign-in page, and a My account page to change the password and keep preferences (colors, density, language, books
  per page).
- Settings by level (server, tenant, library, own preferences) with locks, and a security log of sign-ins and access
  changes.

## 0.1.0

### Added

- First build: the server starts on SQLite or PostgreSQL and shows the app's frame in light and dark, on phones and
  desktops.
