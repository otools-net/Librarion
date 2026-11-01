# Screenshots

The app's pages on a phone (360 px) and a desktop (1280 px), full-page PNGs, dark colors, to look at a layout by eye
(the e2e layout checks catch overflow and small targets, not ugliness). Starts its own e2e server
(`tests/e2e/scripts/server.mjs`, fresh SQLite database, port 5192, work folder `<tmp>/librarion-screens-server`), sets
it up through the API and signs in as its administrator; `login` pages are taken signed out.

```sh
cd tests/e2e && npm ci && cd ../..                    # once: the e2e tests' Playwright
node agents/tools/screenshots/screenshots.mjs         # every page into <tmp>/librarion-screens
node agents/tools/screenshots/screenshots.mjs <out> account admin/users#new   # some pages
LIBRARION_E2E_SKIP_BUILD=0 node agents/tools/screenshots/screenshots.mjs      # rebuild the app first
```

- A page is a client address; `#recover` opens the recovery on the sign-in page, `#new` presses the section's "New …"
  button, `#edit` its first "Edit" button. Files are named `<phone|desktop>-<page>.png`.
- It reuses the last e2e build by default (`LIBRARION_E2E_SKIP_BUILD=1`); without one it builds.
