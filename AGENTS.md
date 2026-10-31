# AGENTS.md

Guidance for AI agents working on this repository. This file is an **index**: the basic rules, then where each
functionality lives (its notes in `agents/docs/` and its code). `README.md` is the short overview for users, `docs/` the
end-user documentation; building, testing and the code are described in `agents/docs/`.

**Librarion**: self-hosted e-book library server (in the spirit of Calibre-Web) built for **huge libraries (2M+
books)**: browse, search, read and download books, with OPDS feeds for reader apps (username/password and API keys),
and users, tenants and rights. .NET 10 minimal APIs + EF Core in `src/Librarion.Core` / `src/Librarion.Api`, Angular
client in `src/Librarion.Web`, tests in `tests/Librarion.Tests` and `tests/e2e`. **Not in production yet**: until the
first release, schema and settings may change without migrations (see
[production-upgrades](agents/docs/development/production-upgrades.md)).

Books come as loose files or by the hundred thousand inside ZIP archives, indexed and read **without unpacking**
([archives](agents/docs/features/archives.md)); reference for archive-based libraries: MyHomeLib
(`C:\workspace\GIT\reference-projects\MyHomeLib`).

Reference project for design, rights and philosophy: SimpleFileExplorer (`C:\workspace\GIT\SimpleFileExplorer`, SFE).
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
- Link between notes with relative links.
- A plan (`agents/docs/development/*-plan.md`) is removed once its epic is finalized: move what stays true (decisions,
  gotchas, how it works) into the feature notes, then delete the plan, its line in the index and every link to it.
  Git history keeps it; work still left becomes a new, smaller plan or a bullet.

## Basic rules (details in the linked notes)

- **Privacy:** no external URL in the client or the server's defaults, no telemetry (ours or a library's), nothing
  fetched at run time from the internet; anything outbound is opt-in by an administrator
  ([privacy](agents/docs/development/privacy.md)).
- **Scale:** millions of books, hundreds of thousands of authors and tags; nothing proportional to the catalog per
  request, every list pages by key ([scale](agents/docs/development/scale.md)).
- **UI:** every screen works from a 360 px phone to a desktop, compact by default, touch and mouse
  ([frontend](agents/docs/development/frontend.md)).
- **Tests** come with every feature and fix; important flows also get end-to-end tests; not done until `dotnet test`
  and `npm test` pass ([testing](agents/docs/development/testing.md)).
- **Rights:** every route names its right; new functions get one ([rights](agents/docs/features/rights.md)).
- **Version + changelog:** each commit on `edge` adds its lines under `## Unreleased` in `CHANGELOG.md`; the version
  is bumped only when `edge` is merged into `main`
  ([versioning-and-changelog](agents/docs/development/versioning-and-changelog.md)).
- **User docs:** user-visible changes go in `docs/` ([user-docs](agents/docs/development/user-docs.md)).
- **Git:** work is committed on the `edge` branch, merged into `main` when ready; messages `claude: <short summary>`;
  commit only when asked.
- **Agent tools:** put any reusable helper (scripts, generators, drivers) in `agents/tools/<job>/` with a `README.md`,
  never only in the scratchpad; work output goes to a temp folder, not the repo.

## Index

### Development rules — `agents/docs/development/`

| Note | Covers | Code |
|---|---|---|
| [foundation-plan](agents/docs/development/foundation-plan.md) | **roadmap to the MVP (1.0.0)**: priorities, confirmed decisions, stages 0–9, budgets, risks | |
| [stage-0-foundation-plan](agents/docs/development/stage-0-foundation-plan.md) | stage 0 work items and exit criteria (repo, build, tests, privacy guards, i18n, shell, CI) | |
| [privacy](agents/docs/development/privacy.md) | fully local: no CDN, no telemetry, opt-in outbound, the tests guarding it | |
| [scale](agents/docs/development/scale.md) | keyset paging, counts, batches, the 2M / 10M benchmark | |
| [frontend](agents/docs/development/frontend.md) | responsive and compact UI, phones, touch, Angular conventions | `src/Librarion.Web/` |
| [testing](agents/docs/development/testing.md) | test rules, layers, infrastructure | `tests/` |
| [production-upgrades](agents/docs/development/production-upgrades.md) | before / after the first release | |
| [versioning-and-changelog](agents/docs/development/versioning-and-changelog.md) | branches, version bumps, `CHANGELOG.md` | `Directory.Build.props` |
| [user-docs](agents/docs/development/user-docs.md) | what goes in `docs/`, README, OpenAPI | `docs/` |

### Features — `agents/docs/features/`

| Note | Covers | Code |
|---|---|---|
| [rights](agents/docs/features/rights.md) | rights catalog, scopes, evaluation (planned, ported from SFE) | `Core/Access/` |
| [archives](agents/docs/features/archives.md) | books inside ZIP containers (100k+ entries): indexing without unpacking, reading in place, extract cache (planned) | `Core/Containers/`, `Core/Import/`, `Core/Reading/` || [tags](agents/docs/features/tags.md) | tag model (SFE/TagStudio), imported tags and mappings, scope and default tags (planned) | `Core/Tagging/` |
| [opds-auth](agents/docs/features/opds-auth.md) | OPDS / API authentication: Basic, API keys, key-in-URL (planned) | `Api/Security/` |
