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

## Merging many at once

Groups with **the same file** or **the same identifier** can be merged without comparing them one by one. Groups with
the same title and author can't: they are often different editions, so they are merged one at a time with Compare and
merge.

- Tick the groups to merge and press **Merge N chosen**, or choose **Same file** or **Same identifier** under **Why**
  and press **Merge all** for every group of that reason in the library.
- The merge runs in the background (the progress panel shows it; it can be cancelled). Each group keeps its book with
  the most details (tags, identifiers, files, title, description, rating; else the oldest), with that book's own title,
  cover, description and rating.
- A group with books marked as different is left as it is. When the run ends, the list is read again.

## Merges: undo

**Merges** (the second tab) lists the merges of the library, newest first: the books merged → the book kept, how many
and when. **Undo** puts a whole merge back, after asking:

- the merged books get back their files, tags, identifiers and pages, and their links lead to them again;
- the kept book loses what they gave it, and gets back its own title, description, rating and cover, unless one of
  them was changed since the merge (then your change stays);
- a merge can't be undone while the kept book has itself been merged into another one since: undo that one first.

## Split

A book's page offers **Split…** (with the Merge books right, on a book with two files or more): tick the files that
are another book and press **Split N files off**. They become a new book, and its page opens.

- The new book is read from its files, as a scan would. A book that came from a Calibre or INPX import is copied
  instead (its files don't carry the import's details), without its identifiers, which stay with the first book.
- The book you split keeps its details: to take back what a merge gave it, undo the merge instead.
- The two books are marked as different, so they aren't suggested as duplicates again.

## Compare and merge

**Compare and merge** shows the books side by side (one under the other on a phone), with what differs marked.

- **Keep this book**: the book that stays (at first the one with the most details).
- **Merge into the kept one**: untick a book to leave it out.
- For the cover, title, description and rating, when they differ, **Use this** picks which book's the kept one gets.
- **Merge** moves the other books' files, tags and identifiers into the kept book. Their pages, covers and download
  links lead to the kept book from then on. What you chose stays when the books are scanned or imported again.

A book's page offers the same for each of its possible duplicates: **Compare and merge**, and **Not a duplicate**.
