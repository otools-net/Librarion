# Searching

The box at the top of every page searches the library you picked. Type and press **Enter**: the results are a list
like the library's (covers or list, sorted by title, author or newest), and **Back** returns to them.

## Words

- `tolkien hobbit` finds books with **every** word in their title, their file name, or the name of one of their
  authors, series, publishers, categories or tags.
- Case and accents don't matter in titles (`cafe` finds *Café*); a word still being typed matches longer ones
  (`hobb` finds *Hobbits*).
- `"the dark tower"` (in quotes) needs the words together, in this order.
- A book without a title is found by its file name (`123456` finds `123456.fb2`).

## Fields and tags

- `author:"Stephen King"`: books by that author (or one of the names merged into it). The same works for every field:
  `series:`, `category:`, `publisher:`, `language:`, `year:`, `translator:` and your libraries' own fields
  (such as Calibre columns). Without quotes a single word is enough: `author:king` finds every author with *king* in
  the name.
- `author:ki*`: authors whose name starts with *ki*.
- `series:` alone: books in any series.
- `category:Fantasy` also finds the categories under it.
- `tag:Favourites`: books with a tag, or with a tag under it; `tag:"category:Horror"` names a field's value.
- `format:epub`: books with an EPUB file.
- `title:dune`: the word in the title only.

## Leaving out

- A minus before any term leaves its books out: `-format:pdf`, `-author:"Stephen King"`, `-series:` (books in no
  series), `-tolkien`.
- Books with a hidden tag (such as *Archived*) are left out of searches unless the search names that tag
  (`tag:Archived`).

## Refine

The results say how many books match. **Refine** (next to **Sort**) lists the values found most often among them, for
each field (categories, authors, series, languages, formats, publishers, years, tags), with how many of the books
have each:

- Pick a value to keep only its books: its term is added to the search (`category:"Fantasy"`).
- The minus beside it leaves its books out instead (`-format:pdf`).
- A value already in the search is marked; picking it again takes it out.

With more than 2,000 matches the counts are over the first 2,000 found, and the results say "2,000+ books".

## Suggestions

While you type, the box suggests what the last word may be: a field (`author:`), then authors, series, tags and other
values whose name has your words, those with the most books in the library first, with their count. Pick one with the
arrows and **Enter**, or tap it: it takes the place of what you typed. After a field, its values are suggested.

Books and tags your groups don't let you see are never found or suggested.
