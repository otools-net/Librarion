# E-reader mode: a plain gray site for e-ink devices (planned)

Code: `src/Librarion.Api/EReader/` (`EReaderEndpoints`, `EReaderGuard`, `EReaderSwitch`, `EReaderLinks`,
`ServerTexts`, `EReaderStylesheet` + `ereader.css`, `Pages/*.razor`), the same `Core/Catalog/` and `Core/Search/`
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

- **Grayscale only:** black text on white, grays for borders and secondary text; no colour anywhere (a test checks
  the stylesheet), no dark theme, no shadows, gradients, animations, transitions or hover effects.
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
  immutable, ETag; `TheStylesheetHasNoColour` allows hex grays only and no shadow, gradient, transition, animation,
  hover, filter, `url(`, `@import`, dark theme. Fonts: `serif` (the device's own).
- **Errors as pages:** `EReaderGuard` (see [sessions-and-pipeline](../architecture/sessions-and-pipeline.md)): setup
  503, switch 404, and any bodiless 400/403/404/405 rendered as a plain page; a visitor without a session is sent to
  `/e/signin?return=…` by the cookie's `OnRedirectToLogin` (the sign-in page itself is chunk 2).
- **Switch:** `ereader.enabled` (tenant, on), read by `EReaderSwitch` (cached per tenant until `AccessChanges` moves).
- The home page lists the libraries the account may browse (a placeholder until chunk 3); `library.browse` needed.

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
