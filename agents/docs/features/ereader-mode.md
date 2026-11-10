# E-reader mode: a plain gray site for e-ink devices (planned)

Code (planned): `src/Librarion.Api/EReader/` (`EReaderEndpoints`, page templates, `ereader.css`), the same
`Core/Catalog/` and `Core/Search/` services as the app. Decision D23 in
[foundation-plan](../development/foundation-plan.md).

A second, **basic face of the library for the browsers of e-ink readers** (Kindle, Kobo, PocketBook, Boox, Tolino):
find a book, look at it, download it to the device. Not the in-app reader ([reader](reader.md)): nothing is read
here, the device's own reader opens the downloaded file.

## Why a separate face

- The browsers of e-ink devices are old or limited WebKit/Chromium builds: slow JS, little memory, partial CSS; the
  Angular app is too heavy for them, and its scrolling, transitions and colours smear on e-ink.
- So the mode is **server-rendered HTML that works with JS off**: links and plain forms only, one small stylesheet,
  no client framework, no script at all (`script-src 'none'` in its CSP). Rendering (Razor components rendered
  statically or plain templates) is chosen when the stage starts.

## Design

- **Grayscale only:** black text on white, grays for borders and secondary text; no colour anywhere (a test checks
  the stylesheet), no dark theme, no shadows, gradients, animations, transitions or hover effects.
- **Big and plain:** system fonts, large text, tap targets ≥ 44 px (e-ink touch is imprecise), generous line height;
  fits 600 × 800 and up, portrait first; nothing needs a long press or a swipe.
- **Pages, not scrolling:** lists of ~10–20 items with **Previous / Next** links (keyset cursors in the query string,
  [scale](../development/scale.md)), so each screen is one full refresh; no infinite scroll, no virtual lists.
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

- Address `/e/` (short, to type on an e-ink keyboard), linked from the app's menu and sign-in page; the app may offer
  it once when the User-Agent is an e-ink device.
- Cookie session like the app, long-lived (typing a password on an e-ink keyboard is slow); forms carry the
  antiforgery token. Devices whose browser loses cookies can use a path API key (`/e/k/<key>/…`, opt-in per key,
  redacted from logs), as [opds-auth](opds-auth.md).
- Rights: the existing `library.browse` and `library.download`, content filters (`Visible()`) and tenants as everywhere
  ([rights](rights.md)); a tenant setting can turn the mode off.
- Texts through the same translation keys (`en.json` read by the server), the UI language as the user's.

## Scale and privacy

- Same keyset queries and page budgets as the app's lists, smaller pages; nothing new per request. Nothing
  external: no fonts, scripts or images from elsewhere ([privacy](../development/privacy.md)).

## Tests to have

- Backend: every `/e/` route names a right; pages render without any `<script>`; Previous / Next follow the keyset
  across pages; hidden books 404; download headers (ASCII + UTF-8 names, type, ranges); device format order per
  User-Agent; path key works and is redacted; the stylesheet has no non-gray colour.
- e2e (important flow): with JavaScript disabled on a 600 × 800 viewport: sign in, search, open a book, download a
  file (also from a container zip), next page of a list; no request leaves the origin.
