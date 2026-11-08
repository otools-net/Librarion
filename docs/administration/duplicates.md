# Duplicates

Big collections hold the same book more than once: the same file in two archives, an FB2 and an EPUB of one work, two
scans with different file names. Librarion finds books that may be the same one, and **Administration → Catalog →
Duplicates** lets you merge them or set them apart.

It needs the **Merge books** right ([rights](../reference/rights.md)), which Administrators and Curators have.

## How they are found

Books are put together when they share:

- **the same file**: exactly the same bytes (inside archives, where the size and checksum are known);
- **the same identifier**: an ISBN, an EPUB's id, a Calibre id… An FB2's own id counts only when the title or the
  first author is the same too: hand-made FB2 files often copy it from one another;
- **the same title and author**: the title (else the file name) and the first author, ignoring case and punctuation.

A key shared by 50 books or more ("Notes" by nobody) is ignored. Possible duplicates are found at the end of every scan
and import; the first scan or import after an update looks at every book, which takes a minute or two on a large
library. **Find again** does it again in the background.

A file with exactly the same bytes as one already in its library joins that book when it is scanned (unless the tenant
setting **Same file twice: one book** is off): such copies never show here.

## The page

- **Library**: the duplicates of one library (books of two libraries can't be merged). It appears when you may merge
  in more than one.
- **Why**: all reasons, or one of them.
- Each group shows why its books may be the same, and the books (each opens its page). The biggest groups come first;
  **More** shows the next ones.
- **Not duplicates** says the group's books are different: they are never suggested together again, also after later
  scans and imports.

## Compare and merge

**Compare and merge** shows the books side by side (one under the other on a phone), with what differs marked.

- **Keep this book**: the book that stays (at first the one with the most details).
- **Merge into the kept one**: untick a book to leave it out.
- For the cover, title, description and rating, when they differ, **Use this** picks which book's the kept one gets.
- **Merge** moves the other books' files, tags and identifiers into the kept book. Their pages, covers and download
  links lead to the kept book from then on. What you chose stays when the books are scanned or imported again.

A book's page offers the same for each of its possible duplicates: **Compare and merge**, and **Not a duplicate**.
