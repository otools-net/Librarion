# Foundation plan: roadmap to the MVP (1.0.0)

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
| D3 | Search DB-native (Postgres FTS + trigram, SQLite FTS5) behind `ISearchIndex`; book documents hold titles only, tag and field names matched separately (D20) | One less moving part; replaceable if benchmarks demand |
| D4 | **Tenant = isolated organisation** holding 1..n libraries; one shared database, `TenantId` on every tenant-owned row + global query filter | A family/organisation manages several libraries under one admin |
| D5 | Content restrictions per group: library access + tag/language/rating filters, as query predicates (`Visible()`) | Post-filters break paging at scale |
| D6 | OPDS 1.2 + OpenSearch in v1, OPDS 2.0 after it, OPDS-PSE later | 1.2 is what reader apps read |
| D7 | API keys per user: scoped, named, revocable; Basic password, Bearer, or opt-in path key ([opds-auth](../features/opds-auth.md)) | Every OPDS client can use one of them |
| D8 | `edge` for work, `main` for releases, `claude: …` commits; version starts at **`0.0.0`**, stays `0.x` until the **MVP = `1.0.0`**, declared by the user | SFE workflow; 1.0 means usable, not "stage 9 reached" |
| D9 | Tags: SFE's TagStudio model, **vocabulary per tenant**, link sources + import mappings ([tags](../features/tags.md)) | Curation survives re-imports; one merge applies to every library |
| D10 | Books inside **ZIP containers only** (Zip64 included), read in place; 7z/RAR only as per-book wrappers ([archives](../features/archives.md)) | One seek per book; solid 7z / RAR can't seek |
| D11 | **No INPX**; reconsidered only if the head-indexing benchmark fails on HDD / NAS | Books are the source of truth |
| D12 | **i18n from the first screen**, English shipped; every UI string through translation keys | Cheap now, costly to retrofit |
| D13 | Packaging: **Docker** (amd64/arm64, compose with Postgres), **portable zip / AppImage** (SQLite), **Windows service / systemd** scripts | As SFE, plus Postgres in compose |
| D14 | Sign-in: **local accounts**, behind SFE's `IUserDirectory` so LDAP / OIDC plug in later | v1 scope; no reshaping later |
| D15 | A book is shown by **its cover and its title**; no title in the metadata → **the file name**. **Every book has a cover:** extracted from the book (lazily, cached, thumbnails `s`/`m`/`l`), else **generated from a template** (several templates and palettes) **assigned to the book at import** and rendered on first view ([book-display](../features/book-display.md)) | As calibre-web, plus no "no image" anywhere (grids, OPDS) and archive libraries where most books are bare files |
| D16 | **E-reader in the app**: EPUB, FB2, MOBI/AZW3, CBZ, PDF, TXT in the browser, bundled viewers, progress per user/book/format, KOReader sync ([reader](../features/reader.md)) | Read anywhere without another app; FB2/MOBI cover what calibre-web can't |
| D17 | Image library (decode, resize, encode, shapes and shaped text with bundled fonts): chosen in stage 3; SkiaSharp + HarfBuzzSharp leading | Generated covers need text in any script; must run on arm64 and portable builds without telemetry |
| D18 | **Metadata is stored as tags under fields**: `author:"Stephen King"`, `series:`, `category:Horror`, `publisher:`, `language:`, `year:`, custom keys (`my-column:value`); order and series number on the link; no separate authors / series / publishers tables; title, rating, sizes, dates and identifiers stay columns ([metadata-tags](../features/metadata-tags.md)) | One curation, search, filter and feed mechanism for every field; merges and mappings fix author and series spellings across re-imports |
| D19 | **Duplicates are found and merged**: a book holds several files (copies, formats); duplicate books found by blocking keys (same file, same document id / ISBN, same title + author, similar), identical files merged automatically, the rest suggested; the book page shows its copies and possible duplicates; merge in a click with a redirect for the old id, undo and split; duplicate tags and fields suggested and merged the same way ([merging](../features/merging.md)) | Big collections repeat books under other titles and file names, and names under other spellings; curation must be one click and reversible |
| D20 | Free text reaches tags and fields **through the tag-name index**, not through names copied into book search documents ([metadata-tags](../features/metadata-tags.md)) | A rename or merge of a tag with 1M books touches one row instead of re-indexing 1M documents |
| D21 | **Fuzzy duplicate suggestions on demand only**, within one book, author or field page, capped; exact normalized matches catalog-wide ([merging](../features/merging.md)) | Edit distance has no ordinary index; a catalog-wide pass is millions of comparisons |
| D22 | Hidden tags and content filters **joined at query time** through `TagClosure`; narrow allow-lists measured in stage 2, precomputed visibility only if the budgets fail (user decides then) ([rights](../features/rights.md)) | Simplest and always current; the cost is known and measured, not guessed |

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
[archives](../features/archives.md), metadata fields as tags in [metadata-tags](../features/metadata-tags.md), duplicates and merges in [merging](../features/merging.md), titles and covers in
[book-display](../features/book-display.md), reading progress
in [reader](../features/reader.md). Reference for book handling (behaviour only, GPL-3 code not copied): calibre-web
(`C:\workspace\GIT\reference-projects\calibre-web`).

## Roadmap

Each stage ends with its exit criteria met and `edge` merged into `main` (when the user says), bumping the minor
version from `0.0.0` (stage 0 → `0.1.0`, …). `1.0.0` is the MVP: the user declares it, at the end of stage 9 or earlier
if the stages done by then make a usable product. Every stage keeps the four priorities: no outbound, benchmark budgets, 360 px to desktop, unit + e2e tests.

| Stage | Goal | Scope | Exit criteria |
|---|---|---|---|
| **0. Foundation** ([plan](stage-0-foundation-plan.md)) | A repo where every later change is cheap and guarded | solution, build, CI, test infrastructure (backend, client, e2e), privacy guards, i18n, responsive app shell, both DB providers wired | empty app builds and runs on both providers; all guards and the e2e smoke set green in CI |
| **1. Access and tenancy** | Who may do what, from the first real route | setup page (first server admin), local accounts (PBKDF2, lockout, throttle, recovery), tenants, groups, rights catalog + evaluator, cookie session, settings levels (server / tenant / library / user), admin UI (tenants, users, groups), security event log | `EveryRouteNamesARight`, `NoQueryCrossesTenants`; a tenant admin can't reach other tenants or server admins; e2e: setup, sign-in, admin flows on phone and desktop |
| **2. Catalog core and scale proof** | Prove priority 2 before building on it | catalog schema (books with several files, archives, identifiers, tag tables with fields and link attributes D18, duplicate blocking keys D19) on both providers, **catalog generator** (2M / 10M, messy tags, planted duplicates, groups with narrow allow-lists, deterministic), benchmark harness with budgets and plan checks, keyset list API for books and for tags per field (authors, series…), denormalized counts | p95 budgets met at 2M on Postgres, recorded for SQLite; 10M shows no growth with catalog size; no sequential scan on big tables |
| **3. Import: folders and ZIP containers** | Real books in, at scale, without unpacking | libraries (roots), background tasks + schedules UI, folder scanner (batched, resumable, change detection), **zip container reader** (port of SFE `ZipDirectory` + bounded streams), metadata readers from entry heads (FB2, EPUB, MOBI, PDF, CBZ) yielding field tags (authors, series, categories…, D18) and plain tags through the mappings, titles with the file-name fallback, covers (extracted lazily, thumbnail cache, image library D17), **generated cover templates** assigned at import, duplicate blocking keys and automatic merge of identical files (D19), download streamed from archives | 100k-entry zip indexed within budget, unchanged archive skipped, changed one diffed; hostile samples and images refused; every template renders long titles and other scripts; HDD / NAS measured (closes D11); e2e: add library, scan with progress, download from a zip |
| **4. Calibre import** | Calibre users move in with their curation | `metadata.db` importer (books, authors, series, publishers, languages and custom columns as field tags, tags incl. hierarchical, identifiers, ratings, covers, formats), re-runnable by Calibre id, through the import mappings; samples from several Calibre versions | generated 2M `metadata.db` within budget; re-import keeps every curation; e2e: import a Calibre library |
| **5. Browse and search** | The daily UI, fast on phones and desktops | virtual grid/list of covers and titles (D15), compact density, book page (cover, metadata, copies and formats, possible duplicates, **Read** / **Download**), browse pages per field (authors, series, categories, languages, publishers, custom fields) and tags, search language (`field:value` for every field, `tag:` with ancestors, `format:`, negation, hidden tags), facets with capped counts, content filters, download | p95 budgets through the UI's API calls; e2e on every phone project + desktop with layout checks; axe AA on main screens |
| **6. Tag management and curation** | Curate thousands of imported tags, fix what the metadata lacks | tag manager (parents, aliases, colors, categories, fields, merge, delete: also authors and series), picker, book / selection / scope tagging, library and folder default tags, Imported tags page (map / merge / ignore by count), tag rights; title and field edit (author order, series number), cover upload, **Change cover style**, cover templates on/off and reassignment; **duplicates** (D19): duplicates page, compare and merge books, bulk merge, undo and split, not-duplicate marks, duplicate tag and field suggestions | top 500 raw tags of the generated catalog curated in minutes, covering > 90 % of imported links; duplicate detection over the generated 2M catalog within budget, its planted duplicates found; e2e for each flow |
| **7. OPDS and API keys** | Reader apps connect with password or key | OPDS 1.2 (root, recent, authors A–Z, series, tags by category, shelves, OpenSearch), acquisition per format (FB2 also as fb2.zip), covers; Basic with verification cache and throttle; API keys UI and scopes; path keys; OPDS 2.0 if time allows | feeds validate against the schemas; HTTP-level e2e as a reader app; KOReader, Moon+, Thorium checked by hand; OPDS p95 met |
| **8. E-reader** | Read in the browser, progress follows the user | the in-app reader (D16, [reader](../features/reader.md)): EPUB, FB2, MOBI/AZW3, CBZ (foliate-js or epub.js, chosen here), PDF (pdf.js), TXT; book content sandboxed; reading preferences per user; extract cache (LRU, capped); position per user/book/format, read states, "Continue reading", KOReader sync (kosync) | e2e: each format, also from a container zip, on phones and desktop, back to the same place after leaving; cache cap holds under parallel readers |
| **9. Release (MVP candidate)** | Installable, documented, reviewed | Docker image (amd64/arm64) + compose with Postgres, portable zip / AppImage, Windows service / systemd scripts with smoke tests, user docs with screenshots, security review (SFE's attack vectors), 10M run, by-hand checks on real phones and OPDS apps, `production-upgrades` switched to "in production" | the user declares the MVP: `1.0.0` tagged |

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
- **Fields as tags at 2M:** ~650k field values and ~15M book–tag links; big fields looked up by index, not held in
  memory; sort keys for author and series denormalized on `Books` (D18).
- **Cover cost at 2M books:** never made up front; extracted or generated on first view, cached by size, long cache
  headers. Fonts for every script weigh megabytes: subset or per-script loading decided with D17.
- **Book content runs in the reader:** EPUB/FB2 HTML is a stranger's; sandboxed frame or sanitised HTML, reader CSP.
- **Duplicate detection at 2M:** blocking keys only, never pairwise; oversized groups dropped as noise; hashes only
  on size + CRC collisions (D19).
- **Narrow content filters** (D22): query-time joins scan far for a group that sees few books; measured in stage 2.
- **Wrong merges:** every merge logged, undo and split, redirects keep old links working (D19).
- **Calibre schema drift:** importer tested against `metadata.db` samples from several Calibre versions.
- **Two providers with migrations each:** every schema change makes two migrations; `ModelsOfBothProvidersAgree`.
