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
npm run e2e -- --grep "@smoke|@a11y"          # the quick set (CI on edge: android, iphone, desktop)
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
| `paths.ts` | work folder, port |
| `scripts/server.mjs` | build + app on a fresh database, run by Playwright's `webServer` |
| `fixtures/test.ts` | the `test` to import: fails on console errors, page errors, CSP violations, 5xx, requests to other origins (`test.use({ allowedErrors: /…/ })` for expected ones); the final layout check on phones |
| `fixtures/layout.ts` | `expectFitsScreen`, `expectNothingCut`, `expectTapTargets` (≥ 24 px), `expectNoZoomOnFocus` |
| `fixtures/press.ts` | `press(locator, isMobile)`: tap on phones, click on the desktop |
| `specs/` | `smoke` (`@smoke`), `a11y` (`@a11y`, axe WCAG 2.2 AA), `harness` |

## Writing a spec

- Import `test`, `expect` from `fixtures/test`, never from `@playwright/test`.
- Phones are touched (`tap`); `click()` only in desktop code paths with a `// mouse: <why>` comment.
- Wait on states (`expect(...).toBeVisible()`), never on time.
- Tag what runs on the desktop too `@desktop`, desktop only `@desktop-only`; the quick set `@smoke`.
- An important flow gets its spec in the same change as the flow ([testing](../../agents/docs/development/testing.md)).
