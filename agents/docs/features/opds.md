# OPDS feeds

Code: `src/Librarion.Api/Opds/` (`OpdsEndpoints` routes, `OpdsEndpoints.Browse` fields and tags, `OpdsEndpoints.Shelves`
shelves, `OpdsFeed` the Atom
writer, `OpdsLinks` every address, `OpdsAuthentication`), `Core/Catalog/BookQueries.EntriesAsync`,
`TagQueries.FieldsWithValuesAsync` / `InitialsAsync`. Tests: `OpdsTests`, `OpdsFeedTests`. Sign-in:
[opds-auth](opds-auth.md). Decisions: [stage 7 plan](../development/stage-7-opds-plan.md) (D-7.5–D-7.8).

## Built (stage 7 chunk 1)

- `/opds` is its own route group (`MapOpdsEndpoints`, after `/api`): GET only, excluded from the OpenAPI document
  (XML, not the JSON API), every route names its right (`library.opds`; `EveryRouteNamesARight` covers `opds`).
- Anything else under `/opds` is 404 (`{**path}` fallback, so `opensearch.xml`-like and `/opds/12/name` addresses never
  get the client's page; `opds` is excluded from `ClientPage`'s `{key}/{id}/{name}` route).
- Before setup: 503 `setup-required`, as `/api` (`RequestGuard`).
- The dev server (`npm start`) proxies `/opds` to the Api (`proxy.conf.json`).

## Built (stage 7 chunk 5): the first feeds

- **Addresses:** `/opds` the root; `/opds/l/<library>` a library's catalog, `…/recent` (newest first, rel
  `http://opds-spec.org/sort/new`), `…/titles` (A to Z), `…/books/<id>/files/<fileId>` (download), `…/books/<id>/cover/<s|m|l>`
  (JPEG). Every href is built by `OpdsLinks` from the request's `PathBase` (a proxy's folder) so a path key's prefix
  (chunk 8) is added in one place. Next pages: `?after=<cursor>` from `BookQueries.ListAsync` (keyset, 50 a page
  `OpdsEndpoints.PageSize`, no totals: D-7.6).
- **Root:** the libraries of the account's tenant where it holds `library.opds` and `library.browse` (an API key's
  libraries and scopes already narrowed them); with exactly one, that library's catalog itself (self `/opds`): no
  extra step in a reader app. A library one can't open answers 404, as one that doesn't exist.
- **Server administrators:** `CurrentAccess` takes the tenant of the library an `/opds/l/<library>` address names
  (`OpdsLinks.LibraryOf`, `AccessService.LibraryTenantAsync`, cached by `AccessChanges`): a reader app has no cookie to
  have chosen a tenant. Their root lists their current (first) tenant's libraries.
- **Writer** (`OpdsFeed`, an `IResult`): `XmlWriter` into memory (a page is a few dozen entries), then sent with its
  length; namespaces Atom, `dc` (`http://purl.org/dc/terms/`), `opds`; times UTC to the second; ids `urn:librarion:…`
  (`root`, `library:<id>`, `library:<id>:recent`, `book:<id>`); author `Librarion`; links self, start, up, next.
- **Book entries** (D-7.7): title (else the file name, D15), authors (`<author><name>`), `dc:language`, a `category`
  per category value, `summary` = series and number on a line, then the description as plain text (tags dropped,
  entities read, spaces folded, cut at a word near 1,000 characters, `OpdsFeed.Summary`); `updated` = the book's;
  cover `l` as `http://opds-spec.org/image` and `m` as `…/image/thumbnail` (`?v=` the cover version); one acquisition
  link per present file (type `BookFormats.ContentType`, `length`, title the format in capitals) only with
  `library.download` there. The extras for a page come from `BookQueries.EntriesAsync`: one query each for the
  books, their present files, their language and category links (never one per book).
- **Downloads and covers** answer exactly as the API's (`BookEndpoints.Download`, `BookEndpoints.CoverAsync`: Range,
  ETag, 410 / 409); a file of another library than the address's is 404; without `library.download` 403.
- Feed texts ("Recently added"…) are English constants for now: reader apps show them as they are.

## Built (stage 7 chunk 6): fields and tags

- **The library's catalog** also lists each field that holds values (`TagQueries.FieldsWithValuesAsync`: one seek per
  field, never a count; built-in fields by their English plural, `OpdsEndpoints.FieldNames`, custom ones by their
  name), then **Tags** (the plain tags).
- **A field** (`/opds/l/<library>/fields/<key>`) or **the tags** (`…/tags`): when its values fit one page (50), the
  values at once; else its **first letters**, then **All** (the whole list from its start: `?from=` empty). A letter
  opens `?from=<letter>`: the values from there on, by key (`TagQueries.ValuesAsync`), next pages `?after=`. Values
  denied by the content filters are left out, values without a book in the library skipped, each with "N books"
  (`TagCounts.TotalCount`) and a link typed `application/atom+xml;profile=opds-catalog` (no `kind`: a tag's page is a
  tree or its books, known when it is read).
- **First letters** (`TagQueries.InitialsAsync`): a fixed list (digits, Latin A–Z, Greek, Cyrillic incl. Ё) probed
  one seek each (the first value at or after the letter, kept when it starts with it), kept 10 minutes per tenant
  and field; names in other scripts through **All**. Found 2026-11-10: a skip scan stepping to "the next code point"
  stopped after Z on PostgreSQL (libc `en_US.utf8` sorts `[` before letters and ignores U+FFFF): no letter
  arithmetic in SQL comparisons, the probe is right in any collation.
- **A tag** (`…/tags/<id>`): with tags under it, a navigation feed ("All books: <name>", then each tag under it, at
  most `TagQueries.MaxChildren`); without, its books. `…/tags/<id>/books`: the books of it and the tags under it, a
  **series by its numbers** (`BookSort.Number`; a series with no numbers at all by title; books without a number in a
  numbered series are not in that order, as on the web), others by title; up = the field's values (or the tags).
  A merged tag's old address answers 301 to the tag it became (`TagQueries.MovedAsync`); a denied one 404.

## Built (stage 7 chunk 7): search

- **Per library**, never across libraries (Q11): `/opds/l/<library>/opensearch.xml` (OpenSearch 1.1, type
  `application/opensearchdescription+xml`; ShortName cut to 16 characters; `Url` templates for the acquisition type
  and plain `application/atom+xml`, both `…/search?q={searchTerms}`; no `{startPage}`: next pages by key).
- **Every feed of a library** (and the root when it is the only library's catalog) has two `search` links: the
  description and the template itself (typed acquisition), for apps that read only one form. `OpdsFeed.Start` takes
  the library from the address (`OpdsLinks.CurrentLibrary`) or its `library` argument. The root listing several
  libraries has none.
- **The search feed** (`…/search?q=&after=`): `BookQueries.ListAsync` with `Query` (the search language, as the app's
  search box; `SearchQuery` caps length and terms, never throws), by title, 50 a page, `next` keeps `q`. A blank `q`
  answers an empty feed, not every book. Up = the library's catalog.
- Test: `OpdsFeedTests.ALibraryIsSearchedPageByPage`.

## Built (stage 7 chunk 8a): `fb2.zip`, merged books

- **FB2 also zipped:** beside an FB2's acquisition link, a second one (`…/files/<id>/zip`, type `application/fb2+zip`
  as FB2 catalogs name it, title `FB2.ZIP`, no `length`). `ZippedDownload` zips while it sends: one entry with the
  file's name, the answer `application/zip` named `<name>.zip`, no Range, no length; from a loose file or an archive
  entry alike (`BookDownloads.Open`), problems (410 / 409) answered before anything is sent
  (`BookEndpoints.FileProblem`). Other formats' `/zip` address is 404 (`OpdsFeed.Zipped`).
- Gotcha: `ZipArchive` still writes synchronously in places (even through `CreateAsync` / `OpenAsync`), which the
  response refuses (`AllowSynchronousIO` off): it writes into a buffer (`PendingStream`) sent on after each 64 KB
  read, so memory stays about a chunk; no sync IO allowed.
- **Merged books:** a reader app holding a merged book's entry still gets its file and cover from the old addresses
  (`BookDownloads.FindAsync` and the cover lookup follow `BookMerger.CurrentAsync`); answered, not redirected (the
  file id is the same); the merged book is no longer listed.
- Tests: `OpdsFeedTests.AnFb2IsAlsoOfferedZipped`, `AMergedBooksOldAddressesStillAnswer`.

## Built (stage 7 chunk 11): shelves

- **Per library**, as everything in the catalog (Q11): shelves hold books of every library, so each library's
  catalog shows its own books of them. A library's catalog lists **My shelves** (after All books by title) only
  when the account holds `account.shelves` and has a shelf (one short query, `OpdsEndpoints.HasShelvesAsync`).
- `/opds/l/<library>/shelves`: the account's shelves by name (never paged: at most `shelves.maxPerAccount`), no
  counts (a shelf's count is of every library). `/opds/l/<library>/shelves/<id>`: its books of that library in the
  shelf's own order, 50 a page by key (`Shelves.BooksAsync` with `library`), up = the list; another account's shelf
  404. Both routes name `account.shelves` besides the group's `library.opds` (403 without it). The right is an
  account one: an API key's library list doesn't narrow it.
- Test: `OpdsFeedTests.ShelvesAreFeedsOfEachLibraryInTheirOwnOrder`.

## Built (stage 7 chunk 12): end to end as a reader app

- `tests/e2e/specs/opds.spec.ts` (`@opds`, desktop project only: HTTP, no screen) with `fixtures/opds.ts`
  (`ReaderApp`: Playwright's request context with Basic or nothing; `parseFeed` reads our own Atom with regular
  expressions, not a schema check). One flow: no credentials 401 with the Basic challenge; the web cookie alone 401;
  a wrong password 401; the password: root (a list: every test's libraries share the tenant) → the library →
  Authors → the author → the books; downloads compared byte for byte, loose and from inside a ZIP; the `fb2.zip`
  starts with `PK`; a thumbnail is JPEG; search through the feed's `{searchTerms}` template; a shelf made through the
  API shows in its own order; a key as the password; a key in the address alone, every feed link keeping the
  prefix, down to a download.
- The libraries are made through the API as the administrator (no page needed); an account of the Users group
  reads them at once (Users hold the library rights tenant-wide).

## Planned (stage 7)

- Keys in the address (`/opds/k/<key>/…`, every link keeping the prefix): [opds-auth](opds-auth.md#built-stage-7-chunk-8b-keys-in-the-address).
