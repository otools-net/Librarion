# End-to-end tests (phones first)

The real app, built from this checkout, in real browsers sized and driven like phones (touch, Android Chromium, iOS
WebKit) and on a desktop. Notes: `agents/docs/development/testing-e2e.md`.

## Run

Needs Node 22+, the .NET SDK, and Microsoft Edge on Windows (elsewhere Playwright's Chromium).

```sh
cd tests/e2e
npm ci                        # once, and after package.json changes
npm run browsers              # once: WebKit (iPhone), Chromium, and the ffmpeg that records failure videos

npm run e2e                                   # everything: builds the app, starts it, runs, stops it
npm run e2e -- --project=android              # one device: android, android-small, iphone, landscape, desktop
npm run e2e -- --grep "@smoke|@a11y|@accounts|@admin"   # the quick set (CI on edge: android, iphone, desktop)
npm run e2e -- specs/smoke.spec.ts            # one file
LIBRARION_E2E_SKIP_BUILD=1 npm run e2e        # reuse the last build
npm run typecheck                             # Playwright runs specs without checking their types
npx playwright show-report <tmp>/librarion-e2e/report   # the HTML report, traces and videos of failures
```

| Environment | |
|---|---|
| `LIBRARION_E2E_WORK` | work folder (build, database, report, traces); default `<tmp>/librarion-e2e` |
| `LIBRARION_E2E_PORT` | the app's port (default 5191) |
| `LIBRARION_E2E_SKIP_BUILD=1` | use the build already in the work folder |
| `LIBRARION_E2E_CHANNEL` | Chromium channel (default `msedge` on Windows) |
| `LIBRARION_E2E_WORKERS` | parallel workers (default half the cores) |

## Layout

| Path | |
|---|---|
| `playwright.config.ts` | devices (projects), server, reports |
| `paths.ts` | work folder, port, data folder, setup code, the administrator and its saved sign-in |
| `scripts/server.mjs` | build + app on a fresh database with a known setup code, its data folder in the work folder, run by Playwright's `webServer` |
| `specs/first-start.setup.ts` | project `first-start`, before every other: the setup page makes the administrator; its sign-in is saved for the specs |
| `fixtures/test.ts` | the `test` to import: fails on console errors, page errors, CSP violations, 5xx, requests to other origins (`test.use({ allowedErrors: /…/ })` for expected ones); the final layout check on phones |
| `fixtures/layout.ts` | `expectFitsScreen`, `expectNothingCut`, `expectTapTargets` (≥ 24 px), `expectNoZoomOnFocus` |
| `fixtures/press.ts` | `press(locator, isMobile)`: tap on phones, click on the desktop |
| `fixtures/accounts.ts` | `makeAccount` (a new account per test, through the API as the administrator), `asAdmin`, `uniqueName`, `signIn` (through the page) |
| `fixtures/axe.ts` | `audit(page, screen)`: axe WCAG 2.2 AA, serious and critical fail |
| `fixtures/opds.ts` | `ReaderApp` (HTTP only: Basic, or nothing for a key in the address), `parseFeed` (titles and links of an Atom feed), `entryOf`, `linkOf` |
| `specs/` | `smoke` (`@smoke`), `a11y` (`@a11y`), `accounts` (`@accounts`: sign-in, sign-out, recovery), `admin` (`@admin`: account, group, right used), `opds` (`@opds`: the catalog as a reader app, desktop project only), `harness` |

## Writing a spec

- Import `test`, `expect` from `fixtures/test`, never from `@playwright/test`.
- Specs start signed in as the administrator; `test.use({ storageState: signedOut })` for a visitor. A test that
  signs in, out or changes an account makes its own (`makeAccount`, `uniqueName`): every device runs at once.
- Phones are touched (`tap`); `click()` only in desktop code paths with a `// mouse: <why>` comment.
- Wait on states (`expect(...).toBeVisible()`), never on time.
- Tag what runs on the desktop too `@desktop`, desktop only `@desktop-only`; the quick set `@smoke`.
- An important flow gets its spec in the same change as the flow ([testing](../../agents/docs/development/testing.md)).
