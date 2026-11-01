# Libraries

A tenant holds one or more libraries; groups give their library rights in every library or in some
([groups](groups.md)). **Administration → Libraries**:

- **Add:** type the name of a new library and add it.
- **Rename** a library.
- **Delete** a library with everything in it, after confirming.

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
metadata from the book itself (a book without a title is shown by its file name), changed ones are read again,
removed ones leave. An unchanged archive is skipped without being read.

- **Scan now** starts a scan (right **Scan**); its progress shows while it runs, and it can be cancelled (what it
  already read stays).
- The list of **runs** shows each scan: when, how it ended, how many books were added, changed and removed, and the
  first files that couldn't be read.
- **Schedules** scan by themselves: every few hours (at least every 15 minutes), or on chosen days at a time of day.
  A scan that comes due while another runs is skipped, and missed ones are not made up later.
