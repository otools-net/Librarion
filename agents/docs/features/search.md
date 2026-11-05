# Search

Code: `Core/Search/` (`SearchQuery` the language, `SearchText` folding, `ISearchIndex` with `SqliteSearchIndex` and
`PostgresSearchIndex`, `BookSearch` terms as conditions, `SearchSuggestions`, `SearchFacets`), `BookQueries.ListAsync`
(`q`), `Books.SearchText`; routes `GET api/books?q=`, `GET api/search/suggestions`, `GET api/search/facets`
(`BookEndpoints.cs`); client `shell/search-box.ts` (`SearchBox`), `search/search-page.ts` (`search?q=`),
`search/facets.ts` (`Facets`, Refine), `BookList` (`query`, `[listTools]`). Tests `SearchTests`,
`CatalogQueryTests.ASearchWalksEveryMatchOnceInOrderWhereverItStarts` (both providers), `search/search.spec.ts`, e2e
`search.spec.ts`, `a11y.spec.ts`; benchmark scenarios `search: …`, `suggestions: …`, `facets: …`.
Decisions D3, D20, D22 in [foundation-plan](../development/foundation-plan.md), D-5.7 and D-5.8 under
[Decisions](#decisions-stage-5-confirmed-by-the-user-2026-11-01). User guide: `docs/user-guide/searching.md`.

## The language (`SearchQuery.Parse`)

- Terms split on spaces outside quotes, at most 20, 500 characters. `-` before a term negates it.
- `key:value` when `key` is `title`, `tag`, `format` or one of the tenant's fields (`FieldKey`); any other key is
  text ("Re:Zero" is a title). `field:` alone = any value of the field (`-series:` = none).
- A bare word of free text is a prefix (it may be being typed); `"phrases"` are not unless followed by `*`; values
  are prefixes only with `*` (or an unclosed quote).

- A key is a field's own key or the key of a field merged into another (`FieldKeyAliases`, stage 6):
  `BookSearch.KeysAsync` reads queries (books, facets, suggestions); `FieldsAsync` lists fields, each once.

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

## Facets (`SearchFacets`, Refine; D-5.8)

- `GET api/search/facets?library=&q=`: per field the 8 values with the most books, then formats; each value carries
  the `term` that finds its books (`category:"Fantasy"`, the quoting of suggestions: `SearchFacets.Term`). Order:
  category, author, series, language, format, publisher, year, other fields by key, plain tags. Hidden and built-in
  tags never listed.
- **Nothing narrows** (no `q`, the library open to the caller): `TagCounts`, nothing counted. A field with more than
  2,000 values (authors, series, publishers at 2M) is left out: its most frequent values can't be found without
  reading every count (no index by count; a `FieldId` on `TagCounts` with one would allow it). No formats there.
- **A search, or a library behind content filters:** counted over at most **2,000 matches** (`MaxCounted`), `capped`
  past it (the client says "2,000+ books"). `BookSearch.SampleAsync` reads them **unordered from the narrowest term's
  own source** (`Condition.Source`: the title index, the links of its tags, a format's files; a start list already
  read when under the threshold), then tests each against every term; only negations: the first matches by title (the
  sort index walked: unordered it was a bare `SCAN`).
- Counting: the sample's links read once (about 15k rows for 2,000 books) and counted in memory, each book once under
  a tag and every tag above it (the parents' edges, `TagParents`, read whole up to 10,000 and walked in memory), then
  the counted tags' fields looked up by key. Tried first and slower at 10,000 books on SQLite: grouping in the database
  through the closure with `COUNT(DISTINCT)` 540 ms; grouping by tag joined to `Tags` 210 ms.
- The cap is the cost: at 2M on SQLite a broad word took 130 ms with 2,000, 290 with 5,000, 480 with 10,000 (D-5.8
  said 10,000; the user kept 2,000, 2026-11-05). Past it the total is shown as "2,000+ books".
- Plain tags are `FieldId IS NULL` minus the fields' own rows **by id**: with `FieldKey IS NULL` SQLite walked that
  index over nearly all 660k tags (111 ms).
- The client asks again whenever the library or the search changes; the button stays disabled without values.
  Choosing a value already in the search (its term, exactly) takes it out; the minus adds `-term`.

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
  scanned `Books` for the 1 % group. Tried and reverted. What works there (2026-11-05): on PostgreSQL the probe is
  `(SELECT count(*) …) > 0`, a scalar subquery the planner never turns into a join; with `EXISTS` a low estimate of
  the tag (744 links for 20k) made it start from the tag and sort its books (100–127 ms, now 31–39).
- The facets of a filtered library sorted the filter's books by title to take 2,000 (SQLite 305 ms for the 1 %
  group): when the filter is read from its own tags (`BookQueries.StartsFromTagsAsync`) the sample is unordered; an
  unordered read with a per-book test would scan `Books` (the 0.1 % group by id walked all 2M rows), so the title
  order stays there.
- Measured after: [scale](../development/scale.md#measured).

## Decisions (stage 5, confirmed by the user 2026-11-01)

- **D-5.7 Search language:** `field:value` for every field, `tag:` with descendants, `format:`, `-` negation, quoted
  phrases, hidden tags honoured; parsed on the server into the same keyset queries; suggestions as you type (fields,
  then values by prefix through the `(TenantId, FieldId, NormalizedName)` index).
- **D-5.8 Facets with capped counts:** counts from `TagCounts` when nothing narrows the list; otherwise counted over
  at most the first matches, never a count over the catalog per request. The cap is **2,000** (proposed 10,000:
  480–780 ms on SQLite at 2M, 5,000 290–420, budget 300); kept by the user (2026-11-05), with "2,000+ books" past it
  so the user knows there are more. Without a search, fields of more than 2,000 values (authors) have no facet (no
  index by count). Shown as Refine: a button by Sort opening a sheet, on the library page and the search results.
