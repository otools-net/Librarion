# Stack

Code: `Librarion.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `src/Librarion.Web/package.json`,
`tests/e2e/package.json`. Design reference: SFE (`C:\workspace\SimpleFileExplorer`).

| Area | Stack |
|---|---|
| Runtime | .NET 10 (SDK pinned by `global.json`, roll forward to the latest feature band), C# latest, `Nullable` + `ImplicitUsings`; package versions only in `Directory.Packages.props` |
| Web | ASP.NET Core minimal APIs (`MapGroup`, `TypedResults`), ProblemDetails, enums as camelCase strings |
| API docs | `Microsoft.AspNetCore.OpenApi` at `/openapi/v1.json` (no Swagger UI yet) |
| Data | EF Core 10, **SQLite** (`Microsoft.EntityFrameworkCore.Sqlite`) and **PostgreSQL** (`Npgsql.EntityFrameworkCore.PostgreSQL`), migrations per provider ([database](../development/database.md)) |
| Frontend | **Angular 22** + TypeScript 6 in `src/Librarion.Web`: standalone, signals, zoneless, OnPush, `HttpClient` (fetch), router; global CSS; runtime translation keys; built into `src/Librarion.Api/wwwroot` (git-ignored) |
| Tests | MSTest 4 (method-level parallelism), `WebApplicationFactory<Program>`; client: Vitest 5 via `ng test` (jsdom); e2e: Playwright 1.64.1 (pinned exactly) + axe-core |
| Archives | own ZIP directory reader (`Core/Containers/`, ported from SFE); SharpCompress for other ZIP methods and 7z / RAR wrappers; `System.IO.Hashing` (CRC-32, XxHash64) |
| Images | **SkiaSharp** + **HarfBuzzSharp** via `SkiaSharp.HarfBuzz` (MIT, no telemetry; D17): decode, resize, JPEG (every cover since stage 7), generated covers with shaped text; Linux assets `NativeAssets.Linux.NoDependencies` (no fontconfig) for x64 / arm64, glibc and musl. Fonts bundled from Core into `fonts/` of the output (Noto Sans, Sans Bold, Serif Bold; Sans Arabic, Hebrew, Devanagari, Bengali, Tamil, Thai Bold; Sans CJK SC Bold; SIL OFL, `Core/Covers/Fonts/OFL*.txt`; about 19 MB, 17 of them CJK) |
| CI | GitHub Actions: `build-test.yml` (Linux/Windows, SQLite and PostgreSQL), `e2e.yml` |
| Hosting | Kestrel `:5090` by default (`appsettings.json` `Urls`); packaging comes in stage 11 |

External references:

- Angular: https://angular.dev/
- EF Core providers: https://learn.microsoft.com/ef/core/providers/, https://www.npgsql.org/efcore/
- Playwright: https://playwright.dev/, axe-core: https://github.com/dequelabs/axe-core
