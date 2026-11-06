# End-to-end tests (Playwright, phones first)

Code: `tests/e2e/` (how to run: its [README](../../../tests/e2e/README.md)). Rules: [testing](testing.md). Ported from
SFE's `testing-e2e.md`; its findings about touch, WebKit and timing apply here too as the UI grows.

- `cd tests/e2e && npm run e2e` builds `Librarion.Api` into `<tmp>/librarion-e2e/build` (`--artifacts-path`, so a
  running dev app's DLLs don't block it), starts it on :5191 on a fresh SQLite database (`scripts/server.mjs`), and
  stops it after. `LIBRARION_E2E_SKIP_BUILD=1` reuses the build.
- The server gets a known setup code (`paths.ts`), and its setup-code file and cookie keys go to `<work>/data`: the
  content root is `src/Librarion.Api`, so without them it wrote into the repo's own `data/` (fixed 2026-11-01). Its
  sign-in limit is raised (every test signs in from one address).
- Project `first-start` (`specs/first-start.setup.ts`, 360 px phone) runs before every other (`dependencies`, kept
  by `--grep` too): the setup page makes the administrator; its sign-in (`<work>/auth/admin.json`) is every spec's
  `storageState`. Signed out: `test.use({ storageState: signedOut })`.
- A test that signs in or out, or changes an account, makes its own (`fixtures/accounts.ts` `makeAccount`, names from
  `uniqueName`): all devices run at once against one server. Signing out of the shared administrator would end
  nothing else (cookies only), but a password change or recovery would.
- The recovery spec reads the code from `<work>/data/recovery-code.txt`, as an administrator reads the console.
- `webServer` waits on `api/setup` (anonymous; everything else answers 503 until the setup).
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
- Books for a spec are written by the spec: `fixtures/zip.ts` writes a ZIP (deflated, `node:zlib`) and FB2s, so no
  binary sample is committed; each device uses its own folder under `<work>/books/<unique name>`.
- Times on this machine: the whole suite (47 tests run, 6 projects) ~1.4 min after the first build.
- Screenshots to look at by eye: [screenshots tool](../../tools/screenshots/README.md).
- A test's own library: `fixtures/library.ts` `scannedLibrary(page, name, write)` writes the folder, adds and scans it
  through the API and chooses it. `a11y.spec` makes its own book (it runs first: other tests' books aren't there yet).
- After typing in "Go to", wait for `?from=` in the address before pressing a row: pressed earlier, the jump's own
  navigation came after and took the page back to the list (2026-11-03).
- Downloads: `page.waitForEvent('download')` before the press, then `download.path()`: works on every project,
  WebKit included.
- `getByRole('option')` also finds the sort menu's `<option>`s (hidden): scope it to `#search-suggestions`.
- The search box's options cancel `mousedown` to keep the focus in the box: cancelling `pointerdown` instead made iOS
  WebKit drop the tap's `click` (found by `search.spec.ts` on `iphone`).
- The final layout check measures the page as the test leaves it: a test that ends scrolled (a row further down as the
  run's other specs add libraries) can put a small link against the sticky top bar's buttons. End such a test scrolled
  to the top; targets covered at their centre are not measured (`expectTapTargets`).
- Two Ctrl-clicks on links in a row (`click({ modifiers: ['ControlOrMeta'] })`) made headless Chromium drop every
  later input now and then (2 runs in 5 under load: no event reached the page at all, though the app prevents the
  default): `selection.spec.ts` Ctrl-clicks one book and takes the next by its box; the client tests cover more.
- After a press that opens another page, wait for its address (`toHaveURL`) or scope the check to its component
  (`lib-book-page .chip`): a check the page left behind also passes (a tag chip in the folder's default tags line) let
  `goBack` run before the book opened, and it left the folder instead (`folders.spec.ts`, 2026-11-06).
- A `page.goto` right after `scannedLibrary` cuts off the library page's own calls (`api/fields`, `api/books`, facets),
  which WebKit reports as "access control" page errors: wait for a book of it and `networkidle` first
  (`imported-tags.spec.ts`, 2026-11-06).
- A tag change (merge, ignore, an undo) answers 202 with a run whenever any run of the tenant goes, and in a full run
  the other devices' scans always do: assert what the list shows once it has followed the run, never the inline toast.
