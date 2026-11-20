# E-reader mode: a plain gray site for e-ink devices

Code: `src/Librarion.Api/EReader/` (`EReaderEndpoints`, `EReaderGuard`, `EReaderSwitch`, `EReaderLinks`, `EReaderDevices`,
`EReaderFrame`, `ServerTexts`, `EReaderStylesheet` + `ereader.css`, `Pages/*.razor`), the same `Core/Catalog/` and `Core/Search/`
services as the app, `ClientPage` for the redirect (`EReaderRedirect`); tests `EReaderTests`, `EReaderListTests`,
`EReaderWaysInTests`, `AddressKeyTests.EReader`; e2e `tests/e2e/specs/ereader.spec.ts`; benchmark `ereader`. Decision D23
in [foundation-plan](../development/foundation-plan.md); built in stage 8 (0.9.0). User guide:
`docs/user-guide/ereader.md`.

A second, **basic face of the library for the browsers of e-ink readers** (Kindle, Kobo, PocketBook, Boox, Tolino):
find a book, look at it, download it to the device. Not the in-app reader ([reader](reader.md)): nothing is read
here, the device's own reader opens the downloaded file.

## Decisions (stage 8, the user's answers 2026-11-11)

- **Rendering (Q1):** Razor components rendered statically (`RazorComponentResult`), not hand-written templates
  (every value encoded by hand is easy to get wrong).
- **Paging (Q2):** Next and First plus the browser's Back; no Previous (every sort walked backwards: twice the list
  queries).
- **Keys in the address (Q3):** the `opds` scope, renamed "Reader apps and e-readers", with the key's **Can be used in
  the address**; no scope of its own.
- **Ways in (Q4, changed by the user):** the link on the app's sign-in page and a server-side redirect of e-ink
  browsers (their script can't be relied on), with Full site to opt out; nothing in the app's menu.
- **Formats a device can't open (Q5):** after the others, dimmed, still downloadable; never hidden.
- **Stay signed in (Q6):** ticked by default (typing on an e-ink keyboard is slow).
- **Page size (Q7):** 20 books with small covers, not the account's page size.
- **Look (D-8.9, changed by the user):** grays and short gray gradients, not only black and white.
- Later: "converted for this device" with stage 9 ([conversion](conversion.md)).

## Why a separate face

- The browsers of e-ink devices are old or limited WebKit/Chromium builds: slow JS, little memory, partial CSS; the
  Angular app is too heavy for them, and its scrolling, transitions and colours smear on e-ink.
- So the mode is **server-rendered HTML that works with JS off**: links and plain forms only, one small stylesheet,
  no client framework, no script at all (`default-src 'none'` in its CSP). Rendered by Razor components rendered
  statically on the server (`RazorComponentResult`: HTML-encoded by default, no Blazor runtime sent).

## Design

- **Grayscale only:** black text, white and grays; no colour with a hue anywhere (a test checks the stylesheet).
  Grays mark areas (the top bar, buttons, notices, headings, every other row of a list, `.panel` for a group) and
  short gray gradients give them shape (the user's decision, 2026-11-11): two close grays per gradient (older panels
  show about 16 levels: a long gradient bands), a plain `background-color` before each for browsers without
  gradients; text black or `#444` on `#ccc` or lighter. No dark theme, custom properties (old e-ink WebKit), animations,
  transitions or hover effects (they make the panel redraw); a pressed button darkens (`:active`).
- **Big and plain:** system fonts, large text, tap targets ≥ 44 px (e-ink touch is imprecise), generous line height;
  fits 600 × 800 and up, portrait first; nothing needs a long press or a swipe.
- **Pages, not scrolling:** lists of 20 items with small covers, **Next** and **First** links (keyset cursors in the
  query string, [scale](../development/scale.md); back is the browser's Back, no Previous: it would need every sort
  walked backwards), so each screen is one full refresh; no infinite scroll, no virtual lists.
- Covers as the small thumbnails (`s`) the app already serves ([book-display](book-display.md)); every image has
  its width and height so the page does not reflow while it loads.

## Screens (and nothing else)

- **Sign in** (plain form, same accounts, lockout and throttle as the app) and **sign out**.
- **Home:** search box, recently added, links to browse by field.
- **Search / list:** cover, title, authors, series; the same search language as the app
  ([metadata-tags](metadata-tags.md)), a simple box only (no facets panel).
- **Browse** authors, series, categories, languages A–Z, then that value's books.
- **Book page:** cover, title, fields as tags (authors, series and number, categories, language, year, publisher),
  plain tags, description (sanitised text), and its files: format, size, **Download** each.
- No reading, rating, tagging, editing, merging, uploads, administration or settings beyond the device formats below.

## Downloads

- The same download route and rights as the app (`library.download`), streamed from loose files or container zips
  ([archives](archives.md)); FB2 also offered as `fb2.zip`, as OPDS.
- **Device formats:** the formats the device opens are listed first and the rest dimmed, guessed from the
  User-Agent, changeable on the device (kept per browser): built in chunk 7, below. A book without a fitting format
  will offer it converted (an FB2 as AZW3 for a Kindle, [conversion](conversion.md), stage 9; planned); a conversion
  still running will answer with a "being prepared, try again" page.
- File names in `Content-Disposition` as plain ASCII `filename` plus UTF-8 `filename*` (old browsers mangle UTF-8
  names), extension always matching the format.

## Access

- Address `/e/` (short, to type on an e-ink keyboard), linked from the app's sign-in page. E-ink browsers (by
  User-Agent) asking for the app's HTML are **redirected to `/e/` by the server** (a book's address to its `/e`
  page); a **Full site** link on `/e` sets a cookie that stops it, so a wrongly detected browser is never stuck. Never
  `/api`, `/opds` or files; no redirect when the mode is off.
- Cookie session like the app; **Stay signed in** ticked by default (typing a password on an e-ink keyboard is slow);
  forms carry the antiforgery token. Devices whose browser loses cookies can use a path API key (`/e/k/<key>/…`, the
  `opds` scope renamed "Reader apps and e-readers", opt-in per key with **Can be used in the address**, redacted from
  logs), as [opds-auth](opds-auth.md).
- Rights: the existing `library.browse` and `library.download`, content filters (`Visible()`) and tenants as everywhere
  ([rights](rights.md)); a tenant setting can turn the mode off.
- Texts through the same translation keys (`en.json` read by the server), the UI language as the user's.

## Built (stage 8 chunk 1): the frame

- **Pages** are Razor components (`Pages/`) returned as `RazorComponentResult` by minimal API routes; `EPage` is the
  frame (doctype, `<title>`, the stylesheet, the app's name as the link home, the `<h1>`), `MessagePage` a page that
  only says something. Links come from `EReaderLinks` (absolute, from `PathBase`): never write an address by hand,
  chunk 8 adds the key prefix there. Pages get it as a parameter; texts by `@inject ServerTexts Texts` (`_Imports.razor`).
- **No script:** Razor's static rendering adds nothing (no Blazor markers, no `blazor.web.js`: `MapRazorComponents` is
  not used); `EReaderTests.PlainPageAsync` checks every page for `<script`, `on…=` handlers, `javascript:`, the CSP.
- **Texts:** `en.json` is embedded in the API assembly (`Librarion.i18n.en.json`, the csproj links the client's file),
  read by `ServerTexts` (`Texts["key"]`, `Format` for `{name}`); `EveryTextKeyTheModeUsesExists` reads the keys used
  in `EReader/`. Keys of the mode: `ereader.*`; shared ones reused (`app.name`, `error.forbidden`).
- **Stylesheet:** `ereader.css` embedded (`Librarion.EReader.ereader.css`), served at `/e/ereader.css?v=<hash>`,
  immutable, ETag; `TheStylesheetHasNoColour` allows hex grays only (gradients and shadows included, each gradient
  with a plain fill in its rule) and no custom property, transition, animation, hover, filter, `url(`, `@import`, dark
  theme. Fonts: `serif` (the device's own).
- **Errors as pages:** `EReaderGuard` (see [sessions-and-pipeline](../architecture/sessions-and-pipeline.md)): setup
  503, switch 404, and any bodiless 400/403/404/405 rendered as a plain page; a visitor without a session is sent to
  `/e/signin?return=…` by the cookie's `OnRedirectToLogin` (the sign-in page itself is chunk 2).
- **Switch:** `ereader.enabled` (tenant, on), read by `EReaderSwitch` (cached per tenant until `AccessChanges` moves).
- The home page lists the libraries the account may browse (a placeholder until chunk 3); `library.browse` needed.

## Built (stage 8 chunk 2): sign in and out

- **One sign-in:** `/e/signin` posts a plain form (`userName`, `password`, `remember`, `return`: the app's field
  names) to `SessionEndpoints.TrySignInAsync`, the app's own path (throttle by address, account lockout, security log,
  the session cookie), so a session from either opens both. Refused: the form again, 400, the name kept, never the
  password; locked out: "try again in N minutes" (the device's clock and zone are unknown, so no time of day). The
  `sign-in` rate limit applies as on the API (a 429 page). A disabled account reads as a wrong password.
- **Stay signed in** (`signIn.remember`) is ticked to begin with (Q6): the cookie then outlives the browser
  (`Librarion:Security:SessionDays`); unticked, a browser-session cookie.
- **Return address:** the cookie challenge sends `/e/signin?return=<path>`; `EReaderEndpoints.SafeReturn` keeps only
  `/e`, `/e/…`, `/e?…` (no `//`, no backslash, no control characters, not the sign-in or sign-out itself), else
  `/e/`. Already signed in, the sign-in page goes straight on.
- **Sign out** is a POST form in every page's header (with the account's name): GET never changes anything; then
  the sign-in page.
- **Form tokens (D-8.4):** `EReaderFrame` carries the antiforgery token set (`IAntiforgery.GetAndStoreTokens`, cookie
  `librarion.antiforgery`, its `X-Frame-Options` suppressed so ours stays DENY); `FormToken.razor` writes the hidden
  field. `EReaderGuard` checks every `/e` request but GET and HEAD (`IsRequestValidAsync`) before any route: refused, a
  "The form has expired" page (400). Handlers read the form themselves (`ReadFormAsync`): `[FromForm]` binding would
  ask for `UseAntiforgery`'s metadata check, which the app doesn't use.
- **Gotcha:** a minimal API handler taking only `HttpContext` and returning `Task<IResult>` binds as a
  `RequestDelegate` and its result is dropped (a 200 with no body): add a `CancellationToken` parameter (as
  `SessionEndpoints` does).

## Built (stage 8 chunk 3): home, a library's page, lists of books

- **Addresses:** `/e/` home; `/e/l/<library>/` a library's page; `/e/l/<library>/recent` and `/titles` its books
  newest first and A to Z (`?after=<cursor>`); `/e/l/<library>/books/<id>/cover/<s|m|l>` covers (under `/e`, so the
  keys in the address of chunk 8 cover them too). Book pages (`/books/<id>`), search (`/search?q=`), fields
  (`/fields/<key>`) and tags (`/tags`) are linked already and built by chunks 4–6. All in `EReaderLinks`.
- **Home:** with one library to browse, its page itself (one refresh less, as the OPDS root); with several, a list
  of them. A library one can't browse (or of another tenant) is 404, never 403 (not told apart from none).
- **A library's page:** the search box (a GET form, no token needed), the 5 books added last
  (`RecentOnLibraryPage`) with "All recently added", then Browse: all books by title, every field with values
  (`TagQueries.FieldsWithValuesAsync`, built-in ones by the app's names `fields.name.<key>`), Tags.
- **Lists** (`BooksPage`, `BookList`, `Pager`): 20 a page (`PageSize`, Q7) by `BookQueries.ListAsync` under the
  account's visibility (content filters); a row is one link: the `s` cover shown at 48 × 72 (`width`/`height` set,
  `object-fit: contain`, a gray box behind), the title bold (else the file's name, else "Untitled"), authors, series
  and number (`0.##`). First (from page 2) and Next (`rel="next"`, at the right). A cursor that doesn't read starts
  at the beginning (`KeysetCursor.Decode`).
- **Razor gotchas:** a component's `string` parameter given without `@` is the literal text (`First="First"` passed
  "First"; write `First="@First"`; parameters of other types are C# expressions either way). An empty attribute
  value renders bare (`alt=""` → `alt`), and void elements end in ` />`: tests match loosely.
- Screenshots of these pages: [screenshots](../../tools/screenshots/README.md) takes `e/…` addresses at 600 × 800 with
  JavaScript off (`LIBRARION_SCREENS_BOOKS=25` for a pager).

## Built (stage 8 chunk 4): search

- **`/e/l/<library>/search?q=<words>&after=<cursor>`** (`library.browse`): the search language as the app's box and
  the OPDS search ([search](search.md)), through `BookQueries.ListAsync(Query:)` by title, 20 a page by key; Next keeps
  the words (`EReaderLinks.Search(library, q, after)`). Hidden books (content filters, hidden tags) as everywhere.
- **The page** (`SearchPage`): the box again with the words kept (`SearchBox`, also on the library's page), "Books
  matching …", the list and First / Next.
- **Said plainly** (the language has no errors: it never refuses, it leaves out): nothing to search for (blank, only
  signs such as `-`, `""`, `tag:` with no value) → no query run, "Type words…" and **How to search** (`SearchHelp`:
  the language's signs as typed, `<code>`, over the app's `search.help.*` texts); nothing found → "No books match this
  search." and the help; cut short (over 500 characters or 20 terms: `SearchQuery.Cut`) → a notice that only its
  start was searched. Whether a search is empty or cut is read with `Parse(q, _ => false)`: a key that is no field is
  still one text term, so neither depends on the tenant's fields.
- `.notice` (light gray, a gray edge) for things to know; `.error` stays for refusals.
- The mode's text-key test also reads `Texts.Format("…")` and whole keys written as strings in the pages.

## Built (stage 8 chunk 5): browse

- **Addresses** (`EReaderEndpoints.Browse.cs`, `library.browse`): `/e/l/<library>/fields/<key>` and `/tags` (plain
  tags) with `?from=<letter>` (empty: from the start) and `&after=<cursor>`; `/tags/<id>` a tag's page;
  `/tags/<id>/books?after=` its books. As the OPDS feeds ([opds](opds.md)), the same `TagQueries` calls.
- **A field's page:** its values fit one page → the list itself (up to the library); more → **first letters**
  (`TagQueries.InitialsAsync`, cached per tenant) as a grid of buttons (`LettersPage`, ≥ 52 × 44 px) and "All, from
  the start" (other scripts' names without a letter are reached so). A letter's page ("Authors: Б") lists from there
  by key (up to the letters); Next keeps `from`, so First goes back to the letter's start. Pages of a sparse library
  can be short (`TagQueries.MaxScanned`).
- **A value's row** (`ValueList`): its name and its books in the library ("2 books", `browse.count`), one link.
- **A tag's page:** tags under it → "All its books" (with the count) and the tags under it (`TagPage`; "More tags
  are under it" past `MaxChildren`); none → its books directly. Books by title; a **series by number** (the cursor
  tells the order; a series without numbers by title), as OPDS. A merged tag's old address → 301 to the tag it
  became; a tag the content filters deny, or with no book here, 404.
- Field names in one place: `ServerTexts.FieldName(key, name, builtIn)` (`fields.name.<key>` for built-in ones).
- **Encoding:** the Razor renderer took the default `HtmlEncoder` (Latin only): every Cyrillic letter came out as
  `&#x411;` (8 bytes for 2). `AddWebEncoders` with `UnicodeRanges.All` in `Program.cs`: text as UTF-8, markup
  characters still escaped (the search test checks `<b>` and quotes).

## Built (stage 8 chunk 6): the book page and downloads

- **`/e/l/<library>/books/<id>`** (`library.browse`, `EReaderEndpoints.Book.cs`, `BookPage`): `BookQueries.GetAsync`
  under the account's visibility, as the app's page. The title (`ServerTexts.BookTitle`: else the file's name, else
  "Untitled"), the `m` cover in a 120 × 180 box beside the fields; the fields in the tenant's order
  (`TagQueries.FieldsAsync`), each value a link to its books (`/tags/<id>`), a series with the book's number, plain
  tags last; then **Files** (format, size as the app's "1.2 MB", the name, "in Pack.zip" for an entry, a missing
  file said and never offered) with **Download FB2** and, for FB2, **Download FB2, zipped**; the description last
  (files first: downloading is what most come for).
- **Description:** plain text already (the importers strip markup), so a line is a paragraph, spaces collapsed, cut
  at `MaxDescription` (2000) at a word with "…"; nothing in it is read as markup (a text's `<desert>` stays).
- A merged book's address → 302 to the kept book (a merge can be undone); a book of another library the account
  browses → its own address; hidden or elsewhere → 404. Without `library.download` the page says so (`book.noDownload`)
  and offers no link.
- **Downloads:** `/books/<id>/files/<fileId>` and `…/zip` (`library.browse` and `library.download` on the route, the
  library checked again inside), through the app's `BookEndpoints.Download` (Range, ETag, ZIP entries) and OPDS's
  `ZippedDownload`. A file gone, changed or unreadable since the scan: a plain page with the app's `error.file.*`
  text and its 410 / 409, never the problem JSON.
- **File names (D-8.8):** `DownloadNames` (`Api/Endpoints/`), used by the app, OPDS and `/e`: `filename*` in UTF-8 and
  an ASCII `filename` with accents folded ("Café" → "Cafe"), other letters as one `_` (trimmed at the ends), and a
  name with no Latin letter or digit left as `book-<file id>.fb2`. ASP.NET's own fallback was `_____ _ ___.fb2`.
- Gotcha: the tool layer turns a typed backslash-u-00a0 into the character itself; write `\xA0` in C# sources.

## Built (stage 8 chunk 7): device formats

- **The device** (`EReaderDevices`, `EReaderDevice`): five families, each with the format keys its own reader opens,
  best first: Kindle AZW3, AZW, MOBI, PDF, TXT; Kobo KEPUB, EPUB, PDF, CBZ, CBR, MOBI, TXT, RTF; PocketBook FB2,
  EPUB, PDF, DJVU, MOBI, AZW3…; Tolino EPUB, PDF, TXT; Boox EPUB, FB2, PDF, DJVU… (a test checks every key is a
  known format). Guessed from the User-Agent: `Kindle/` without `Silk` (a Fire tablet is no e-ink device), `Kobo`,
  `PocketBook`, `tolino`, `BOOX` or `Onyx` (Boox models naming neither aren't recognised: no order, which is safe);
  anything else none. Chunk 9's redirect uses the same guess.
- **The choice** wins over the guess: cookie `librarion.device` (a family's key, or `none` for "another device: no
  order"; absent = automatic), path `/e` (with `PathBase`), a year, HttpOnly, Lax. An unknown value in the cookie is
  ignored (the guess again).
- **Where it shows:** `EReaderFrame.Device` (every page has it). The book page lists the device's files first in its
  order, then "Other formats: Kindle may not open them." and those in the book's own order in `ul.files.dim` (gray
  text, a lighter button, still a Download); above the files "Formats ordered for Kindle." (or "not ordered") and
  **Change**. A list's row shows the book's formats, the device's first and bold, the others `.dim`. No device: one
  list, nothing dimmed.
- **The chooser** `/e/device?return=<path>` (`library.browse`): radio buttons, Automatic naming the guess, each family
  with its formats, Another device; posts with its form token to `/e/device`, keeps the choice (Automatic deletes the
  cookie) and goes back (`SafeReturn`); a family not known is 400.

## Built (stage 8 chunk 8): keys in the address

- `/e/k/<key>/…` opens the pages with a key allowed in the address, no cookie needed; every link, cover, download and
  form keeps the prefix; no sign-in, sign-out or Sign out button there; a refused key is a plain "Key not accepted"
  page (401) linking to `/e/`. How it works: [opds-auth](opds-auth.md#built-stage-8-chunk-8-keys-in-the-e-readers-address).

## Built (stage 8 chunk 9): ways in

- **The redirect** (`EReaderRedirect.TargetAsync`, asked by `ClientPage.Serve` first): a GET or HEAD of the app's page
  from a browser `EReaderDevices.Guess` names (Kindle without Silk, Kobo, PocketBook, Tolino, Boox) is a 302 to `/e/`;
  the app's book address `/books/<id>` to `/e/books/<id>`, which finds the book's library under the account's
  visibility and goes on to `/e/l/<library>/books/<id>` (unknown or hidden: 404; a visitor signs in first). Not
  redirected: the `librarion.fullsite` cookie present, the server waiting for its setup (the app's setup page),
  `ereader.enabled` off (`EReaderSwitch`, the tenant of the cookie session, else the server's value), anything but GET
  / HEAD. The device chosen on `/e/device` doesn't count (its cookie is `/e`'s): only the User-Agent.
- **Full site** (`/e/full-site`, a muted link in every page's `footer.foot`, under a key's prefix too): sets
  `librarion.fullsite` (path the app's root, a year, HttpOnly, Lax), then 302 to the app's root. **The way back**
  (`/e/start`, the app's sign-in page's "On an e-ink reader? Use the e-reader version", a plain `href`, not the router)
  deletes it if present and goes to `/e/`. Both GET: they set only this browser's preference, nothing on the server.
  Both `AllowAnonymous`; with the mode off they are 404 as every `/e` address.
- Nothing in the app's menu (D-8.11). The sign-in page's link shows even when the mode is off (no setting is known
  before signing in): it then leads to `/e`'s Not found.
- Tests: `EReaderWaysInTests` (per User-Agent redirected or not, a tag's readable address, a book's address; the API,
  OPDS, files, covers, `/e` itself and POSTs never; the cookie, its attributes and the way back; setup and both
  switches; `/e/books/<id>`), the client spec for the link.

## Built (stage 8 chunk 10): end-to-end

- `tests/e2e/specs/ereader.spec.ts` in the Playwright project **`ereader`** (600 × 800, a Kindle's User-Agent,
  `javaScriptEnabled: false`, light, touch; only `@ereader` specs, which no other project runs). With script off: the
  app's address sent to `/e/signin`, Stay signed in ticked, sign in, the libraries, a library, search, a book whose FB2
  is inside a ZIP (ordered for Kindle, the FB2 under Other formats), Download (the same bytes) and Download zipped
  (`PK`), All books by title 20 + 6 with Next and First, a loose download, Sign out (no session cookie left), then a
  key in the address alone down to a download. On each screen: no `<script>`, every computed colour gray, fits,
  nothing cut, targets ≥ 44 px. A second test runs axe AA on each screen with script on (axe needs it; the pages have
  none). The guard fails on any request to another origin.
- Found by it: the pages declared no icon, so browsers asked for `/favicon.ico` (404 on every first page): `EPage`
  links the app's `favicon.svg` (`EReaderLinks.Icon`, the root's, never under a key).
- Playwright's `evaluate` and locators work with `javaScriptEnabled: false` (its own world); a download is a link's
  `tap()` with `waitForEvent('download')` before it.

## Built (stage 8 chunk 11): measured at 2M

- `ereader` benchmark command (`tests/Librarion.Benchmarks/Bench/EReaderCommand.cs`,
  [benchmark README](../../tools/benchmark/README.md#what-ereader-measures)): the real server on the 2M catalog, asked
  as a Kindle's browser signed in through the form; every page within 100 ms p95 on both providers (a search 108 on
  PostgreSQL, within the search budget 300), no full scan (`run`); numbers in
  [scale](../development/scale.md#measured). Nothing needed changing.

## Scale and privacy

- Same keyset queries and page budgets as the app's lists, smaller pages; nothing new per request. Nothing
  external: no fonts, scripts or images from elsewhere ([privacy](../development/privacy.md)).

## Tests

- Backend: every `/e/` route names a right (`EveryRouteNamesARight`); no `<script>`, handler or `javascript:` in any
  page (`EReaderTests.PlainPageAsync`); the stylesheet has no hue (`TheStylesheetHasNoColour`); Next / First by key
  without repeats, hidden books absent or 404 (`EReaderListTests`); download headers (ASCII + UTF-8 names, ranges);
  device order per User-Agent (`EReaderListTests.Devices`); the redirect and Full site (`EReaderWaysInTests`); keys in
  the address and their redaction (`AddressKeyTests.EReader`); form posts without the token refused.
- e2e: `ereader.spec.ts` (chunk 10, above). Benchmark: the `ereader` command (chunk 11).

## Checked by hand (the stage's exit criterion: the user, before the merge)

Following [docs/user-guide/ereader.md](../../../docs/user-guide/ereader.md), on each of a **Kindle**, a **Kobo** and a
**PocketBook** (Tolino and Boox if at hand), over the home WLAN or, as the Kobo's browser wouldn't open a LAN IP
address, on a test server with a domain (the `:edge` image behind Traefik, [docker](../environment/docker.md)):

1. The app's address in the device's browser: sent to `/e/` by itself (the sign-in page); **Keep me signed in** ticked.
2. Sign in; the page is readable, buttons easy to hit, no colour smear; sleep the device and wake it: still signed in.
3. A library's page → **All books by title** → **Next page** twice → **First page**: no book twice, covers shown.
4. Search a word, then `author:"…"`; a blank search shows How to search.
5. **Authors** → a letter → an author → a book: fields as links, files, description.
6. "Formats ordered for <the device>" names it; its formats first; **Change** → another device → back to Automatic.
7. **Download** a book in the device's format (Kindle AZW3 or MOBI, Kobo EPUB or KEPUB, PocketBook FB2 and **FB2,
   zipped**), one from a ZIP library too: it opens in the device's library.
8. A key in the address (API keys page, **Can be used in the address**): opens without signing in, a download works.
9. **Full site** → the app (heavy, but no redirect back); the app's sign-in page's e-reader link → `/e/` again.
10. **Sign out**.

Also stage 7's OPDS checks in **KOReader** and **Thorium** ([opds](opds.md#checked-by-hand-the-stages-exit-criterion-the-user-before-the-merge)).
Note what each device names differently from the guide, and anything shown wrongly.
