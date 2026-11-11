# Changelog

User-visible changes per version (agents/docs/development/versioning-and-changelog.md).

## Unreleased

### Fixed

- **Tags → Possible duplicates**: tags with as many books are listed by name; their order depended on the order the
  files were found, which differs between Linux and Windows.

### Changed

- **Covers are JPEG** everywhere (they were WebP), so every reader app can show them. Covers already made are made
  again when next shown; covers uploaded before are kept and shown as JPEG too.

### Added

- New rights, given to every built-in group: **Reader apps (OPDS)** (per library, for the coming OPDS catalog at
  `/opds`), **Own API keys** and **Own shelves**. Reader apps sign in with a password or an API key; the web app's
  sign-in never opens the catalog. The catalog itself comes in the next changes.
- Reader apps sign in to `/opds` with the account's name and password (Basic). Wrong passwords count as on the sign-in
  page (five from one address and that address waits) and appear in the security log. Each account's reader apps may
  make `Librarion:Security:OpdsRequestsPerMinute` (600) requests a minute.
- **API keys** (My account → API keys): named keys for reader apps and scripts, each for
  reader apps (OPDS), reading the API and/or changing through it, optionally limited to some libraries and to an end
  date. A key is shown once; it works as the password in a reader app, or as `Authorization: Bearer` / `X-Api-Key`.
  It ends when revoked, at its end date, or when the account's password changes or the account is disabled.
  The page shows the catalog's address for reader apps. Administrators can see and revoke an account's keys on its
  page in Administration → Users. The security log notes keys made and revoked. A database made
  by an earlier build must be deleted (before 1.0 the database is not upgraded).
- **The OPDS catalog** at `/opds` for reader apps: the libraries the account may read (with one, its catalog at
  once), each with its books **recently added** and **by title**, 50 a page, each book with its authors, language,
  categories, series, description, cover and a download link per file; **authors, series, categories, languages and
  the other fields** and **tags**, by first letter when there are many (a series in its order, a tag with the tags
  under it). **Search** from the reader app's search box, in the same search language as the web app (`author:`,
  `-` to leave out…), the results by title, 50 a page. FB2 books can also be downloaded as **`fb2.zip`** (zipped as
  they are sent, much smaller). A reader app still showing a book merged into another downloads its file and cover.
- **Shelves**: personal lists of books, up to 100 per account (the setting **Shelves per account**, server or
  tenant, 1 to 1,000), 10,000 books each, showing only the books the account may see. A merged book's places on
  shelves go to the book it was merged into, and back on undo. **Add to shelf** on a book's page and in the
  selection bar (tick a shelf to put the books on it, clear it to take them off; **New shelf** makes one);
  **Shelves** in the navigation, a **Shelves** page (`/shelves`), and a page per shelf (`/shelves/<id>/<name>`) with
  its books in **its own order** (or by title, author, date added). The order changes by **dragging** a book's grip
  (mouse or finger), by the **arrow keys, Home and End** on it, or with **Move** (a click on the grip: to the start,
  up, down, to the end); a selected book moves with its selection. **Remove from shelf**, **Rename** and **Delete**
  (asking first; the books stay). Through the API too (`api/shelves`; `POST …/books/move` moves a selection at
  once). In the **OPDS catalog**, each library lists **My shelves** once the account has one: each shelf's books of
  that library, in the shelf's own order.
- **API keys in the address:** a key made with **Can be used in the address** opens the catalog from
  `…/opds/k/<key>` alone, for reader apps that can't sign in; every link of the catalog keeps it. Under a new key,
  **How to use it** gives that address (with **Copy address**), or the user name and the key as the password, and
  the headers for scripts. The tenant setting **API keys in the address** (Reader apps) switches it off. Keys are
  never written to the server's log, whatever its log level.

## 0.7.0

### Added

- **Search: `special:untagged`** finds the books without a tag of their own (fields such as authors and series don't
  count), to see what is left to tag; `-special:untagged` the others. **`special:duplicates`** finds books that may be
  duplicates, for those who may merge books. **`tag:"Freddy (FNAF)"`** finds one of two tags of the same name, told
  apart by the tag in brackets; Refine and the suggestions name such tags that way. Typing `spec` in the search box
  offers the special terms; the search page's help lists them. A database made by an earlier build must be deleted
  and the libraries scanned again (before 1.0 the database is not upgraded).
- **Tags → Possible duplicates** (with the Manage tags right): tags of one field that may be the same, with most books
  first: the same words in another order or alphabet ("King, Stephen", "Кинг Стивен" spelled alike), names that sound
  alike ("Steven" / "Stephen" / "Стивен"), or a name and its initials ("S. King"). Keep one and merge the others into
  it, or say they are not the same. A tag's page shows its possible duplicates to those who manage tags.
- **Duplicates: merge many, undo, split.** Groups with the same file or the same identifier can be ticked and
  merged together, or all of one reason at once, in the background; each keeps its book with the most details. The
  new **Merges** tab lists a library's merges, and **Undo** puts one back whole (the kept book's title, cover,
  description and rating too, unless changed since). **Split…** on a book's page moves some of its files out into a
  new book.
- **Administration → Catalog → Duplicates** (with the Merge books right): a library's possible duplicates, the biggest
  groups first, with why (the same file, identifier, or title and author). **Compare and merge** shows the books side
  by side (one under the other on a phone): keep one, choose which title, cover, description and rating it gets, and
  the others' files, tags and identifiers move into it; their pages and links lead to the kept book. **Not
  duplicates** sets books apart for good. **Find again** looks at every book again in the background. A book's
  page offers the same for each possible duplicate.
- Possible duplicate books are now found once for the whole catalog and kept up to date by every scan and import
  (the first scan or import after this update finds them); a run in the background can find them again. Books marked
  as different stay apart. The Duplicates page comes next.
- **Same file twice: one book** (tenant setting, on by default): turned off, a scanned file with the same bytes as one
  already in the library stays a book of its own, shown as a possible duplicate.
- **Administration → Catalog → Cover styles** (with the new Manage cover styles right, Administrators and
  Curators): the drawn cover styles with a sample in every set of colors, each turned on or off for new books (one
  stays on). The books of a style turned off keep it until **Give its books another style**, a background run; a
  style chosen on a book's page stays. A book's **Cover** offers only the styles turned on.
- **Cover** on a book's page (for those who may edit books): upload a picture (JPEG, PNG or WebP, up to 5 MB), pick
  a drawn cover from previews of the book in every style and color set, or go back to the book's own cover. The
  choice stays through re-imports. Uploaded covers are kept in a new data folder (`Librarion:Data:Directory`,
  default `data`): back it up with the database.
- **Administration → Catalog → Imported tags** (with the Imported tags right): the tags and genres that books came
  with, in one library by how many books have them, with the names and sources they came from and how much the first
  500 cover. Tick some to **Keep** them as they are, **Map** them onto the right tag (a merge, undone under Tags →
  Merges) or **Ignore** them (imports skip their names and the links imports gave them go; undone under Ignored).
  Imports remember from now on every tag and genre name they meet, except people and series.
- **Folders** in the navigation, for those with the new **Browse by folder** right (Administrators and Curators):
  the library's folders and ZIP files with how many files each holds, then the books of each, which can be selected
  and tagged as anywhere else.
- **Default tags** for a library and its folders (with the Default tags right): books that come in get them, a book
  moved into a folder gets that folder's, and adding one can also tag the books already there. Shown under each
  folder's name; a folder with its own has a badge in the list.
- Select books (the box on a cover, Ctrl- or Shift-click, a long press on a phone) and give them tags or take tags
  away at once; **Tag all** does it for every book of the list on screen (the library, an author, a tag, a search),
  saying first how many books that is. A big change runs in the background.
- Edit a book on its page: title, authors in order, series and number, other fields, tags, description and rating,
  for those allowed to tag or edit books there (such as Curators). Edits stay when the book is read or imported
  again, and a tag taken off stays off.
- Choosing a tag (a parent, the tag a merge goes into) finds it by its other names and its shorthand too, the most
  used first, with its color, books and the name it was found by; the arrow keys and Enter choose.
- Readable addresses for authors, series and every tag: `/author/1234/Stephen King`, `/tag/5/To read`. The number
  finds the page and the name is only for reading: an address without it, with an old name or the old `/tags/1234`
  shows the current one, and a merged tag's address opens the tag it went into.
- A third built-in group, **Curators**, and the rights for looking after the catalog, under a new Catalog heading in
  the group editor: **Tag books**, **Edit books**, **Merge books**, **Imported tags**, **Default tags** and **Manage
  tags**. Administrators and Curators have them; Curators also have what Users have, and nothing that manages
  accounts, groups, libraries or settings. The screens using them come in the next changes. A tenant created before
  this version gets no Curators group (before 1.0 the database is not upgraded: delete it to start again).
- Tags and fields can be created, edited (names, other names, parents, colour, hidden), deleted and merged through the
  API (`api/tags`, `api/fields/{key}/merge`), and every merge undone (`api/merges`): a merged author's books move to
  the other, its name becomes another name of it, and books imported later under the old name land there too; a
  merged field's key keeps working in searches (`genre:` after merging it into `category:`). Changes of more than
  10,000 links run in the background, shown in the progress panel to those who manage tags.
- A tag's number of books now includes the books of the tags under it, each once: "Fiction" counts its "Fantasy" and
  "Horror" books, a field (Authors) the books with any author. Shown in the lists of values, on a tag's page, in
  search suggestions, in Refine and in the tag manager, which also says how many are tagged with it itself.
- Administration › Catalog › Tags: every plain tag or every value of a field (authors, series…), with how many books
  have it and how many tags are under it, a page at a time or found by name; an editor for its names, other names,
  parents, color, category and hidden; Merge and Delete, each saying first how many books change; and Merges, where a
  merge is undone. See [Tags](docs/administration/tags.md).

### Changed

- Importing a Calibre library is faster: the books' files are checked several at a time (2M books in 25 minutes
  instead of 31 on the test machine).
- Imports that meet many new authors, series or tags are faster on PostgreSQL: the new tags are written in bulk
  (a 2M-book Calibre library in 24.5 minutes instead of 28.8 on the test machine).
- The catalog now records the folder (or ZIP) each book file is in, for browsing by folder and folder default tags
  coming next. A database made by an earlier build must be deleted and the libraries scanned again (before 1.0 the
  database is not upgraded).

### Fixed

- Administration → Catalog → Tags now shows the list again once a merge, undo or delete that ran in the background
  ends (it showed the list from before the change until reloaded).
- **Tag all** on a big search no longer fails in a large library (counting every match took longer than the server
  waits): a list of more than 10,000 books is shown as "at least 10,000" and tagged in the background.
- The tag manager opens faster on SQLite with many tags (its count of plain tags read every field's values: 190 ms at
  2M books, now 4).
- The Duplicates page now shows the list again once **Find again** ends; possible duplicates could be listed twice
  when scans of several libraries ended at the same time.
- Possible duplicates no longer lists every book made by the same person as the same book: an FB2 file's maker (in
  its document info) can have an id of their own, which was read as the book's FB2 id. Read the library again to
  drop the wrong ids from books already there. A shared FB2 id now also needs the same title or the same first
  author, since hand-made files often copy it from one another.

## 0.6.1

### Fixed

- A book's page no longer gets wider than the screen on a phone held sideways: when there is little room beside the
  cover, a field's name goes above its values, and a long value wraps.

## 0.6.0

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

- The home page is the library: its books as a grid of covers or a compact list, in order of title, author or
  newest (both choices kept by the browser), more books coming as you scroll, and the place kept when you come back.
  Only the books in view are drawn, so libraries of millions stay quick. The version and database moved to
  Administration › System.
- The administration is loaded the first time it is opened: the app starts with less to download.
- Authors, Series, Tags and All fields in the navigation: names from A to Z with how many of their books the library
  holds (those without any left out), a Go to box, and a page for each name with its books (a series in its order),
  the tags it is under and the narrower ones. Tags hidden from an account by its group's filters are left out.
- A page for each book, opened from its cover or row: its cover, title, authors and series as links, the other
  fields as chips, the description, its files (format, size, the ZIP holding it) with Download for those who may
  download in its library, its identifiers and imported properties, and other books that may be the same one (the
  same file in another library, the same ISBN or other identifier, the same title and author).
- Search: the box in the top bar finds the books of the library with every word in their title, file name or the
  name of an author, series or tag (case and accents aside, words being typed as prefixes); `"phrases"`,
  `author:"Stephen King"` and every other field (`author:king`, `author:ki*`, `series:` for any), `tag:`, `format:epub`,
  `title:`, and `-` to leave books out. Books with a hidden tag (Archived) only show when the search names it. While
  typing, it suggests fields and then authors, series and tags with their number of books. Results are listed like the
  library, sorted by title, author or newest. See [Searching](docs/user-guide/searching.md).
- Refine, on the library and on search results: each field's most frequent values among the books (categories,
  authors, series, languages, formats, publishers, years, tags) with their counts; one narrows the search to its
  books, its minus leaves them out, one already chosen is marked and takes it out again. The results say how many
  books match; past 2,000 matches the counts are over the first 2,000 ("2,000+ books").
- Accounts whose groups see only part of a library (content filters) get its pages and Refine faster on big
  libraries.

### Fixed

- Typing in "Go to" while the page was still loading the libraries was forgotten: the list stayed at its start.
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
