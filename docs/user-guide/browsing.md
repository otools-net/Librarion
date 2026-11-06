# Browsing the library

The home page is the library you picked in the top bar: its name, how many books it holds, and its books.

- **Covers** (the grid button) shows each book's cover with its title, authors and series; **List** (the lines
  button) shows more books at once, with their formats. A book without a title shows its file name.
- **Sort** puts the books in order of **title**, **author** or **newest** (the last added first).
- Your choice of view and order is kept by this browser.
- **Refine** lists the library's most frequent categories, languages, years and other values with their number of
  books; picking one searches for its books ([Searching](searching.md#refine)). Fields with very many values, such as
  the authors of a big library, are left out there: their own pages list them.
- More books come as you scroll; there is no page to turn.
- After visiting another page (the administration, for instance), coming back shows the library where you left it.
- A library without books says so; administrators get a link to add a folder and scan it.

## Selecting and tagging books

If you may tag books in the library (Curators and Administrators may), you can give many books a tag at once.

- **Select** a book with the box on its cover, or Ctrl-click it (Cmd on a Mac); Shift-click selects every book up to
  the one before. On a phone, rest your finger on a book. Once one is selected, a tap or click selects more instead of
  opening them. **Esc** clears the selection.
- The bar at the bottom says how many are selected; **Select all** takes every book shown so far, **Tags** opens their
  tags: a tick means all of them have it, a dash some of them. Tick a tag to give it to all, clear it to take it from
  all, leave a dash as it is, or add a tag (type its name; **Create** makes a new one). Up to 1,000 books at a time.
- **Tag all** (next to Sort) does it for every book of the list on screen: the whole library, an author's or a tag's
  books, or a search's results. It says how many books that is first; choose the tags to give and to take away, then
  **Apply**. A big change runs in the background, shown in the panel under the top bar.
- A tag you take away stays away when the library is scanned or imported again.

## Authors, series, tags and other fields

- **Authors**, **Series** and **Tags** in the navigation list their names from A to Z, each with how many of its books
  are in the library you picked, the books of the names under it included, each once (a category counts its
  subcategories' books); names without books there are left out. **All fields** shows every list: translators,
  categories, publishers, languages, years, and the fields your libraries' imports brought (such as Calibre columns).
- **Go to** starts the list at the name you type: its first letters are enough.
- A name opens its page: what it is (an author, a series…), how many books, the tags it belongs under and the
  narrower ones, then its books, as covers or a list like the library's.
- Its address reads like it: `/author/1234/Stephen King`, `/series/88/The Dark Tower`, `/tag/5/To read` for a plain
  tag. The number finds it; the name is only for reading, so a link keeps working after a rename (the address shows
  the new name) or a merge (it opens the tag it went into). A field whose key is also a page of the app (a Calibre
  column named *books*) has its values under `/fields/books/…`.
- A series shows its books by their number in the series first; pick another order in **Sort** (books without a
  number appear only in the other orders).

## A book's page

A cover or row opens the book's page; **Back** returns to where you were, scrolled as you left it.

- Its cover, title, authors and series (each opens its page), its rating, then its other fields as chips: categories,
  publishers, languages, years, plain tags and your libraries' own fields. A title in italics is the file's name: the
  book's metadata gave none.
- **Download** gets the book's first file; **Files** lists every copy and format with its size and, for a book inside
  a ZIP archive, the archive's name, each with its own download button. A file missing since the last scan is shown,
  not offered. Downloading needs the right to download in that library; without it the page says so.
- **Details**: when the book was added, its identifiers (ISBN and others) and the values an import brought that are
  not fields (such as Calibre's custom columns).
- **Edit** (for those allowed to tag or edit books in that library, such as Curators) changes the book: its title,
  authors (drag them into order, or use the arrows), series and its number, its other fields (**Add a field** for one
  it has no value of yet), its tags, description and rating. Type a name to find a value; **Create** makes a new one.
  What you change stays when the book's file is read again or the library is imported again, and a tag you took off
  stays off.
- **Possible duplicates** lists other books you can see that may be the same one, with why: the same file (in
  another library), the same identifier, or the same title and author. An FB2 id counts only when the title or the
  first author is also the same: hand-made FB2 files often copy it from one another. Merging them comes later.
