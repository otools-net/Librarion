# E-reader mode: a plain gray site for e-ink devices (planned)

Code: `src/Librarion.Api/EReader/` (`EReaderEndpoints`, `EReaderGuard`, `EReaderSwitch`, `EReaderLinks`,
`EReaderFrame`, `ServerTexts`, `EReaderStylesheet` + `ereader.css`, `Pages/*.razor`), the same `Core/Catalog/` and `Core/Search/`
services as the app; tests `EReaderTests`. Decision D23 in [foundation-plan](../development/foundation-plan.md); being
built in stage 8 ([stage-8-ereader-plan](../development/stage-8-ereader-plan.md)).

A second, **basic face of the library for the browsers of e-ink readers** (Kindle, Kobo, PocketBook, Boox, Tolino):
find a book, look at it, download it to the device. Not the in-app reader ([reader](reader.md)): nothing is read
here, the device's own reader opens the downloaded file.

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
- **Device formats:** the formats the device opens are listed first and the rest dimmed (Kindle: AZW3, MOBI, PDF,
  TXT; Kobo: EPUB, KEPUB, PDF, CBZ; PocketBook: FB2, EPUB, PDF, DJVU…), guessed from the User-Agent, changeable on the
  device (kept per browser). A book without a fitting format offers it converted (an FB2 as AZW3 for a Kindle,
  [conversion](conversion.md)); a conversion still running answers with a "being prepared, try again" page.
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

## Scale and privacy

- Same keyset queries and page budgets as the app's lists, smaller pages; nothing new per request. Nothing
  external: no fonts, scripts or images from elsewhere ([privacy](../development/privacy.md)).

## Tests to have

- Backend: every `/e/` route names a right; pages render without any `<script>`; Next / First follow the keyset
  across pages; hidden books 404; download headers (ASCII + UTF-8 names, type, ranges); device format order per
  User-Agent; the redirect of e-ink browsers (and its Full site cookie); path key works and is redacted; the
  stylesheet has no non-gray colour.
- e2e (important flow): with JavaScript disabled on a 600 × 800 viewport: sign in, search, open a book, download a
  file (also from a container zip), next page of a list; no request leaves the origin.
