# OPDS feeds

Code: `src/Librarion.Api/Opds/` (`OpdsEndpoints`, `OpdsAuthentication`). Tests: `OpdsTests`. Sign-in:
[opds-auth](opds-auth.md). Decisions: [stage 7 plan](../development/stage-7-opds-plan.md) (D-7.5–D-7.8).

## Built (stage 7 chunk 1)

- `/opds` is its own route group (`MapOpdsEndpoints`, after `/api`): GET only, excluded from the OpenAPI document
  (XML, not the JSON API), every route names its right (`library.opds`; `EveryRouteNamesARight` covers `opds`).
- Anything else under `/opds` is 404 (`{**path}` fallback, so `opensearch.xml`-like and `/opds/12/name` addresses never
  get the client's page; `opds` is excluded from `ClientPage`'s `{key}/{id}/{name}` route).
- Before setup: 503 `setup-required`, as `/api` (`RequestGuard`).
- `GET /opds`: a placeholder root feed (Atom, `application/atom+xml;profile=opds-catalog;kind=navigation`) until the
  feeds are built (chunk 5).
- The dev server (`npm start`) proxies `/opds` to the Api (`proxy.conf.json`).

## Planned (stage 7)

- Root = a list of the user's libraries (the catalog itself with one); `/opds/l/<library>/…` per library;
  `/opds/k/<key>/…` path keys (D-7.5).
- Recent, All by title, fields A–Z, tags by category, shelves, OpenSearch; keyset `next` links, 50 a page, no totals
  (D-7.6); entries with acquisition links per file, FB2 also as `fb2.zip` (D-7.7); covers as JPEG (D-7.8).
