# Browse: the library page, fields and tags

Code: `app/browse/browse-page.ts` (`BrowsePage`, the route `''`), `browse/book-list.ts` (`BookList`: tools, books,
paging, way back), `browse/list-window.ts` (`ListWindow`: the drawn rows), `browse/book-window.ts` (`bookWindow`,
`gridColumns`), `browse/browse-state.ts` (`BrowseState`); `app/tags/` (`FieldsPage` `fields`, `ValuesPage`
`fields/:key` and `tags`, `TagPage` `tags/:id`, `FieldCatalog`); `coverUrl`, `Api.books`, `fields`, `tagValues`,
`tag` in `core/api.service.ts`; styles `styles/browse.css`. Server: `GET api/books` ([catalog](catalog.md), keyset
pages, `sort` title / author / added / number), `api/fields`, `api/fields/{key}/values`, `api/tags`, `api/tags/{id}`
(`TagQueries`). Tests `browse.spec.ts`, `tags/tags.spec.ts` (client), `CatalogQueryTests` (both providers), e2e
`browse.spec.ts`, `fields.spec.ts`, `a11y.spec.ts`. D-5.5 in the [stage 5 plan](../development/stage-5-browse-plan.md).

- The home page is the library chosen in the switcher (`LibraryContext.current`): its name, its book count, the books
  as a grid of covers (`m`, 240 px) or a dense list (`s` covers, formats as badges). View and sort are kept per browser
  (`librarion.browse.view`, `librarion.browse.sort`). Page size: the account's `ui.pageSize` (50 by default).
- **Windowing:** only the rows in view and 4 more each side are in the DOM; the others are padding above and below
  (`bookWindow`). It needs every row the same height: grid cards have a fixed text block (two title lines, authors,
  series), list rows are 64 px. The row height is the pitch between the drawn rows' tops (measured each render, so
  density and width changes follow); the column count comes from the list's width and `--card-min` (140 px, 100 on
  phones) and is set as `grid-template-columns`, so CSS and the window agree.
- When the column count changes, that render's measure stops there: the rows drawn still have the old count, and a
  pitch or a restore taken from them was wrong (on the way back on phones it landed rows short).
- The next page is asked when the window's end is within 3 rows of what is loaded; answers to an earlier library or
  sort are dropped (`requested`). An error keeps the books loaded and offers Try again.
- **Coming back:** on leaving, `BrowseState.keep` holds the rows, the next key and how far down the list was, for the
  last 6 lists (library, value lists, tag pages; keyed by what they list and how); the same list shows them again
  without asking and scrolls back once a row is measured. Kept in memory for the tab (a reload starts at the top).
- Each book is a `li` with `aria-posinset` and `aria-setsize` (the library's count), as the list is partial.
- Every book is a link to its page ([book-page](book-page.md)): `a.book-link` inside the `li`, which keeps the layout's
  measures.

## Fields and tags (chunk 14)

- The sidebar: Library, Authors (`fields/author`), Series (`fields/series`), Tags (`tags`), All fields (`fields`: every
  field of the tenant, built-in ones first, custom ones by name, then Tags). Items are lit on their own path only
  (`exactPath`: a tag page lights none, `?from=` keeps Authors lit). The pages load on first use.
- **A list of values:** A–Z by sort name (index `(TenantId, FieldId, SortName, Id)`), each with its books in the library
  being browsed (`TagCounts`); values without a book there are skipped. To keep a request bounded, the server reads at
  most `TagQueries.MaxScanned` (2,000) rows per page and then returns what it found with a `next`: a sparse library's
  list may come as short (even empty) pages, and the client goes on asking while `next` is set (it shows the
  skeleton, not "empty", until then). Rows are windowed as the books are (`ListWindow`, columns of `--value-min`
  240 px, one column on phones; rows 44 px).
- **Go to:** typing (300 ms pause) or Enter sets `?from=` (replacing the address); the server starts at the first sort
  name at or after it, its first letter upper cased ("kin" finds "King, Stephen"). Sort names compare in the database's
  order (SQLite: binary, so lower-case names sort after "Z"): the jump follows the list's own order.
- **A tag's page:** what it is (its field's singular name, linking to the field's list; "Tag" for plain tags), its
  name, "N books in <library>", the tags it is under and up to 50 under it (chips; "and more" past 50, then any 50 by
  the parent index), then `BookList` with the tag. A series sorts by number first (`sortScope` `series`, remembered
  apart as `librarion.browse.sort.series`; books without a number are left out of that sort, the empty text says so).
- Names: built-in fields by `fields.name.<key>` / `fields.one.<key>` (`ClientI18nTests` checks every built-in key),
  custom ones by their own name; `language:` values by `Intl.DisplayNames` in the UI language ("ru" → Russian).
- The library asked about must be one the caller browses (else 404: no counts of other libraries). A tag the caller's
  content filters deny in that library (every group admitting it there denies it), and the tags under it (at most
  500 listed), are left out of the lists and their pages are 404.
- **Known limits:** counts are the library's, not what a filtered account may see (an author whose books are all
  filtered out is still listed, with their count); `TagCounts` counts direct links, so a parent tag's count leaves out
  books tagged only with a narrower tag (its book list includes them).

- The administration is loaded on first use (`loadComponent`; its matcher in `admin/admin-route.ts` so the routes don't
  pull the page in): the initial bundle went from 513 kB (over the 500 kB budget) to 406 kB.
- e2e gotchas: `toBeInViewport` reported a drawn, painted card as out of view under mobile emulation after the space
  above it changed: the test checks the bounding box instead. Mobile WebKit has no `mouse.wheel`: scroll by script.
  WebKit reports a poll cut off by `page.reload()` as an "access control" error.
