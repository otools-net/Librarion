# Build

Code: `src/Librarion.Api/Librarion.Api.csproj` (targets `LibrarionWebInstall`, `LibrarionWebBuild`,
`LibrarionWebContent`), `src/Librarion.Web/angular.json`, `package.json`. As SFE's `build.md`.

- The Api build runs `npm ci` (when `package-lock.json` changed) and `npm run build` (when a client source is newer
  than `wwwroot/index.html`) in `src/Librarion.Web`, output into `src/Librarion.Api/wwwroot` (git-ignored). Needs Node
  on the PATH. `-p:LibrarionSkipWebClient=true` skips both.
- `npm run build` first compiles `src/theme-boot.ts` alone (`npm run boot`, `tsconfig.boot.json`) into
  `out-tsc/boot`; `angular.json` copies it next to `index.html`.
- **Fixed (2026-10-31): the build after a client change failed once in `DefineStaticWebAssets`** ("No file exists for
  the asset … main-XXXX.js"; SFE still has it and builds twice). Cause: the wwwroot `Content` items globbed when the
  project was read were removed inside a target with a wildcard, which matches files on disk and so missed the
  previous build's chunks. `LibrarionWebContent` now removes them by name. Checked: two client changes in a row, each
  built once.
- `NG_CLI_ANALYTICS=false` is set for the npm steps; `angular.json` has `cli.analytics: false`
  (`TheAngularCliSendsNoAnalytics`).
- Initial bundle budget 500 kB warning / 1 MB error (`angular.json`); stage 0: ~260 kB raw, ~72 kB transferred.
- `inlineCritical: false`: Angular's critical-CSS inlining adds an `onload` handler that the CSP forbids.
