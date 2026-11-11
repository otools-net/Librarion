# Stage 8: e-reader mode (plan)

Roadmap: [foundation-plan](foundation-plan.md) (D23; D7 for path keys). Goal: **find a book and download it from an
e-ink device's browser** (Kindle, Kobo, PocketBook, Boox, Tolino): a second, plain face of the library at `/e/`,
server-rendered, grayscale, no script. Version stays on `edge`; the merge into `main` makes it `0.9.0`.

Written 2026-11-11, at the close of stage 7. Decisions D-8.1–D-8.10 below are **proposed**: chunk 0 asks the
questions (Q1–Q7) and records the answers.

Feature notes this stage builds (their "planned" parts become built): [ereader-mode](../features/ereader-mode.md)
(the design: screens, grayscale, pages, downloads, access), [opds-auth](../features/opds-auth.md) (path keys on `/e`
too), [rights](../features/rights.md), [settings](../features/settings.md) (the tenant switch),
[sessions-and-pipeline](../architecture/sessions-and-pipeline.md) (a second CSP, form posts). Format conversion is
stage 9: here devices get the formats the library has, ordered for the device; "converted for this device" comes
with stage 9 ([conversion](../features/conversion.md)). Work left for after 1.0 goes to
[next-version-plan](next-version-plan.md).

Reference: calibre-web has no such mode (its pages need script); SimpleFileExplorer has none either. The design
is the note's; nothing to port.

## How to use this plan (every session)

- Read this plan, take the chunk on the **Next:** line (the first unticked one), read the notes and code it names,
  build it with its tests, commit it on `edge`, tick it here (`[x]` and a short **Built:** with links to the notes),
  move **Next:** on. Then stop, or ask the user before starting the next chunk.
- A session may end early: this list is the state of the work. A chunk too big for one session is split here
  (`5a`, `5b`…) before it starts; the parts are ticked one by one.
- Questions are asked when the chunk that needs them starts; the answers go in "The user's answers" with their date
  and the decisions are updated.
- Anything decided to wait past 1.0 goes to [next-version-plan](next-version-plan.md), not a bullet left here.

**Next:** chunk 0 (after stage 7 is merged).

## Questions for the user (chunk 0)

- **Q1 Rendering:** Razor components rendered statically on the server (`RazorComponentResult`: typed templates,
  HTML-encoded by default, layouts; part of ASP.NET Core, no Blazor runtime sent) **(recommended)**, or plain C#
  templates written by hand (no Razor in the project, but every value encoded by hand: easy to get wrong)?
- **Q2 Previous page:** lists page by key forward only. **Next** and **First** links plus the browser's Back
  **(recommended: no new queries)**, or a real **Previous** (every sort also walked backwards: about twice the list
  queries to write and test)?
- **Q3 Keys in the address on `/e/k/<key>/…`:** the same scope as reader apps (`opds`, renamed "Reader apps and
  e-readers") with the key's **Can be used in the address** **(recommended)**, or a scope of its own?
- **Q4 Reaching it:** a link on the app's sign-in page and in its menu, and on an e-ink browser (by its User-Agent) a
  one-time note on the sign-in page offering `/e/` **(recommended)**, or an automatic redirect of e-ink browsers?
- **Q5 Formats the device can't open** (until stage 9 converts): listed after the others, dimmed, still downloadable
  **(recommended)**, or hidden?
- **Q6 Session:** the sign-in form's **Stay signed in** ticked by default on `/e/` (typing on an e-ink keyboard is
  slow; the app's own remembered-session length) **(recommended)**, or as on the app (unticked)?
- **Q7 Page size:** 20 books a page with small covers **(recommended)**, or 10 (faster refresh on the slowest
  devices) — or the account's page size preference?

## Decisions for this stage (proposed; chunk 0 confirms or changes them)

- **D-8.1 Rendering (Q1):** Razor components rendered statically by minimal API endpoints
  (`RazorComponentResult`), one layout, no interactivity, no `blazor.web.js`; markup in
  `src/Librarion.Api/EReader/`. Nothing of the Angular client is used.
- **D-8.2 Addresses:** `/e/` the site; `/e/k/<key>/…` with a key in the address (D-8.7). `e` excluded from
  `ClientPage`'s `{key}/{id}/{name}` route (as `opds`, `api`), a `/e` 404 page of its own (HTML, not the client),
  `RequestGuard`'s setup 503 answered as a plain HTML page there.
- **D-8.3 Security headers:** a second CSP for `/e`: `default-src 'none'; img-src 'self'; style-src 'self';
  form-action 'self'; frame-ancestors 'none'; base-uri 'none'` (no script at all); the app's CSP unchanged.
- **D-8.4 Session and forms:** the same cookie session as the app (one sign-in for both), signed in through the
  same `SignInService` (throttle, lockout, security events). Plain HTML forms can't send `X-Librarion-Request`:
  `/e` form posts carry ASP.NET Core's antiforgery token instead (`AddAntiforgery`, checked on every `/e` POST);
  GET never changes anything. Stay signed in per Q6.
- **D-8.5 Rights and switch:** the existing `library.browse` and `library.download` (no new right), content filters
  and tenants as everywhere (`BookVisibility`); a tenant setting `ereader.enabled` (on by default) turns `/e` off
  (404). Every `/e` route names its right (`EveryRouteNamesARight`).
- **D-8.6 Pages:** lists by key, `PageSize` per Q7, **Next** / **First** per Q2 (cursor in the query string, as the
  feeds); the same Core queries as the app and OPDS (`BookQueries`, `TagQueries`, `InitialsAsync`, search); one full
  page per screen, no scrolling list, no infinite scroll.
- **D-8.7 Path keys (Q3):** `/e/k/<key>/…` for browsers that lose cookies, as `/opds/k/` (`OpdsPathKey` made
  general: one middleware for both prefixes), every link keeping the prefix, covers and downloads through it, the key
  redacted from logs (`LogRedaction` covers the new prefix), the tenant setting `opds.pathKeys` applies.
- **D-8.8 Downloads and device formats:** the app's download route (`BookDownloads`: Range, ETag, loose files and
  ZIP entries), FB2 also as `fb2.zip` (`ZippedDownload`); `Content-Disposition` with an ASCII `filename` and a UTF-8
  `filename*` (old browsers mangle UTF-8 names; checked, fixed if the app's isn't). The device's formats first, guessed
  from the User-Agent (Kindle: AZW3, MOBI, PDF, TXT; Kobo: EPUB, KEPUB, PDF, CBZ; PocketBook and others: FB2, EPUB,
  PDF, DJVU…), changeable on the page (a form, kept in a cookie); others per Q5.
- **D-8.9 Look:** grayscale only (a test reads the stylesheet: no hue), system fonts, large text, targets ≥ 44 px,
  600 × 800 portrait first, every image with its width and height, covers as the `s` thumbnails; no dark theme,
  shadows, gradients, transitions or hover effects.
- **D-8.10 Texts:** the same `en.json` keys read by the server (`ServerTexts`, the user's UI language, English
  shipped), `ClientI18nTests` extended to the server's keys; fields named as the app names them.

## Todo (one chunk per session; commit it, then ask before the next)

0. [ ] The user answers Q1–Q7; D-8.1–D-8.10 updated and marked confirmed.
1. [ ] The `/e` frame: route group and fallback, `ClientPage` exclusion, setup page (503 as HTML), the `/e` CSP
   (D-8.3), the rendering (D-8.1: layout, `ereader.css`), `ServerTexts` (D-8.10), the tenant setting
   `ereader.enabled` (D-8.5); a placeholder home behind `library.browse`. Tests: every route names a right, no
   `<script>` in any page, the stylesheet has no colour, headers (CSP), the switch, 404 page.
2. [ ] Sign in and out (D-8.4): plain form, antiforgery, `SignInService` (throttle, lockout, events), Stay signed in,
   return address, sign out by POST; the same cookie as the app. Tests: right and wrong password, lockout, the
   token refused when missing, a disabled account.
3. [ ] Home and the book list: search box, recently added, links to browse; one list component (small cover,
   title with the file-name fallback, authors, series and number), Next / First by key (D-8.6). Tests: the keys
   walk without repeats, hidden books absent.
4. [ ] Search: the box and its results in the search language, paged by key; a blank search shows nothing; the
   language's errors shown plainly. Tests on both providers.
5. [ ] Browse: the fields that hold values, a field's first letters (`InitialsAsync`) and values A–Z, a value's books
   (a series by number), plain tags; content filters as on the app. Tests.
6. [ ] The book page: cover, title, fields as links, plain tags, description as plain text (sanitized, cut), files with
   format, size and Download, FB2 also `fb2.zip`; a merged book's address goes to its book; hidden 404. Downloads:
   `Content-Disposition` ASCII + UTF-8 (D-8.8). Tests.
7. [ ] Device formats (D-8.8, Q5): User-Agent guess (Kindle, Kobo, PocketBook, Tolino, Boox, else none), the order and
   dimming on the book page and in lists, the chooser form and its cookie. Tests per User-Agent.
8. [ ] Keys in the address (D-8.7, Q3): `/e/k/<key>/…`, the middleware shared with `/opds/k/`, links, covers and
   downloads keeping the prefix, redaction (a test reads the log), the tenant switch; the API key page's "How to use
   it" names the `/e` address. Tests.
9. [ ] Ways in (Q4): links on the app's sign-in page and menu, the one-time note for e-ink browsers; client specs.
10. [ ] e2e: a project with JavaScript off at 600 × 800 (and a Kindle-like User-Agent): sign in, search, a book page,
    download loose and from a ZIP, the next page, a key in the address, sign out; no request leaves the origin; axe AA
    on each screen (run with script on: the pages have none of their own).
11. [ ] Benchmark at 2M on both providers: `/e` pages over HTTP (home, a list's deep page, search, a field's letters
    and values, a book page) within the list budget (100 ms p95), no full scan (the `opds` command's way: the real
    server).
12. [ ] Docs and close: `docs/user-guide/ereader.md` (Kindle, Kobo, PocketBook), the API keys page, rights and
    settings references; notes (`ereader-mode`, `opds-auth`, `rights`, `settings`, `sessions-and-pipeline`),
    CHANGELOG; the by-hand list for the user (Kindle, Kobo and PocketBook browsers); merge `0.9.0`, this plan
    removed, the roadmap marks stage 8 done.

## Exit criteria

From the roadmap: with JavaScript off on 600 × 800: sign in, search, book page, download (also from a ZIP); no
`<script>` and no colour; Kindle, Kobo and PocketBook browsers checked by hand (by the user). Also: every `/e` route
names its right; form posts need the antiforgery token; keys never appear in logs; both providers green; the pages
within the list budget at 2M.
