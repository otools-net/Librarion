# icon-sheet

Draws every icon of the client's sprite (`src/Librarion.Web/src/app/shared/icon.ts`) on a contact sheet, in the light
and dark theme, at 20 px (as used) and 48 px, and saves it as a PNG: a look at new or changed icons without running
the app.

```sh
node agents/tools/icon-sheet/icon-sheet.mjs [out.png]   # default D:/Temp/librarion-icons.png
```

- Needs the Playwright of `tests/e2e` (`npm ci` there) and the system's Edge (as the e2e tests).
- Reads the `<symbol id="icon-…">` elements from the source file; the colours are the ink and paper surfaces.
- What it caught first: the settings icon (rays round a circle) looked like the sun; it is a gear now.
