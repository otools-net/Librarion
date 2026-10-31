# Frontend: responsive, compact, Angular

Code (planned): `src/Librarion.Web/src/app/`, styles in `src/Librarion.Web/src/styles/`. Priority 3 of the project.
Ported from SFE's `frontend.md` (its phone, touch and overflow rules come from real phone bugs: keep them).

## Responsive and compact

- **One app, every width:** each screen works from a **360 px phone** to a wide desktop; no separate mobile site.
  Breakpoints: phone ≤ 640 px, tablet ≤ 1024 px, desktop above.
- **Compact by default:** dense lists and grids (many books per screen), small paddings, a density setting
  (compact / comfortable) per user; covers sized by the grid, not fixed.
- Book lists are **virtual** (only visible rows in the DOM) with keyset paging behind: a 2M-book list scrolls like a
  short one.
- Rows of buttons that may not fit a phone are **overflow rows** (one line, the rest in a "⋯" menu), never wrapped or
  cut.
- On phones, menus and popups from the lower half are **bottom sheets** (full width, safe-area padded, backdrop);
  dialogs become full-width sheets.
- Touch: tap targets ≥ 24 px (44 preferred), text fields ≥ 16 px on touch screens (no iOS zoom), close-on-outside on
  `pointerdown`, long press for the item menu; nothing needs hover.
- The logo is the "portico of spines" (a temple front whose columns are book spines): `shared/logo.ts` inline, its
  blue from `--accent` so it follows the theme; `public/favicon.svg` is its simple form (roof, three spines, step),
  `public/logo.svg` the mark for the README. Drawn from scratch, no font, nothing from elsewhere.
- Light and dark themes, set before first paint; system fonts or bundled ones (no web font from outside:
  [privacy](privacy.md)).
- Accessibility: WCAG 2.2 AA checked by axe in e2e on the main screens.

## Languages (i18n)

- Every UI string goes through translation keys (`src/i18n/<lang>.json`, runtime, no rebuild per language); English
  ships first. A spec fails on a literal string in a template and on a key missing from `en.json`.
- Dates, numbers and sizes through `Intl` with the UI language; sort and search are culture-aware on the server.
- Labels must fit longer languages: no fixed widths on text, overflow rows handle the rest.

## Angular conventions (as SFE)

- Standalone components, `ChangeDetectionStrategy.OnPush`, signals, zoneless, `input()`/`output()`, `inject()`,
  built-in control flow, `strict` + `strictTemplates`.
- Only `core/api.service.ts` makes HTTP requests; URLs stay relative (virtual directory).
- Global CSS in `src/styles/` (one partial per area), no component styles.
- The client hides what a right forbids (`config.can(...)`).
- Every button has a `title` saying what it does.
- Anything polling stops while the tab is hidden (battery).
