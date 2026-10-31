# Foundation plan: roadmap to v1.0

From an empty repo to the first release, in stages. This file holds the decisions and the roadmap; each stage gets
its own detailed plan (`stage-<n>-<name>-plan.md`) written when the stage starts, removed when it is finalized
(decisions move into the architecture/feature notes, see AGENTS.md). Reference: SFE
(`C:\workspace\GIT\SimpleFileExplorer`), whose design is ported where it fits.

## Priorities (in this order, when they conflict)

1. **Privacy:** no CDN, no telemetry, fully local; outbound only when an administrator opts in
   ([privacy](privacy.md)).
2. **Huge catalogs:** millions of books, tens of millions of related rows, fast ([scale](scale.md)).
3. **Responsive and compact UI** for desktop and phones ([frontend](frontend.md)).
4. **Tests:** unit tests for every functionality, e2e for the important flows ([testing](testing.md)).

## Goal and non-goals

- **Goal:** a Calibre-Web-like server whose first concern is **millions of books** staying fast (browse, search,
  OPDS), books as loose files or **inside ZIP archives of 100k+ entries read in place**, OPDS clients signing in by
  **username/password or API key**, and **users, tenants and rights** as in SFE.
- **Non-goals for v1:** editing book files, format conversion, sending to Kindle, metadata from the internet,
  downloading from online libraries, INPX catalogs (D11). Each can come later behind its own right.

## Decisions (confirmed 2026-10-31)

| # | Decision | Why |
|---|---|---|
| D1 | **Own database.** Sources: folder scan (FB2/EPUB/PDF/MOBI/AZW3/DJVU/CBZ… as files or inside ZIP containers, metadata read from the books) and **Calibre `metadata.db` import in v1**; both re-runnable | Calibre's schema is what fails at 2M; Calibre users keep their curation |
| D2 | **PostgreSQL + SQLite**, both with EF Core migrations (one set per provider), the same tests on both | Postgres for large installs (`tsvector`, `pg_trgm`, keyset plans); SQLite for portable installs |
| D3 | Search DB-native (Postgres FTS + trigram, SQLite FTS5) behind `ISearchIndex` | One less moving part; replaceable if benchmarks demand |
| D4 | **Tenant = isolated organisation** holding 1..n libraries; one shared database, `TenantId` on every tenant-owned row + global query filter | A family/organisation manages several libraries under one admin |
| D5 | Content restrictions per group: library access + tag/language/rating filters, as query predicates (`Visible()`) | Post-filters break paging at scale |
| D6 | OPDS 1.2 + OpenSearch in v1, OPDS 2.0 after it, OPDS-PSE later | 1.2 is what reader apps read |
| D7 | API keys per user: scoped, named, revocable; Basic password, Bearer, or opt-in path key ([opds-auth](../features/opds-auth.md)) | Every OPDS client can use one of them |
| D8 | `edge` for work, `main` for releases, `claude: …` commits, version `0.<stage>.x` until v1.0 | SFE workflow |
| D9 | Tags: SFE's TagStudio model, **vocabulary per tenant**, link sources + import mappings ([tags](../features/tags.md)) | Curation survives re-imports; one merge applies to every library |
| D10 | Books inside **ZIP containers only** (Zip64 included), read in place; 7z/RAR only as per-book wrappers ([archives](../features/archives.md)) | One seek per book; solid 7z / RAR can't seek |
| D11 | **No INPX**; reconsidered only if the head-indexing benchmark fails on HDD / NAS | Books are the source of truth |
| D12 | **i18n from the first screen**, English shipped; every UI string through translation keys | Cheap now, costly to retrofit |
| D13 | Packaging: **Docker** (amd64/arm64, compose with Postgres), **portable zip / AppImage** (SQLite), **Windows service / systemd** scripts | As SFE, plus Postgres in compose |
| D14 | Sign-in: **local accounts**, behind SFE's `IUserDirectory` so LDAP / OIDC plug in later | v1 scope; no reshaping later |

## Architecture (ported from SFE)

```
src/Librarion.Core/     domain, no ASP.NET: Access, Catalog, Containers (zip reader), Import, Reading, Search,
                        Tagging, Covers, Tasks, Settings, Data (DbContext, providers, migrations per provider)
src/Librarion.Api/      Program.cs, Endpoints/*Endpoints.cs, Opds/, Security/, Tenancy/, Maintenance/
src/Librarion.Web/      Angular (standalone, signals, zoneless, OnPush, i18n), built into Api/wwwroot
tests/Librarion.Tests/  MSTest: Core + HTTP (WebApplicationFactory), SQLite always, Postgres when configured
tests/Librarion.Benchmarks/  scale benchmark over generated catalogs
tests/e2e/              Playwright, phone + desktop projects
agents/tools/           catalog generator, samples, benchmark runner, OPDS validator, docs screenshots
```

Data model: detailed in each stage plan; tags in [tags](../features/tags.md), archives in
[archives](../features/archives.md).

## Roadmap

Each stage ends with its exit criteria met, `edge` merged into `main` (when the user says) and a `0.<stage>.0`
version. Every stage keeps the four priorities: no outbound, benchmark budgets, 360 px to desktop, unit + e2e tests.

| Stage | Goal | Scope | Exit criteria |
|---|---|---|---|
| **0. Foundation** ([plan](stage-0-foundation-plan.md)) | A repo where every later change is cheap and guarded | solution, build, CI, test infrastructure (backend, client, e2e), privacy guards, i18n, responsive app shell, both DB providers wired | empty app builds and runs on both providers; all guards and the e2e smoke set green in CI |
| **1. Access and tenancy** | Who may do what, from the first real route | setup page (first server admin), local accounts (PBKDF2, lockout, throttle, recovery), tenants, groups, rights catalog + evaluator, cookie session, settings levels (server / tenant / library / user), admin UI (tenants, users, groups), security event log | `EveryRouteNamesARight`, `NoQueryCrossesTenants`; a tenant admin can't reach other tenants or server admins; e2e: setup, sign-in, admin flows on phone and desktop |
| **2. Catalog core and scale proof** | Prove priority 2 before building on it | catalog schema (books, files, archives, authors, series, publishers, languages, identifiers, tag tables) on both providers, **catalog generator** (2M / 10M, messy tags, deterministic), benchmark harness with budgets and plan checks, keyset list API for books / authors / series / tags, denormalized counts | p95 budgets met at 2M on Postgres, recorded for SQLite; 10M shows no growth with catalog size; no sequential scan on big tables |
| **3. Import: folders and ZIP containers** | Real books in, at scale, without unpacking | libraries (roots), background tasks + schedules UI, folder scanner (batched, resumable, change detection), **zip container reader** (port of SFE `ZipDirectory` + bounded streams), metadata readers from entry heads (FB2, EPUB, MOBI, PDF, CBZ), FB2 genres, imported tags through mappings, lazy covers, download streamed from archives | 100k-entry zip indexed within budget, unchanged archive skipped, changed one diffed; hostile samples refused; HDD / NAS measured (closes D11); e2e: add library, scan with progress, download from a zip |
| **4. Calibre import** | Calibre users move in with their curation | `metadata.db` importer (books, authors, series, tags incl. hierarchical, identifiers, ratings, covers, formats), re-runnable by Calibre id, through the import mappings; samples from several Calibre versions | generated 2M `metadata.db` within budget; re-import keeps every curation; e2e: import a Calibre library |
| **5. Browse and search** | The daily UI, fast on phones and desktops | virtual grid/list, compact density, book page, authors / series / tags / languages / publishers pages, search language (`author:`, `tag:` with ancestors, `series:`, `lang:`, `format:`, negation, hidden tags), facets with capped counts, content filters, download | p95 budgets through the UI's API calls; e2e on every phone project + desktop with layout checks; axe AA on main screens |
| **6. Tag management** | Curate thousands of imported tags | tag manager (parents, aliases, colors, categories, merge, delete), picker, book / selection / scope tagging, library and folder default tags, Imported tags page (map / merge / ignore by count), tag rights | top 500 raw tags of the generated catalog curated in minutes, covering > 90 % of imported links; e2e for each flow |
| **7. OPDS and API keys** | Reader apps connect with password or key | OPDS 1.2 (root, recent, authors A–Z, series, tags by category, shelves, OpenSearch), acquisition per format (FB2 also as fb2.zip), covers; Basic with verification cache and throttle; API keys UI and scopes; path keys; OPDS 2.0 if time allows | feeds validate against the schemas; HTTP-level e2e as a reader app; KOReader, Moon+, Thorium checked by hand; OPDS p95 met |
| **8. Reading** | Read in the browser, progress follows the user | bundled readers: EPUB (epub.js), PDF (pdf.js), FB2 (ours), CBZ (pages from the inner zip); extract cache (LRU, capped); read states, shelves, KOReader sync (kosync) | e2e: each format, also from a container zip, on phones and desktop; cache cap holds under parallel readers |
| **9. Release v1.0** | Installable, documented, reviewed | Docker image (amd64/arm64) + compose with Postgres, portable zip / AppImage, Windows service / systemd scripts with smoke tests, user docs with screenshots, security review (SFE's attack vectors), 10M run, by-hand checks on real phones and OPDS apps, `production-upgrades` switched to "in production" | v1.0.0 tagged by the user |

Order: rights before the first real route (1); the scale proof (2) before import and UI build on the schema; import
(3, 4) before the UI (5) so every screen is built against millions of real-shaped rows.

## Scale budgets (checked by the benchmark, see [scale](scale.md))

- Catalog: 2M books, 500k authors, 100k series, 50k tags, 2.5M files, one library; 10M before each release.
- p95 (warm, Postgres): any list page < 100 ms, search < 300 ms, OPDS feed page < 150 ms, book detail < 50 ms.
- Archives: list a 100k-entry zip < 2 s, skip an unchanged one < 50 ms, 100k FB2 entries indexed from heads < 3 min
  (SSD), first byte of a book from a 100k-entry zip < 50 ms; HDD / NAS measured (decides D11).
- Calibre `metadata.db` of 2M books: < 30 min (Postgres); unchanged re-import < 5 min.
- Server memory with the 2M catalog idle: < 500 MB.

## Risks

- **Counts** at 2M: exact counts only from denormalized columns, capped "1000+" elsewhere.
- **Basic auth cost:** PBKDF2 per OPDS request; cache successful verifications ([opds-auth](../features/opds-auth.md)).
- **Hostile files** (zip bombs, huge XML): bounded reads, timeouts, DTDs off, as SFE's archive readers.
- **Nested books** deflated inside container zips: extract cache on open; its cap and eviction under many readers.
- **Archives changing under the server:** locators checked against the local header, size + mtime before use.
- **Imported tag sprawl:** mappings, per-source categories, the generator makes realistic messy tags.
- **Calibre schema drift:** importer tested against `metadata.db` samples from several Calibre versions.
- **Two providers with migrations each:** every schema change makes two migrations; `ModelsOfBothProvidersAgree`.
