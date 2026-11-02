# Changelog

User-visible changes per version (agents/docs/development/versioning-and-changelog.md).

## Unreleased

### Changed

- New colours, "ink and paper": warm paper with ink-blue in the light theme, deep blue-grey in the dark one, every
  text readable at WCAG AA contrast in both.
- Restyled buttons, fields, checkboxes, chips and badges, with visible focus rings; panels and lists as cards.
- Deleting something asks in the page's own dialog (a sheet from the bottom on phones, Cancel focused) instead of the
  browser's box; deleting a tenant needs its name typed in it. Short messages ("Saved.", "Library deleted.", "Scan
  started") appear as toasts in the corner, kept while pointed at. Empty lists show an icon and what to do next, and
  pages that load show grey placeholders instead of "Loading…".
- A new frame: the top bar shows the library being browsed (a switcher when there are several, remembered by the
  browser) and an account menu (My account, Administration, light or dark colors, the tenant for server
  administrators, Sign out) in place of the theme button and the tenant box. The navigation is grouped, with
  Administration at its foot; on phones it and the menus rise from the bottom. The administration is a page of its
  own, with an arrow back to the library.
- The administration is grouped as in SFE: a left column of sections in groups (Account, Libraries, People and
  access, Tenant and server), an overview of cards, a path back above each page and an address per page
  (`/admin/settings/server`). My account is its first section (`/account` still opens it), settings are split by
  level (library settings under Libraries; this tenant and the whole server under Settings), and System shows the
  version and database.
- On phones the administration opens on the list of sections (each with its description); a section opens on its
  own with its pages as tabs, and the back arrow returns to the list.
- Each library has its own pages instead of one long panel: Folders, Runs, Import, Missing books and Schedules as
  tabs under its name (`/admin/libraries/1/runs`), with Scan now and the progress of a running scan above every tab.
- Schedules say what they run: a folder scan, or an import from Calibre or INPX (new: imports can run on a
  schedule, checked as an import started by hand). They are set in a dialog that asks what, then where from, then
  when, and listed with what they run, when, their next time and an on/off switch.
- Settings come in groups, each setting with a line on what it does; changed ones are marked, a bar at the bottom
  counts the changes with Save and Discard, and leaving a page with unsaved changes asks first.
- The administration has a search box (as in SFE): it finds sections, pages and settings by every word typed, in
  their names, descriptions and choices, accents and capitals aside, with the words marked; a setting opens on its
  page scrolled into view, selected and briefly highlighted.
- A panel under the top bar shows the scans, imports and removals running (as SFE's scan panel): what runs and
  where, the time it has taken, the books found so far and the file being read, with Cancel; once done, how it went
  until closed. It replaces the line of progress above a library's tabs.
- Administration › Background tasks (as SFE's): a card per kind (folder scan, import) with its state, the progress
  of what runs, the last and the next run, Run now; every library's schedules; and the history of every library's
  runs.

### Fixed

- Times of runs and other records read from a SQLite database were shown in the wrong time zone (an hour or more
  off); they are now read as UTC.
- Starting on a database made by an older build (before 1.0 the database is not upgraded between builds) stopped
  with "table already exists" halfway; it now stops before touching it and says which file to delete.

## 0.5.0

### Added

- Imports from other programs, from the library's panel: a Calibre library (its `metadata.db`: authors, series,
  publishers, languages, tags, ratings, descriptions, identifiers, custom columns, covers) or an INPX catalog with its
  zips (authors, genres, series, languages, keywords, ratings; deleted records left out) is imported into a library and
  stored in Librarion's database; the source is only read. Importing again brings in what changed and never removes a
  book; books already scanned are matched by their files, and a later scan keeps the imported metadata. New right
  **Import** (`library.import`), given to Administrators.

### Changed

- Scans and imports write tag counts with one statement per batch, and imports write new books' rows in bulk: a 2M-book
  Calibre library imports in 27 minutes on PostgreSQL, a 1M-record INPX catalog in 9.

- A book whose files a scan no longer finds is kept as a missing book (hidden, with its metadata and tags) instead of
  being deleted; it comes back when the file is put back or found moved. Missing books are removed only when an
  administrator accepts it in the library's panel; they are offered for removal after a number of days set per
  library (30 by default).

## 0.4.0

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
  titles in Latin, Greek, Cyrillic, Arabic, Hebrew, Chinese, Japanese, Korean, Devanagari, Bengali, Tamil and Thai
  scripts), the same for a book every time.
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
