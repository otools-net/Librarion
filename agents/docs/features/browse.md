# Browse: the library page

Code: `app/browse/browse-page.ts` (`BrowsePage`, the route `''`), `browse/book-window.ts` (`bookWindow`, `gridColumns`),
`browse/browse-state.ts` (`BrowseState`); `coverUrl` and `Api.books` in `core/api.service.ts`; styles `styles/browse.css`.
Server: `GET api/books` ([catalog](catalog.md), keyset pages, `sort` title / author / added). Tests `browse.spec.ts`
(client), e2e `browse.spec.ts`. D-5.5 in the [stage 5 plan](../development/stage-5-browse-plan.md).

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
- **Coming back:** on leaving, `BrowseState.keep` holds the books, the next key and how far down the list was; the
  same library and sort shows them again without asking and scrolls back once a row is measured. Kept in memory for
  the tab (a reload starts at the top).
- Each book is a `li` with `aria-posinset` and `aria-setsize` (the library's count), as the list is partial.
- Books are not links yet: the book page comes in chunk 15.
- The administration is loaded on first use (`loadComponent`; its matcher in `admin/admin-route.ts` so the routes don't
  pull the page in): the initial bundle went from 513 kB (over the 500 kB budget) to 406 kB.
- e2e gotchas: `toBeInViewport` reported a drawn, painted card as out of view under mobile emulation after the space
  above it changed: the test checks the bounding box instead. Mobile WebKit has no `mouse.wheel`: scroll by script.
  WebKit reports a poll cut off by `page.reload()` as an "access control" error.
