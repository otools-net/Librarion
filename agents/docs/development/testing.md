# Testing: rules and infrastructure

Code: `tests/Librarion.Tests/` (`Infrastructure/`), client `src/Librarion.Web/src/testing/` (`setup.ts`, `fakes.ts`)
and `*.spec.ts` next to the code, end-to-end `tests/e2e/`, benchmark `tests/Librarion.Benchmarks/` (planned). Ported
from SFE's `testing*.md`; gotchas get their own notes (`testing-backend.md`, `testing-client.md`, `testing-e2e.md`)
as they are found.

## Rules

- **Every new feature comes with tests**, in the same change: backend (MSTest in `tests/Librarion.Tests`) for server
  behaviour, frontend (a `*.spec.ts`) for client behaviour, both when it spans the two.
- A bug fix gets a test that fails without it.
- Not done until `dotnet test` and `npm test` pass.
- **Important flows get end-to-end tests** (Playwright, desktop and phone sizes), in the same change as the flow:
  setup and sign-in, browse and scroll a large library, search, book page and download (loose file and from inside
  a container zip), read in the browser (EPUB, FB2, PDF, CBZ, also from a container zip), OPDS
  with password and API key (an HTTP-level e2e, as a reader app would), tagging (book, scope, imported tags page),
  user / group / tenant administration, import with progress. Their specs carry `@smoke` for the quick set run on
  every push to `edge`; the whole suite runs before a merge into `main`.
- A change seen on phones runs the quick set and its area's specs; a phone bug fix adds an e2e test that fails
  without it. Every phone test ends with a layout check (nothing cut, nothing past the screen, tap targets), as SFE.
- Both database providers run the same backend tests (SQLite always; Postgres when `LIBRARION_TEST_POSTGRES` holds a
  connection string, as CI does): a provider-specific test says why.
- A change to a query on a big table (books, files, authors, tags) runs the benchmark for that query
  ([scale](scale.md)) and keeps its budget.
- Every route names its right (`EveryRouteNamesARight`), every tenant-owned query is filtered by tenant
  (`NoQueryCrossesTenants`): both tests fail for a new route or table that forgets.
- OPDS feeds are validated against the OPDS 1.2 RelaxNG / OPDS 2.0 JSON schemas in tests, not only by eye.
- No fixed sleeps; waits on the machine's speed go through one helper whose limit makes the test inconclusive, not
  failed (SFE's `SlowWork`), multiplied by `LIBRARION_TEST_TIME_FACTOR` on CI.
- `test.fixme` / `[Ignore]` always say why; a flaky test is fixed or marked with its reason, never deleted silently.

## Layers

| Layer | Tool | What |
|---|---|---|
| Core | MSTest, `CoreServices` | catalog, import, search parsing, rights evaluation, without HTTP |
| HTTP | MSTest, `WebApplicationFactory<Program>`, `ApiClients` | routes, rights, sessions, Basic/API key, OPDS feeds |
| Client | Vitest via `ng test` (jsdom, zoneless TestBed) | components with `mockApi()` + `testProviders(api)` |
| End-to-end | Playwright (desktop + phone sizes) | sign-in, browse, search, read, admin; `@smoke` quick set |
| Scale | benchmark runner over the 2M synthetic catalog | p95 budgets, query plans (no seq scan on big tables) |
| Compatibility | by hand, before a release | OPDS in KOReader, Moon+ Reader, Thorium, Panels/Chunky |

## Backend infrastructure (planned)

- Each test gets its own `TestCatalog`: temp library folder with sample books (odd/Unicode names) + own SQLite file
  (or Postgres database with a random name, dropped after).
- `ApiClients`: `SetUpAsync` = signed in as the server administrator, `SignInAsync(user)`, `Create` = anonymous,
  `WithBasic(user, password)`, `WithApiKey(key)`; all send `X-Librarion-Request`.
- Builders for tenants, users, groups, books (`Catalog.Book().By("Author").InSeries("S", 2).Build()`).
- Core has `InternalsVisibleTo Librarion.Tests`.

## Sample files

- Books a test needs (EPUB 2/3, PDF, FB2, MOBI/AZW3, DJVU, CBZ, broken and hostile ones), small container zips (every
  method, Zip64, code-page names, truncated, lying directories) and Calibre `metadata.db` files from
  several Calibre versions are **committed** in `tests/samples/`, made by `agents/tools/samples/` when possible, never
  per run; a change to a generator updates the samples in the same commit (as SFE's `test-samples.md`).
- The 2M synthetic catalog is **generated** (deterministic seed) by `agents/tools/catalog-generator/`, never
  committed: as a database dump for the benchmark, as a fake Calibre `metadata.db`, and as container zips of 100k+
  small FB2/EPUB entries, for the archive scanner.
- Small synthetic data written by the test itself (a few books via builders) stays in the test: it shows what is
  tested.
