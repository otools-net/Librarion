# Stage 7: OPDS and API keys (plan)

Roadmap: [foundation-plan](foundation-plan.md) (D6, D7, D15, D22). Goal: **reader apps connect with a password or an
API key**, browse, search and download from catalogs of 2M books within the OPDS budget; users keep **personal
shelves**, also as feeds. Version stays on `edge`; the merge into `main` makes it `0.8.0`.

Written 2026-11-10. Decisions D-7.1–D-7.10 confirmed by the user's answers (2026-11-10), below.

Feature notes this stage builds (their "planned" parts become built): [opds-auth](../features/opds-auth.md),
[rights](../features/rights.md), [accounts](../features/accounts.md), [book-display](../features/book-display.md)
(OPDS image links), [merging](../features/merging.md) (shelves follow merges); new notes `features/opds.md` (feeds)
and `features/shelves.md`. Work left for after 1.0 goes to [next-version-plan](next-version-plan.md).

Reference: calibre-web (`C:\workspace\calibre-web`, `cps/opds.py`, `templates/feed.xml`, `cps/shelf.py`: behaviour
only, its GPL-3 code is never copied). SimpleFileExplorer has no API keys or Basic; its `Sfe.Api/Security/FileLinks.cs`
(a token checked again against the account's stamp and rights on each request) is the model for path keys.

## How to use this plan (every session)

- Read this plan, take the chunk on the **Next:** line (the first unticked one), read the notes and code it names,
  build it with its tests, commit it on `edge`, tick it here (`[x]` and a short **Built:** with links to the notes),
  move **Next:** on. Then stop, or ask the user before starting the next chunk.
- A session may end early: this list is the state of the work. A chunk too big for one session is split here
  (`5a`, `5b`…) before it starts; the parts are ticked one by one.
- Questions are asked when the chunk that needs them starts; the answers go in "The user's answers" with their date
  and the decisions are updated.
- Anything decided to wait past 1.0 goes to [next-version-plan](next-version-plan.md), not a bullet left here.

**Next:** chunk 12.

## The user's answers (2026-11-10)

- **Q1 Shelves:** built in this stage: personal shelves per user, Add to shelf, a Shelves page, a feed per shelf.
- **Q2 Anonymous access:** no guests before 1.0 (every feed needs a password or a key); guests are an open topic in
  the [next-version plan](next-version-plan.md).
- **Q3 OPDS 2.0:** the last chunk, optional; skipped if the stage runs long.
- **Q4 Rights:** a right for OPDS and one for API keys, both given to Users (so also Curators and Administrators).
- **Q5 Covers (D-7.8): JPEG everywhere**, not only for OPDS: thumbnails, generated covers and uploads are encoded and
  served as JPEG in the web app too (chunk 4a).
- **Q6 Covers uploaded before:** not in production, ignored: no conversion of the `.webp` uploads already stored
  (they may break; uploaded again) and the old `.webp` thumbnails are left in the cache folder.
- **Q7 Keys on `/api` (D-7.2):** yes, scoped (`Bearer` / `X-Api-Key`); Basic only on `/opds`.
- **Q8 Key expiry (D-7.3):** an optional date, none by default.
- **Q9 Path keys (D-7.5):** opt-in per key ("Can be used in the address"); a tenant setting can switch them off.
- **Q10 Shelf order (D-7.9):** the shelf's own order (drag; also the feed's order), and its page sortable by title,
  author or date added.
- **Q11 OPDS root (D-7.5):** a list of libraries (each its own catalog), the catalog itself when there is one; no
  catalog across libraries.
- **Q12:** D-7.1, D-7.4, D-7.6, D-7.7 and D-7.10 confirmed as written.

## Decisions for this stage (confirmed 2026-11-10)

- **D-7.1 Rights:** `library.opds` (Read, area `library`: feeds; downloads by OPDS also need `library.download`,
  books `library.browse`, in that library), `account.apiKeys` (Write: one's own keys), `account.shelves` (Write: one's
  own shelves); all three to Users (Q4). A tenant administrator (`tenant.users`) lists and revokes a user's keys.
- **D-7.2 Ways in, by path:** `/opds/**` accepts Basic (password or key) and keys only, **never the cookie**, GET only.
  `/api/**` keeps the cookie and adds `Authorization: Bearer <key>` / `X-Api-Key` (never Basic: browsers replay Basic
  as they do cookies, a CSRF surface); a key-signed request needs no `X-Librarion-Request` header. Scopes narrow
  `EffectiveAccess`, never widen it: `opds` = `/opds` only, `api:read` = GET routes, `api:write` = all; an optional
  subset of libraries.
- **D-7.3 Keys:** `lib_<id>_<secret>` (160+ bits of secret), stored as the id + SHA-256 of the secret, compared in
  fixed time (`PasswordHashing.SameSecret`), shown once. Per key: name, scopes, libraries, path allowed, expiry,
  created, last used + address (written at most once a minute per key), revoked. Expiry optional, none by default
  (Q8). A new security stamp or a disabled
  account ends them at once. `ApiKeys` has the tenant query filter (and a row in `NoQueryCrossesTenants`), migrations
  for both providers.
- **D-7.4 Basic cost:** a successful password check cached in memory 5 minutes, keyed by the user, a keyed hash of the
  secret and the security stamp; a failure never cached. Failures go through `SignInThrottle` and the account
  lockout as on the web sign-in; a rate limit per user on feeds and downloads. Security events `ApiKeyCreated`,
  `ApiKeyRevoked`, failed OPDS sign-ins (`SignInFailed` / `LockedOut` with the detail `opds`; never a secret).
- **D-7.5 Addresses:** `/opds` = the root (one library: its catalog; several: a list of them);
  `/opds/l/<library>/…` per library (ids are global: the tenant follows, also for server administrators);
  `/opds/k/<key>/…` path keys for keys that allow it (opt-in per key; a tenant setting switches them off), every link of the feed keeping the prefix, the key redacted
  from logs. `opds` excluded from `ClientPage`'s `{key}/{id}/{name}` route and an `/opds` 404 fallback as `/api` has;
  `RequestGuard`'s setup 503 covers `/opds`.
- **D-7.6 Feeds (OPDS 1.2):** root, Recent (`BookSort.Added`), All by title, each field (authors, series, categories as
  a tree, languages, publishers, custom fields) A–Z by letter (`TagQueries.ValuesAsync` `from`), then values, then a
  value's books (series by number); plain tags by category; shelves; OpenSearch. `next` links carry the keyset cursor
  (`KeysetCursor`), never `OFFSET` or total counts; 50 entries a page; a page's files and descriptions read in one
  batched query each (never one per book).
- **D-7.7 Entries:** id `urn:librarion:book:<id>`, title (file-name fallback, D15), authors, series and number,
  language, categories, summary (cut), `updated`; an acquisition link per file with its media type
  (`BookFormats.ContentType`); FB2 also as `fb2.zip`, zipped on the fly (streamed, no Range); merged ids redirect.
- **D-7.8 Covers as JPEG everywhere (Q5):** covers are WebP today, which many reader apps (older Moon+, PocketBook,
  Kobo) don't decode; every cover becomes JPEG: `CoverImages` encodes JPEG, `CoverCache` names `.jpg`, uploads are
  stored as `.jpg`, generated covers too, the routes answer `image/jpeg`. OPDS image links: `l` as the image, `m` as
  the thumbnail. Nothing converted up front (Q6).
- **D-7.9 Shelves:** personal (per user, in the user's tenant), a name, books in their own order (position, added;
  Q10), the shelf's page also sortable by title, author or date added; a book
  shows in a shelf only while the user may see it (`BookVisibility`); merges move shelf rows and their undo moves them
  back. Shared / public shelves: [next-version plan](next-version-plan.md).
- **D-7.10 Validation:** feeds checked against the OPDS 1.2 / Atom RelaxNG schemas by an offline tool in
  `agents/tools/opds-validate/` (which validator: found in chunk 13; nothing downloaded at run time).

## Todo (one chunk per session; commit it, then ask before the next)

0. [x] The user confirms D-7.1–D-7.10 (or changes them); this plan updated. **Built:** answers Q5–Q12
   (2026-11-10): JPEG everywhere (chunk 4a added), keys on `/api` scoped, no default expiry, path keys opt-in, shelves
   in their own order, the root a list of libraries.
1. [x] Rights and the `/opds` frame: `library.opds`, `account.apiKeys`, `account.shelves` (`RightCatalog`, default
   groups, texts, `core/rights.ts`, `docs/reference/rights.md`); the `/opds` route group, its 404 fallback, the
   `ClientPage` exclusion, the setup 503; `EveryRouteNamesARight` (`RouteRightsTests`) and
   `TheServerMakesNoOutboundRequests` (`PrivacyTests`) cover `/opds`; a placeholder root feed behind the right.
   **Built:** the `Opds` scheme named by the group (the cookie never the user there), [opds](../features/opds.md),
   [opds-auth](../features/opds-auth.md), [sessions-and-pipeline](../architecture/sessions-and-pipeline.md); the dev
   proxy forwards `/opds`.
2. [x] Basic with a password: an authentication handler for `/opds` setting the cookie's claims (user id, stamp), so
   `CurrentAccess` works unchanged; the verification cache (D-7.4) through `SignInService`; throttle, lockout, 401
   with `WWW-Authenticate: Basic realm="Librarion"`, rate limit per user, security events. Tests: right and wrong
   password, locked and disabled account, cache dropped on a password change, the cookie refused on `/opds`.
   **Built:** `BasicVerificationCache`, the `opds` rate limit (`OpdsRequestsPerMinute`, 600);
   [opds-auth](../features/opds-auth.md#built-stage-7-chunk-2-basic-with-the-password).
3. [x] API keys in Core: `ApiKeys` (both migrations, tenant filter), create, list, revoke, expire; scopes and
   libraries narrowing `EffectiveAccess`; a key as the Basic secret on `/opds`, `Bearer` and `X-Api-Key` on `/api`
   (D-7.2), last used, security events; routes `api/account/api-keys` (own, `account.apiKeys`) and a user's keys for
   `tenant.users`. Tests on both providers (scopes narrow, revoked / expired / new stamp → 401).
   **Built:** a policy scheme choosing key or cookie, narrowing in `CurrentAccess`, keys can't manage keys, the
   throttle per key id; [opds-auth](../features/opds-auth.md#built-stage-7-chunk-3-api-keys).
4. [x] API keys UI: My account › API keys (create: name, scopes, libraries, path key, expiry; the key shown once with
   Copy; list with last used; revoke, asking first); Administration › Users: a user's keys, revoke; phones; client
   specs, e2e on every device, axe in both themes. **Built:** one `ApiKeys` component, own and an account's;
   [opds-auth](../features/opds-auth.md#built-stage-7-chunk-4-the-pages), user docs (account, administration › accounts).
4a. [x] Covers as JPEG everywhere (D-7.8, Q5–Q6): `CoverImages` (thumbnails, `Remake`), `GeneratedCovers`,
    `CoverCache` (`.jpg`), `CoverUploads` (`.jpg`), the cover routes in `BookEndpoints` / `CoverEndpoints`
    (`image/jpeg`, OpenAPI summaries); quality chosen against size (measured on sample covers); tests (a JPEG comes
    back at every size, hostile images still refused); notes [book-display](../features/book-display.md), user docs
    if they name WebP. **Built:** quality 75 for the cache (85 for uploads), baseline, transparency on white, ETags
    ending in `.jpg`; [book-display](../features/book-display.md).
5. [x] Feed writer and first feeds: a streaming OPDS / Atom writer (`XmlWriter`), navigation and acquisition feeds,
   links (start, up, self, next, search), book entries (D-7.7) with batched files; root and library list, Recent,
   All by title; acquisition through `BookDownloads` (Range, ETag), the right per library; cover links (D-7.8).
   **Built:** `OpdsFeed` (buffered per page, not streamed: a page is small), `OpdsLinks`, `BookQueries.EntriesAsync`,
   a server administrator's tenant from the library's address; [opds](../features/opds.md#built-stage-7-chunk-5-the-first-feeds).
6. [x] Fields and tags: each field A–Z → values → books (series by number), categories as a tree, plain tags by
   category; values hidden by content filters left out (`TagQueries`' deny rule), counts from `TagCounts`.
   **Built:** first letters probed (any collation) with All, a tag with tags under it as a tree, merged tags
   redirected; [opds](../features/opds.md#built-stage-7-chunk-6-fields-and-tags).
7. [x] OpenSearch: the description document, the search feed through `BookQueries.ListAsync` (`q`, the search
   language), paged by key; the search link in every feed. **Built:** per library (`…/opensearch.xml`, `…/search`),
   both the description and the template linked, a blank search empty;
   [opds](../features/opds.md#built-stage-7-chunk-7-search).
8a. [x] FB2 as `fb2.zip` on the fly (an acquisition link beside the FB2's, zipped while sent: no Range, no length);
    merged books' old download and cover addresses still answer through `/opds` (tests). **Built:** `ZippedDownload`
    (buffered per chunk: `ZipArchive` writes synchronously), `application/fb2+zip`; merged addresses answered, not
    redirected; [opds](../features/opds.md#built-stage-7-chunk-8a-fb2zip-merged-books).
8b. [x] Path keys `/opds/k/<key>/…` (keys that allow them, the tenant setting) with every link keeping the prefix and
   the key redacted from logs (a test reads the log).
   **The key page tells how to use a key without the user name** (the user's request, 2026-11-10): in the panel
   showing a new key once, per scope: reader apps: the catalog address with the key in it (`…/opds/k/<key>`, with
   Copy) when the key allows the address, else the address with the user name and the key as the password; scripts:
   `Authorization: Bearer <key>` / `X-Api-Key: <key>`; user docs (account › API keys) and the client spec / e2e
   updated. Basic stays as built (the password, or a key with its owner's name); an empty name is refused (the
   user's answer, 2026-11-10: a key without the name goes in the address or the headers).
   **Built:** `OpdsPathKey` before an explicit `UseRouting`, `ApiKeys.VerifyAsync(inPath)`, the tenant setting
   `opds.pathKeys`, the logger factory redacting keys (`LogRedaction`; the hosting layer logs the address before any
   middleware), "How to use it" on the key page; [opds-auth](../features/opds-auth.md#built-stage-7-chunk-8b-keys-in-the-address).
9. [x] Shelves in Core: `Shelves`, `ShelfBooks` (both migrations, tenant filter, `NoQueryCrossesTenants`), create,
   rename, delete, add and remove books (one, a selection), order; lists by key under visibility; merges, undo and
   split keep shelf rows right; routes behind `account.shelves`. Tests on both providers. **Built:** caps: shelves per account the
   setting `shelves.maxPerAccount` (100, the user's answer 2026-11-10), 10,000 books a shelf, spaced positions (`Gap` 1,024, renumbered when neighbours touch), stored counts, `BookMergeData.Shelves`;
   the test helper `Shelf` renamed `TestLibrary`; [shelves](../features/shelves.md).
10. [x] Shelves UI: Add to shelf on the book page and in the selection bar, Shelves in the sidebar, a shelf's page
    (the book list as the library's, reorder, remove, rename, delete); phones; client specs, e2e on every device,
    axe in both themes. **Built:** `ShelfStore`, `ShelfPicker`, `BookList` with `shelf`; reorder by drag (pointer
    events), keys and Move; `POST …/books/move` for a selection, the shelf order's cursor following its book;
    selection with `account.shelves` alone; user docs `user-guide/shelves.md`; [shelves](../features/shelves.md#built-stage-7-chunk-10-the-pages).
11. [x] Shelves in OPDS: the shelf list and each shelf's feed (in the shelf's order). **Built:** per library (Q11:
    each library's books of a shelf), My shelves only when the account has one, `account.shelves` on both routes;
    [opds](../features/opds.md#built-stage-7-chunk-11-shelves).
12. [ ] HTTP-level e2e as a reader app (Playwright request context): Basic, key, path key; root → a field → a value →
    books → download (also from inside a zip); search; a shelf; 401 without credentials, and with a cookie only.
13. [ ] Validation: `agents/tools/opds-validate/` (offline RelaxNG check of OPDS 1.2 / Atom, with a README); every feed
    kind validated (in tests, or the tool over a running server in CI).
14. [ ] Benchmark at 2M on both providers: OPDS scenarios in `tests/Librarion.Benchmarks/Bench/RunCommand.cs` (root,
    Recent first and deep page, a field's first letters uncached (~90 seeks over 500k authors), a field letter, a big author's books, a series by number, search, a shelf, a page's
    entries with files) within the OPDS budget (150 ms p95), no full scan; Basic cold vs cached; idle memory.
15. [ ] Docs and close: user docs (`docs/user-guide/` OPDS with KOReader, Moon+ and Thorium set up, API keys,
    shelves; `docs/reference/rights.md`), feature notes (`opds-auth`, `opds`, `shelves`, `rights`, `accounts`,
    `merging`), CHANGELOG; the by-hand list for the user (KOReader, Moon+, Thorium); merge `0.8.0`, this plan removed
    (what stays true moved to the notes, open items to the next-version plan), the roadmap marks stage 7 done.
16. [ ] (Optional, Q3) OPDS 2.0 JSON feeds on the same feed model (`/opds/v2/…`); if skipped, left as an open topic
    in the [next-version plan](next-version-plan.md).

## Exit criteria

From the roadmap: feeds validate against the schemas; HTTP-level e2e as a reader app; KOReader, Moon+ and Thorium
checked by hand (by the user); OPDS p95 met. Also: every `/opds` route names its right; the cookie is never accepted
on `/opds`; keys never appear in logs; scopes only narrow rights (tests); both providers green; e2e and axe AA on the
new screens (API keys, shelves) on every device.
