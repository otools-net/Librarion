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
LIBRARION_SCREENS_THEME=light node agents/tools/screenshots/screenshots.mjs   # light colors (default dark)
```

- A page is a client address; `#recover` opens the recovery on the sign-in page, `#new` presses the section's "New …"
  button, `#edit` its first "Edit" button, `#open` its first row (a library's pages), `#delete` its first Delete button (the
  confirmation dialog), `#account` the account menu, `#library` the library switcher (desktops), `#menu` the navigation sheet
  (phones), `#search=<words>` types them in the administration's search box (`%20` for spaces), `#found=<words>` also
  opens the first result (a setting: the shot catches its flash), `#scan` presses the first "Run now" (Background tasks:
  the progress panel shows the run, or its result), `#list` switches the library to its list view (`""#list`; the
  address `""` is the library), `#first` opens the first value of a list (`fields/author#first`: an author's page), `#book` the library's first book (`""#book`, a full-page shot). `#suggest=<words>` types them in the top bar's search (its suggestions open); a page may carry a query (`search?q=book`). Shots with an action are the viewport, not the full page. The setup adds a second library, "Comics", so
  the switcher shows. Before
  the pages, a folder of three FB2s is added to the library and scanned. Files are named `<phone|desktop>-<page>.png`.
- It reuses the last e2e build by default (`LIBRARION_E2E_SKIP_BUILD=1`); without one it builds. The client is
  served from the source tree's `wwwroot` but the server from that build: after a server change, shoot with
  `LIBRARION_E2E_SKIP_BUILD=0` (a stale server once hid the book page's Download, `canDownload` missing).
