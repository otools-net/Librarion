# Stage 0: foundation (plan)

Roadmap and decisions: [foundation-plan](foundation-plan.md). Goal: a repo where every later change is cheap and
guarded: builds, tests on three layers, privacy guards, i18n and a responsive shell exist before the first feature.
Version: `0.0.0` in `Directory.Build.props`; the merge into `main` bumps it to `0.1.0` (changelog: "first build").

Machine (2026-10-31): .NET SDK 10.0.103, Node 24.21 / npm 12.2, Docker 29.3, no local Postgres (tests use a
container). Pins taken from SFE where it has them: Angular 22, TypeScript 6.0, Vitest 5, Playwright 1.64.1 (exact),
MSTest 4, EF Core / ASP.NET packages 10.0.x.

## Work items

### 0.1 Repository

- [x] Branch `edge` from `main`; all stage work there.
- [x] `.gitattributes` (LF everywhere, binaries `-text`, `tests/samples/** -text`), `.editorconfig` (C# style from SFE
      `code-style.md`, 4-space TS), `.gitignore` (+ `!tests/samples/**`).
- [x] `global.json` (SDK 10.0.x, `rollForward: latestFeature`), `Directory.Build.props` (`Version 0.0.0`, `Nullable`,
      `ImplicitUsings`, `LangVersion latest`, warnings as errors in CI), `Directory.Packages.props` (central package
      versions), `dotnet-tools.json` (dotnet-ef).
- [x] `Librarion.slnx`, `README.md` (short, links into `docs/`), `CHANGELOG.md` (`## Unreleased`), `docs/README.md`.

### 0.2 Backend skeleton

- [x] `src/Librarion.Core` (no ASP.NET), `src/Librarion.Api` (minimal APIs, ProblemDetails, enums as camelCase
      strings, OpenAPI at `/openapi/v1.json`), `InternalsVisibleTo Librarion.Tests`.
- [x] Data: `LibrarionDbContext` with a provider switch (`Librarion:Database:Provider` = `sqlite` | `postgres`,
      connection string); **migrations per provider** (`Data/Migrations/Sqlite`, `Data/Migrations/Postgres`, provider
      contexts deriving from the shared model); initial migration with `ServerProperties` (installed version) only.
      `ModelsOfBothProvidersAgree` test.
- [x] Pipeline (SFE `sessions-and-pipeline.md`): forwarded headers, `SecurityHeaders` (CSP `default-src 'self'`,
      nosniff, frame-ancestors, Referrer-Policy), request guard (`X-Librarion-Request` on non-GET `/api`), static client
      with SPA fallback.
- [x] First routes: `GET api/system/info` (version, provider), anonymous for now and marked so; the
      `EveryRouteNamesARight` test exists from now on (anonymous routes must say `.AllowAnonymous()`).
- [x] Config files and order (SFE `config-files.md`): `appsettings.json`, `appsettings.Local.json` (git-ignored),
      environment `LIBRARION__*`.

### 0.3 Client skeleton

- [x] `src/Librarion.Web`: Angular 22, standalone, zoneless, OnPush, `strict` + `strictTemplates`, global CSS in
      `src/styles/` (tokens, base, layout partials), `analytics: false`.
- [x] Built by MSBuild into `Api/wwwroot` (git-ignored) with `-p:LibrarionSkipWebClient=true` to skip (SFE
      `build.md`).
- [x] **i18n:** runtime translations (keys in `src/i18n/en.json`, a `t()` pipe/signal service, language from user
      setting → browser → `en`), a spec that fails on a template string not going through it (allow-list for
      symbols), a spec that every key used exists in `en.json`.
- [x] **App shell, responsive from 360 px:** top bar (title, search placeholder, user menu), sidebar on desktop /
      drawer on phones, content area; light/dark theme before first paint (`theme-boot.ts`); icon sprite (bundled
      SVG); density tokens (compact default). Only `core/api.service.ts` makes HTTP calls.

### 0.4 Test infrastructure

- [x] `tests/Librarion.Tests` (MSTest 4, method-level parallelism): `Infrastructure/` with `TestCatalog` (temp library
      folder + own SQLite file, or own Postgres database when `LIBRARION_TEST_POSTGRES` is set, dropped after),
      `ApiClients` (`CreateFactory`, anonymous client; sign-in helpers come in stage 1), `SlowWork`
      (`LIBRARION_TEST_TIME_FACTOR`), `WarningLog`.
- [x] `agents/tools/postgres-dev/` (README + script): starts `postgres:17` in Docker for local runs, prints the
      connection string.
- [x] Client: Vitest via `ng test`, `src/testing/setup.ts` (jsdom stand-ins: `matchMedia`, `IntersectionObserver`,
      dialog), `fakes.ts` (`mockApi`, `testProviders`, `settle`); first specs for the shell and i18n.
- [x] `tests/e2e`: Playwright 1.64.1 pinned; projects `android` (Pixel 7), `android-small` (360 px), `iphone`
      (WebKit), `landscape`, `desktop`; `webServer` builds and starts the app on SQLite in a temp folder; guard
      fixture (console errors, page errors, CSP violations, 5xx, **any request to another origin**); final layout
      check on every phone test (`expectFitsScreen`, `expectNothingCut`, `expectTapTargets`) and helpers ported from
      SFE `tests/e2e/fixtures/`; `harness.spec.ts`; `@smoke` spec: shell opens, drawer opens/closes on phones,
      theme switch; axe on the shell (`@a11y`).
- [x] `agents/tools/` README conventions; `agents/tools/check-links/` (relative links of `AGENTS.md` and notes).

### 0.5 Privacy guards

- [x] `TheWebClientHasNoExternalUrls` (built `wwwroot`, allow-list for XML namespaces and licence texts).
- [x] `TheServerMakesNoOutboundRequests`: test host with an `HttpMessageHandler` and socket guard that fail any
      outbound call; runs over the stage's routes (later stages add their flows).
- [x] `TheWebClientHasNoInlineScripts` (CSP without `unsafe-inline`).
- [x] Telemetry off: `DOTNET_CLI_TELEMETRY_OPTOUT=1` and `NG_CLI_ANALYTICS=false` in CI and build scripts; a test
      that `angular.json` has `analytics: false`; packages audited (no SDK phoning home) — note in [privacy](privacy.md).

### 0.6 CI (GitHub Actions)

- [x] `build-test.yml` on pushes and PRs: Linux + Windows matrix; `dotnet build`, `dotnet test` on SQLite; Linux job
      also with a `postgres:17` service (`LIBRARION_TEST_POSTGRES`); `npm ci && npm test` in `src/Librarion.Web`.
- [x] `e2e.yml`: quick set (`@smoke`) on pushes to `edge`, all on `main` and by hand; report, traces and app log kept
      on failure; retries 1.
- [x] No workflow publishes anything in stage 0.

### 0.7 Notes and docs

- [x] `agents/docs/architecture/stack.md`, `source-layout.md`, `config-files.md`, `sessions-and-pipeline.md`
      (as built), `agents/docs/environment/commands.md`, `dev-machine.md`, `line-endings.md`;
      `testing-backend.md` / `testing-client.md` / `testing-e2e.md` started with what stage 0 finds.
- [x] `agents/docs/development/database.md` (two providers, migrations per provider, commands), `i18n` rules in
      [frontend](frontend.md), `code-style.md`.
- [x] `docs/`: README, `installation.md` stub (how to run from source), `configuration.md` (provider keys).

## Status (2026-10-31)

Done, with these differences from the items above:

- **PostgreSQL run not verified locally:** Docker on this machine can't pull `postgres:17` (its proxy refuses Docker
  Hub). The provider is wired, its migrations exist, `ModelsOfBothProvidersAgree` and
  `EveryModelChangeHasItsMigrationForBothProviders` pass; the suite on PostgreSQL first runs in CI
  (`build-test.yml`, job `postgres`), or locally once an image is available (`agents/tools/postgres-dev/`).
- **CI not run yet:** the workflows exist; they first run when `edge` is pushed (the user's decision).
- The "no literal text" and "key exists" checks are backend tests (`ClientI18nTests`, reading the templates) instead
  of client specs: no bundler tricks to read sources from a spec.
- The outbound guard listens to the runtime's activity sources (`OutboundGuard`) rather than a failing
  `HttpMessageHandler`: it also sees clients made without `IHttpClientFactory`.
- Found and fixed on the way: deep client addresses loaded scripts relative to the address (`ClientPage`); the brand
  link had no name on phones; the build failed once after every client change (`build.md`).

Measured: backend 19 tests ~3 s, client 15 specs ~12 s, e2e 36 tests (23 run, 13 skipped by design) ~20 s after the
first build.

## Exit criteria

- `dotnet build`, `dotnet test` (SQLite, and Postgres via container), `npm test` green locally and in CI on Linux and
  Windows.
- The app starts on SQLite and on Postgres, shows the shell in EN, light/dark, from 360 px to desktop.
- e2e `@smoke` + `@a11y` green on every project with layout checks; the guard blocks external requests.
- Privacy guards and `EveryRouteNamesARight` / `ModelsOfBothProvidersAgree` in place and green.
- AGENTS.md index lists every note written; links check clean.

## Out of stage 0

Accounts, rights catalog content, tenants (stage 1); catalog schema and generator (stage 2); Docker / portable
packaging (stage 9, a Dockerfile only if CI needs it earlier).
