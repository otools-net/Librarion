# AGENTS.md

Guidance for AI agents working on this repository. This file is an **index**: the basic rules, then where each
functionality lives (its notes in `agents/docs/` and its code). `README.md` is the short overview for users, `docs/` the
end-user documentation; building, testing and the code are described in `agents/docs/`.

**Librarion**: self-hosted e-book library server (in the spirit of Calibre-Web) built for **huge libraries (2M+
books)**: browse, search, read and download books, with OPDS feeds for reader apps (username/password and API keys),
and users, tenants and rights. .NET 10 minimal APIs + EF Core in `src/Librarion.Core` / `src/Librarion.Api`, Angular
client in `src/Librarion.Web`, tests in `tests/Librarion.Tests` and `tests/e2e`. **Not in production yet**: until the
MVP (1.0.0), schema and settings may change without migrations (see
[production-upgrades](agents/docs/development/production-upgrades.md)).

Books come as loose files or by the hundred thousand inside ZIP archives, indexed and read **without unpacking**
([archives](agents/docs/features/archives.md)); reference for archive-based libraries: MyHomeLib
(`C:\workspace\MyHomeLib`).

Reference for how books are shown and read (covers, titles, the reader; behaviour only, its GPL-3 code is never
copied): calibre-web (`C:\workspace\calibre-web`).

Reference project for design, rights and philosophy: SimpleFileExplorer (`C:\workspace\SimpleFileExplorer`, SFE).
When a pattern exists there (tenants, rights catalog, sessions, settings levels, tests), port it and say so in the
note rather than inventing a new one.

## Priorities (in this order, when they conflict)

1. **Privacy:** fully local; no CDN, no telemetry, no call home, nothing leaves the server unless an administrator
   turns it on ([privacy](agents/docs/development/privacy.md)).
2. **Huge catalogs:** millions of books and tens of millions of related rows stay fast
   ([scale](agents/docs/development/scale.md)).
3. **Responsive and compact UI:** one app for desktop and phones, dense by default
   ([frontend](agents/docs/development/frontend.md)).
4. **Tests:** unit tests for every functionality, end-to-end tests for the important flows
   ([testing](agents/docs/development/testing.md)).

## Keeping these notes current

- Add what the next agent would need (a convention, a gotcha, a command that works or fails), fix or delete what is
  wrong. One fact per bullet, short.
- Details go in `agents/docs/<area>/<topic>.md`, **one small file per functionality**: prefer a new file over growing
  one that already covers several things. Each file starts with its title and the code files it is about.
- This file stays an index: a new topic file gets one line in the table below (what it covers, where the code is);
  never put implementation details here.
- Areas: `architecture/` (how the app is put together), `development/` (rules for changing it), `environment/`
  (commands, build, this machine), `features/` (one file per function of the app).
- Link between notes with relative links; `node agents/tools/check-links/check-links.mjs` checks them.
- A plan (`agents/docs/development/*-plan.md`) is removed once its epic is finalized: move what stays true (decisions,
  gotchas, how it works) into the feature notes, then delete the plan, its line in the index and every link to it.
  Git history keeps it; work still left becomes a new, smaller plan or a bullet.

## Basic rules (details in the linked notes)

- **Privacy:** no external URL in the client or the server's defaults, no telemetry (ours or a library's), nothing
  fetched at run time from the internet; anything outbound is opt-in by an administrator
  ([privacy](agents/docs/development/privacy.md)).
- **Scale:** millions of books, hundreds of thousands of authors and tags; nothing proportional to the catalog per
  request, every list pages by key ([scale](agents/docs/development/scale.md)).
  A feature or idea (the user's or an agent's) that would cost at scale is **brought to the user before it is added**:
  the cost, alternatives, a recommendation, or a recommendation not to add it when no good alternative exists.
- **UI:** every screen works from a 360 px phone to a desktop, compact by default, touch and mouse
  ([frontend](agents/docs/development/frontend.md)).
- **Tests** come with every feature and fix; important flows also get end-to-end tests; not done until `dotnet test`
  and `npm test` pass ([testing](agents/docs/development/testing.md)).
- **Rights:** every route names its right; new functions get one ([rights](agents/docs/features/rights.md)).
- **Version + changelog:** each commit on `edge` adds its lines under `## Unreleased` in `CHANGELOG.md`; the version
  is bumped only when `edge` is merged into `main`
  ([versioning-and-changelog](agents/docs/development/versioning-and-changelog.md)).
- **User docs:** user-visible changes go in `docs/` ([user-docs](agents/docs/development/user-docs.md)).
- **Database:** both providers (SQLite, PostgreSQL) work; every model change makes a migration for each
  ([database](agents/docs/development/database.md)).
- **Texts:** every UI text is a translation key in `src/i18n/en.json` ([frontend](agents/docs/development/frontend.md)).
- **Git:** work is committed on the `edge` branch; messages `claude: <short summary>`; a commit after each finished
  stage or functionality of importance, green tests only (decided 2026-10-31). `edge` is pushed after each finished
  stage and merged into `main` with the version bump when a stage's exit criteria are met, without asking (the
  user's decision, 2026-11-01); tags only when the user asks.
- **Long background runs:** a run in the background expected to take more than 10 minutes (benchmarks, full test or
  e2e suites, big imports) gets a cron job showing its progress every 10 minutes, deleted when the run ends (the
  user's rule, 2026-11-08; how: [commands](agents/docs/environment/commands.md#long-runs-in-the-background)).
- **Agent tools:** put any reusable helper (scripts, generators, drivers) in `agents/tools/<job>/` with a `README.md`,
  never only in the scratchpad; work output goes to a temp folder, not the repo.

## Index

### Architecture — `agents/docs/architecture/`

| Note | Covers | Code |
|---|---|---|
| [stack](agents/docs/architecture/stack.md) | technologies, versions, external references | `Librarion.slnx`, `Directory.Packages.props` |
| [source-layout](agents/docs/architecture/source-layout.md) | **what each folder holds** (start here to find code) | `src/`, `tests/` |
| [sessions-and-pipeline](agents/docs/architecture/sessions-and-pipeline.md) | middleware order, CSP, request guard, deep addresses (`ClientPage`) | `Api/Program.cs`, `Api/Security/` |
| [config-files](agents/docs/architecture/config-files.md) | configuration sources and order, keys so far | `Api/Program.cs`, `appsettings.json` |

### Development rules — `agents/docs/development/`

| Note | Covers | Code |
|---|---|---|
| [foundation-plan](agents/docs/development/foundation-plan.md) | **roadmap to the MVP (1.0.0)**: priorities, confirmed decisions, stages 0–9, budgets, risks | |
| [stage-6-curation-plan](agents/docs/development/stage-6-curation-plan.md) | **stage 6, start here** (decisions confirmed 2026-11-05): tag manager, picker, tagging and editing books, default tags, imported tags, covers, duplicates and merges | `Core/Tagging/`, `Core/Merging/`, `src/Librarion.Web/` |
| [privacy](agents/docs/development/privacy.md) | fully local: no CDN, no telemetry, opt-in outbound, the tests guarding it | |
| [scale](agents/docs/development/scale.md) | keyset paging, counts, batches, the 2M / 10M benchmark and its measured numbers | |
| [frontend](agents/docs/development/frontend.md) | responsive and compact UI, phones, touch, Angular conventions | `src/Librarion.Web/` |
| [testing](agents/docs/development/testing.md) | test rules, layers, infrastructure | `tests/` |
| [testing-backend](agents/docs/development/testing-backend.md) | provider runs, SQLite pools, outbound guard, built-client tests | `tests/Librarion.Tests/` |
| [testing-client](agents/docs/development/testing-client.md) | Vitest/jsdom gotchas, fakes | `src/Librarion.Web/src/testing/` |
| [testing-e2e](agents/docs/development/testing-e2e.md) | Playwright: devices, guard, layout check, no mouse | `tests/e2e/` |
| [database](agents/docs/development/database.md) | two providers, migrations per provider, schema agreement | `Core/Data/` |
| [code-style](agents/docs/development/code-style.md) | C# style | `.editorconfig` |
| [production-upgrades](agents/docs/development/production-upgrades.md) | before / after the first release | |
| [versioning-and-changelog](agents/docs/development/versioning-and-changelog.md) | branches, version bumps, `CHANGELOG.md` | `Directory.Build.props` |
| [user-docs](agents/docs/development/user-docs.md) | what goes in `docs/`, README, OpenAPI | `docs/` |

### Environment — `agents/docs/environment/`

| Note | Covers |
|---|---|
| [commands](agents/docs/environment/commands.md) | build, test (both providers), run, client, e2e |
| [build](agents/docs/environment/build.md) | client build inside MSBuild, theme-boot, the fixed static-assets error |
| [dev-machine](agents/docs/environment/dev-machine.md) | this machine: SDK, Node, Edge, Docker without Hub access |
| [line-endings](agents/docs/environment/line-endings.md) | LF everywhere, autocrlf warnings, ef output |

### Features — `agents/docs/features/`

| Note | Covers | Code |
|---|---|---|
| [browse](agents/docs/features/browse.md) | the library page: grid of covers or list, sort and view, windowed rows, pages by key, the place kept on the way back; fields, A–Z value lists with counts, tag pages | `app/browse/`, `app/tags/` |
| [book-page](agents/docs/features/book-page.md) | a book's page: metadata, fields as links, files with Download, details, possible duplicates (same file, identifier, work; on demand, capped) | `app/books/`, `BookQueries.DuplicatesAsync` |
| [search](agents/docs/features/search.md) | the search language, the full-text index per provider (FTS5, GIN), terms as keyset conditions by size, hidden tags, suggestions, facets with capped counts (Refine), the search box and results page | `Core/Search/`, `shell/search-box.ts`, `app/search/` |
| [admin-page](agents/docs/features/admin-page.md) | the administration: groups, sections and sub-pages (`admin-sections.ts`), its own column, overview, breadcrumbs, moved addresses | `app/admin/`, `styles/admin.css` |
| [admin-search](agents/docs/features/admin-search.md) | the administration's search: sections, sub-pages and settings by every word, marks, a setting opened scrolled to and flashed, the catalog route | `app/admin/admin-search.ts`, `settings/setting-reveal.ts` |
| [shell](agents/docs/features/shell.md) | the frame: top bar, library switcher (`LibraryContext`), account menu, sidebar, phone sheets, the administration's own frame | `app/shell/`, `core/library-context.ts`, `styles/shell.css` |
| [design](agents/docs/features/design.md) | the look: tokens, ink and paper palette, layered surfaces, type scale, motion, tested contrast (SFE's approach) | `src/styles/tokens.css`, `ClientDesignTests` |
| [rights](agents/docs/features/rights.md) | rights catalog, scopes, built-in groups, evaluation and its cache, route rule; planned rights and content filters | `Core/Access/`, `Api/Security/` |
| [accounts](agents/docs/features/accounts.md) | accounts and groups, passwords, sign-in throttle and lockout, session cookie and stamp, recovery codes | `Core/Access/`, `Api/Security/`, `app/account/`, `app/admin/` |
| [tenants](agents/docs/features/tenants.md) | tenants and libraries, isolation by query filters (`TenantDb`), server administrators across tenants | `Core/Data/`, `Core/Access/`, `app/admin/` |
| [setup](agents/docs/features/setup.md) | first start: setup code (configured or generated), 503 until set up, recovery mode | `Core/Access/`, `Api/Security/`, `app/account/` |
| [settings](agents/docs/features/settings.md) | settings by level (server, tenant, library, user), locks, catalog of keys | `Core/Settings/`, `app/settings/` |
| [security-log](agents/docs/features/security-log.md) | security events: what is logged (never secrets), retention, who reads | `Core/Access/SecurityLog.cs`, `app/admin/` |
| [catalog](agents/docs/features/catalog.md) | books, files, identifiers, tag tables, sort indexes, visibility, keyset pages, tag joins by size, catalog routes, known limits | `Core/Data/Entities/Catalog/`, `Core/Catalog/`, `Api/Endpoints/BookEndpoints.cs` |
| [tasks](agents/docs/features/tasks.md) | library roots (who adds which folder), scans and root removals as background runs, run history, schedules | `Core/Import/LibraryRoots.cs`, `Core/Tasks/`, `Api/Endpoints/TaskEndpoints.cs` |
| [task-progress](agents/docs/features/task-progress.md) | the progress panel under the top bar and Administration › Background tasks (cards, schedules, history across libraries) | `core/task-watch.ts`, `shared/task-panel.ts`, `admin/tasks-section.ts` |
| [folders](agents/docs/features/folders.md) | the folders books are in (roots, directories, ZIPs and directories inside them): every file in one, stored counts, the folder view (`library.folders`) | `Core/Import/Folders.cs`, `Core/Catalog/FolderQueries.cs`, `app/folders/` |
| [default-tags](agents/docs/features/default-tags.md) | default tags of a library and its folders: given as rule links to books that come in or move in, "also those there", removal | `Core/Tagging/DefaultTags.cs`, `CatalogWriter`, `app/folders/default-tags.ts` |
| [archives](agents/docs/features/archives.md) | books inside ZIP containers (100k+ entries): indexing without unpacking, reading in place, extract cache (planned) | `Core/Containers/`, `Core/Import/`, `Core/Reading/` |
| [book-display](agents/docs/features/book-display.md) | cover + title of every book, file-name fallback, extracted covers and thumbnails, generated cover templates, a cover uploaded or styled on the book page, the data folder, Cover styles (templates on / off, reassign) | `Core/Catalog/`, `Core/Covers/` |
| [reader](agents/docs/features/reader.md) | the in-app e-reader: formats, viewers, sandboxing, preferences, progress, KOReader sync (planned) | `app/reader/`, `Core/Reading/` |
| [imported-tags](agents/docs/features/imported-tags.md) | the Imported tags page: raw names recorded by the importer, a library's tags by books (small fields whole, big ones walked by key), keep, map, ignore and its undo, rights | `Core/Tagging/ImportedTags.cs`, `Merging/TagMerger.Imported.cs`, `admin/imported-tags-section.ts` |
| [tags](agents/docs/features/tags.md) | tag model (SFE/TagStudio), imported tags and mappings, scope and default tags (planned) | `Core/Tagging/` |
| [metadata-tags](agents/docs/features/metadata-tags.md) | metadata as `field:value` tags: authors, series, categories, publishers, languages, custom fields; import, search, browse (planned) | `Core/Tagging/`, `Core/Import/` |
| [book-formats](agents/docs/features/book-formats.md) | one class per format (key, extensions, media type, metadata, cover), the registry, adding a format | `Core/Formats/` |
| [metadata-readers](agents/docs/features/metadata-readers.md) | FB2, EPUB, CBZ, MOBI, PDF heads: what each gives, limits, safe XML, the reader-loop gotcha | `Core/Import/Metadata/` |
| [imports](agents/docs/features/imports.md) | imports from other sources (Calibre `metadata.db`, INPX): pipeline, matching, who owns metadata, the route, Calibre mapping | `Core/Imports/`, `CatalogWriter` |
| [missing-books](agents/docs/features/missing-books.md) | books whose files are gone: kept, found again (same place or moved), removed only when a user accepts (SFE missing files) | `Core/Import/MissingBooks.cs`, `LibraryScanner` |
| [merging](agents/docs/features/merging.md) | duplicate books (copies, formats, same work) and tags: stored groups and their upkeep, the Duplicates page, compare and merge, redirects, Not a duplicate; undo and split (planned) | `Core/Merging/` |
| [ereader-mode](agents/docs/features/ereader-mode.md) | e-reader mode: plain grayscale site for e-ink browsers (search, book info, download; no script) (planned) | `Api/EReader/` |
| [conversion](agents/docs/features/conversion.md) | format conversion on demand: built-in FB2/EPUB → EPUB, KEPUB, AZW3; optional Calibre (MOBI, other inputs); queue, cache (planned) | `Core/Conversion/` |
| [opds-auth](agents/docs/features/opds-auth.md) | OPDS / API authentication: Basic, API keys, key-in-URL (planned) | `Api/Security/` |
