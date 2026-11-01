# Source layout

```
src/Librarion.Core/        domain, no ASP.NET; DI in ServiceCollectionExtensions (AddLibrarionCore)
  Data/                    LibrarionDbContext (the model) + SqliteLibrarionDbContext / PostgresLibrarionDbContext,
                           DatabaseOptions (provider + connection string), LibrarionDatabase (registration,
                           InitializeAsync, design-time factories)
    Entities/              one class per file, namespace Librarion.Core.Data (ServerProperty)
    Migrations/Sqlite/     SQLite migrations + snapshot
    Migrations/Postgres/   PostgreSQL migrations + snapshot
src/Librarion.Api/         Program.cs, AppVersion, ClientPage (the client's page for deep addresses)
  Endpoints/               one *Endpoints class per area (SystemEndpoints)
  Security/                SecurityHeaders (CSP...), RequestGuard (X-Librarion-Request), RightMetadata
  wwwroot/                 the built client (generated, git-ignored)
src/Librarion.Web/         the Angular workspace (angular.json, package.json, proxy.conf.json)
  public/                  copied as is (favicon.svg)
  src/theme-boot.ts        classic script run before first paint (theme), compiled alone by `npm run boot`
  src/i18n/en.json         the English texts (every UI text is a key here)
  src/styles.css, styles/  global CSS, one partial per area (tokens, base, shell, pages)
  src/testing/             setup.ts (jsdom stand-ins), fakes.ts (mockApi, testProviders, settle, setMediaQuery)
  src/app/core/            Api (the only HTTP client), models, I18n + `t` pipe, ThemeService, preferences
  src/app/shared/          Icon + IconSprite
  src/app/shell/           the frame: top bar, sidebar / phone drawer, page
  src/app/home/            HomePage, NotFoundPage
tests/Librarion.Tests/     MSTest; Infrastructure/ (TestCatalog, ApiClients, SlowWork, WarningLog, OutboundGuard,
                           RepositoryFiles)
tests/e2e/                 Playwright: playwright.config.ts, paths.ts, scripts/server.mjs, fixtures/, specs/
agents/                    notes (docs/) and reusable tools (tools/<job>/ with a README)
docs/                      end-user documentation
```
