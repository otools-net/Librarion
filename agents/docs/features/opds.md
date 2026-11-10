# OPDS feeds

Code: `src/Librarion.Api/Opds/` (`OpdsEndpoints` routes, `OpdsFeed` the Atom writer, `OpdsLinks` every address,
`OpdsAuthentication`), `Core/Catalog/BookQueries.EntriesAsync`. Tests: `OpdsTests`, `OpdsFeedTests`. Sign-in:
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

## Planned (stage 7)

- Fields A–Z, tags by category (chunk 6), OpenSearch (7), `fb2.zip`, merged ids redirected, path keys
  `/opds/k/<key>/…` (8), shelves (11).
