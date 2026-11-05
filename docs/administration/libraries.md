# Libraries

A tenant holds one or more libraries; groups give their library rights in every library or in some
([groups](groups.md)). **Administration → Libraries**:

- **Add:** type the name of a new library and add it.
- **Rename** a library.
- **Delete** a library with everything in it, after confirming.
- **Open** a library (its name) for its own pages, as tabs under its name: **Folders**, **Runs**, **Import**,
  **Missing books** and **Schedules** (the last four with the scan right; Import with the import right too). **Scan
  now** stays above every tab.

## Folders

A library's books are found in its **folders**: loose book files (FB2, EPUB, PDF, MOBI, AZW3, DJVU, CBZ…) and ZIP
archives full of them, which are read where they are, never unpacked.

- **Add a folder:** its full path on the server (`/srv/books`, `D:\Books`). It must exist, and can't be inside or
  around another folder of the tenant's libraries.
- Server administrators may add any folder the server can read. Tenant administrators only folders under the ones
  the server's configuration allows (`Librarion:Libraries:Folders`, see [configuration](../configuration.md)).
- **Remove a folder:** its books leave the library (in the background); the files themselves are not touched.
- A folder that is missing at a scan (an unplugged disk) keeps its books until it comes back or is removed.

## Scanning

Scanning reads what changed in the folders: new books come in with their title, authors, series, genres and other
metadata from the book itself (a book without a title is shown by its file name), changed ones are read again. An
unchanged archive is skipped without being read.

- **Scan now** (above the tabs) starts a scan (right **Scan**). While it runs, a panel under the top bar shows it on
  every page: "Scanning …", the time it has taken, the books found so far and the file being read, with **Cancel**
  (what it already read stays). When it ends the panel shows how it went until you close it. See also
  [Background tasks](background-tasks.md).
- The **Runs** tab shows each scan: when, how it ended, how many books were added, changed and removed, and the
  first files that couldn't be read.
- The **Schedules** tab sets work that runs by itself. **New schedule** asks, in order: **what it runs** (a folder
  scan, or an import from Calibre or INPX if you may import), for an import **where it comes from** (as on the Import
  tab), then **when**: every few hours (at least every 15 minutes), or on chosen days at a time of day. Each schedule
  shows what it runs, when, and its next time; its switch turns it off without deleting it. One that comes due while
  another scan or import of the library runs is skipped, and missed ones are not made up later. An import whose source
  file or files folder is gone shows as a failed run saying so.

## Importing from another program

A library can take in the books of a **Calibre library** or of an **INPX catalog** (MyHomeLib, Lib.rus.ec and similar
collections): on the library's **Import** tab, choose the kind of source, type the path
of its file on the server, and start the import. It runs in the background and shows on the **Runs** tab.

- **Calibre:** the path of the library's `metadata.db`. Titles, authors, series and numbers, publishers, languages,
  tags, ratings, descriptions, identifiers (ISBN and others) and covers come in; custom columns too (text, series,
  rating and enumeration columns as values you can browse, the others shown on the book's page). Calibre's library is
  only read, never changed, and can be open in Calibre meanwhile.
- **INPX:** the path of the `.inpx` file, with the zips of books next to it. Authors, genres, series, languages,
  keywords and ratings come in; books marked deleted in the catalog are left out.
- **Folder of the books' files** (optional): where the source's books are, by default the folder of the file you
  chose. It becomes one of the library's folders if it isn't in one already, with the same checks as adding a folder.
- What the source says is **stored in Librarion**; Librarion doesn't keep reading the source. To bring in what you
  changed in Calibre or a newer catalog, **import again**: only what changed is written.
- A book the library already had from a scan is matched by its file and takes the source's metadata; a later scan
  keeps it (the source's metadata wins over what the book file says).
- An import **never removes a book**, also when the source no longer has it. A book whose file is gone becomes a
  missing book (see below).
- A record whose file isn't found is skipped and counted, with the first problems listed under the run.

## Missing books

A book whose files a scan no longer finds is **not deleted**: it becomes a *missing book*. It disappears from the
lists, but everything about it is kept (its metadata, its tags, your changes), in case the file comes back.

- Put the file back where it was, or move it (rename its folder or its archive): the next scan finds it and the book
  is back as it was. A moved file is recognised when exactly one new file matches it (same size and contents for books
  in archives, same size and name for loose files).
- The library's **Missing books** tab lists them, oldest first. Those missing longer than the library's setting
  **Days before missing books are offered for removal** (30 by default) are marked *ready to remove*.
- **Nothing is removed by itself.** Remove one book, all the ready ones or all of them from that tab; each asks you
  to confirm first, and removal can't be undone.
- Removing a folder from the library still removes its books right away (you confirm that removal).

## Downloads

Readers with the right **Download** in a library get its books' files as they are in the folders, also straight out
of a ZIP archive (nothing is unpacked on the server). A file that moved or changed since the last scan can't be
downloaded until the library is scanned again.
