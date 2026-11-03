# Search

Code: `Core/Search/` (`SearchQuery` the language, `SearchText` folding, `ISearchIndex` with `SqliteSearchIndex` and
`PostgresSearchIndex`, `BookSearch` terms as conditions, `SearchSuggestions`), `BookQueries.ListAsync` (`q`),
`Books.SearchText`; routes `GET api/books?q=`, `GET api/search/suggestions` (`BookEndpoints.cs`); client
`shell/search-box.ts` (`SearchBox`), `search/search-page.ts` (`search?q=`), `BookList` (`query`). Tests
`SearchTests`, `CatalogQueryTests.ASearchWalksEveryMatchOnceInOrderWhereverItStarts` (both providers),
`search/search.spec.ts`, e2e `search.spec.ts`, `a11y.spec.ts`; benchmark scenarios `search: …`, `suggestions: …`.
Decisions D3, D20, D22 in [foundation-plan](../development/foundation-plan.md), D-5.7 in the
[stage 5 plan](../development/stage-5-browse-plan.md). User guide: `docs/user-guide/searching.md`.

## The language (`SearchQuery.Parse`)

- Terms split on spaces outside quotes, at most 20, 500 characters. `-` before a term negates it.
- `key:value` when `key` is `title`, `tag`, `format` or one of the tenant's fields (`FieldKey`); any other key is
  text ("Re:Zero" is a title). `field:` alone = any value of the field (`-series:` = none).
- A bare word of free text is a prefix (it may be being typed); `"phrases"` are not unless followed by `*`; values
  are prefixes only with `*` (or an unclosed quote).

## The index (D3: the database's own)

- `Books.SearchText` = `SearchText.ForBook(title, file title)`: NFKD, marks dropped, lower case, anything but
  letters and digits a space (ё reads as е); the file name too when it differs (lib.rus.ec's numbers are found).
  Folded in C# so both providers hold the same words (PostgreSQL's parser reads "a.b" as a host). Set by
  `CatalogWriter.Apply` (and the bulk columns) and by the generator.
- **SQLite:** FTS5 tables `BookSearch` (over `Books.SearchText`) and `TagSearch` (over `Tags.NormalizedName`), external
  content (the index only), `unicode61 remove_diacritics 2`, kept by triggers on insert, delete and update of the
  column: `BulkRows` inserts reach them too.
- **PostgreSQL:** GIN indexes on `to_tsvector('simple', "SearchText")` and on the tag names with punctuation made
  spaces; queries use the very expression. No stemming (every language). Tag names keep their accents there: each
  word is asked as typed and folded (`(jose:* | josé:*)`); a name typed without its accents is not found on
  PostgreSQL (known limit). No trigram yet: prefixes cover typing, fuzzy matching is on demand only (D21).
- Neither is part of the EF model: `ISearchIndex.EnsureAsync` makes them after the migrations
  (`LibrarionDatabase.InitializeAsync`; `IF NOT EXISTS`, a new FTS table rebuilt from its content), so the
  regenerated Initial migrations never lose them. The benchmark generator drops and rebuilds the GIN indexes around
  its load like the others; imports keep them up to date row by row.

## Terms as conditions (`BookSearch`)

- A search is conditions on the visible books in the same keyset query as every list: pages by key, sorts by title,
  author, newest (and a series' numbers); no relevance order (it can't page by key).
- **Where the database starts** (as tags, D22): each positive term gets a size (title matches read up to
  √(page × books); tags' `TagCounts`). The smallest under that threshold is read **into a list of book ids** the
  query starts from (`Id IN (list)`: seeks, then only they are sorted); the others are tests of each book as the sort
  index is walked. A list, not a subquery: from `Id IN (index query)` PostgreSQL walked the title index computing
  every book's words (11 s for one book), and from `Id IN (links of tags)` it sought every listed tag for each book.
- Free words: the title (`ISearchIndex.TitleTest`) OR a link to a tag whose name has the words (the tag-name index,
  D20; up to 2,000 tags as a list, more as the index's query). Fields and `tag:`: the name or an alias exactly,
  else by prefix (`*`), else the field's values having the words (`author:king`); with descendants
  (`BookQueries.DescendantsAsync`, past 500 through the closure).
- Tag ids are read **from the index first** (at most 2,000, unordered), then looked up by key.
- Hidden tags (`IsHidden`, a filtered index finds them) keep their books out unless the search names the tag or
  one above it. Content filters apply as in every list (`VisibleAsync`).
- The library asked about must be one the caller browses (else 404).

## Suggestions (`SearchSuggestions`)

- For the last term (after the last space outside quotes; `from` tells the client where it starts): fields whose key
  starts with it (2 letters at least), then field values and plain tags whose name has its words (index, at most 200
  read), ranked by their books in the library (those without books there left out, denied tags never), 8 at most.
  After `author:` or `tag:` the field's values by name prefix and by word; with nothing typed, its first values A–Z.
- The client asks 150 ms after typing stops; a field chosen asks again for its values; a value ends the term with a
  space. Arrows, Enter, Escape; `role="combobox"` with `aria-activedescendant`. The options cancel `mousedown`, not
  `pointerdown`: on iOS WebKit a cancelled `pointerdown` lost the tap's `click`.

## Plans that went wrong at 2M (2026-11-03), and the rule each gave

Both providers run the same LINQ; where they need different SQL, `ISearchIndex` or `BookSearch.Linked` choose by
provider.

- `Tags WHERE FieldKey IS NOT NULL` scanned all 660k tags on SQLite (95 ms on every search, and on `api/fields`):
  read the fields `ORDER BY FieldKey` so the index (TenantId, FieldKey) is walked.
- `Tags WHERE Id IN (index matches) ORDER BY Id LIMIT n` walked the table by id (SQLite): read ids from the index
  first, then look the rows up by key (`BookSearch.MatchedTagsAsync`).
- A capped read of the index's matches **ordered** sorted every match first: leave capped reads unordered when the
  order changes nothing (all of them, or "too many").
- **Testing a book against a word, SQLite:** `Id + 0 IN (FTS matches)` (read once into a list of compact ids) and
  `TagId + 0` in the links' test: the `+ 0` keeps SQLite from starting from the matches, and from seeking every
  listed tag for each book (1 s for a common word).
- **The same, PostgreSQL:** the book's own `to_tsvector('simple', SearchText) @@ query` (a few µs): reading the
  matches first hashed 120k titles from the heap (1.7 s). In a link's test the redundant `BookId <= b.Id` keeps the
  `EXISTS` a test of each book: with the equality alone, under an `OR` it became "among all books linked to these
  tags" and read every one of their links first; with `TagId + 0` it scanned all 15M links.
- `SELECT DISTINCT BookId … WHERE TagId = ANY(…)` read PostgreSQL's whole key of BookTags for ids in order (1.6 s):
  read the links plain (fewer than the threshold: their count says so) and make them distinct in memory.
- EF 10 sends a list to SQLite as one parameter per id: a start list of 10,000 ids made SQLite compile a new statement
  of 10,000 parameters each time (330 ms). `EF.Parameter(list)` sends it as one JSON parameter (`json_each`).
- Npgsql translates `Regex.Replace` as `regexp_replace(…, 'p')` (the first match only): not used.
- The redundant `BookId <= b.Id` does **not** help the content filters' probe (`BookQueries.Probe`): PostgreSQL then
  scanned `Books` for the 1 % group. Tried and reverted.
- Measured after: [scale](../development/scale.md#measured).
