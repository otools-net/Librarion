# End-to-end tests (Playwright, phones first)

Code: `tests/e2e/` (how to run: its [README](../../../tests/e2e/README.md)). Rules: [testing](testing.md). Ported from
SFE's `testing-e2e.md`; its findings about touch, WebKit and timing apply here too as the UI grows.

- `cd tests/e2e && npm run e2e` builds `Librarion.Api` into `<tmp>/librarion-e2e/build` (`--artifacts-path`, so a
  running dev app's DLLs don't block it), starts it on :5191 on a fresh SQLite database (`scripts/server.mjs`), and
  stops it after. `LIBRARION_E2E_SKIP_BUILD=1` reuses the build.
- Devices: `android` (Pixel 7, Edge), `android-small` (360 px), `iphone` (WebKit), `landscape` (800 × 360: wider than
  a phone, so the sidebar shows), `desktop` (only `@desktop` specs; `@desktop-only` never runs on phones).
- The guard (`fixtures/test.ts`) fails a test on console errors, page errors, CSP violations, 5xx, and **any request
  to another origin** (privacy).
- Every phone test that passes ends with the layout check (`expectFitsScreen`, `expectNothingCut`,
  `expectTapTargets`); `test.use({ layoutCheck: false })` with a comment for a state made on purpose.
- No mouse in phone specs (`harness.spec.ts`); a spec shared with the desktop uses `press(locator, isMobile)`
  (`fixtures/press.ts`).
- First browsers on a machine: `npm run browsers` downloads WebKit, Chromium (CI's) and Playwright's own ffmpeg
  (needed to record the failure videos: without it every test fails at `newPage`).
- Its first run found two bugs (2026-10-31): the brand link had no accessible name on phones (its text hidden, the
  logo `alt=""`), and deep client addresses loaded their scripts relative to the address (`ClientPage` now serves the
  page with the app's root as base).
- Times on this machine: the whole suite (36 tests, 5 devices) ~20 s after the first build.
