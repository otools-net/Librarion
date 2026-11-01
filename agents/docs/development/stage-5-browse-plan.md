# Stage 5: browse and search, with a designed interface (plan)

Roadmap: [foundation-plan](foundation-plan.md) (D5, D15, D22). Goal: **the daily UI, fast on phones and desktops**,
and an interface that looks designed, not bare. Version stays on `edge`; the merge into `main` makes it `0.6.0`.

**The user's rules (2026-11-01):**
- The current design is too bare: take SFE's interface as the reference for the approach (its palette may differ).
- The administration will grow: group it **as SFE does**, on desktops and on phones.
- Look for good design solutions: **intuitive use, accessibility and visual appeal**.
- The administration has a **search that finds settings**, as SFE's.

Reference: SimpleFileExplorer (`C:\workspace\SimpleFileExplorer`): `agents/docs/features/admin-page.md`,
`admin-search.md`, `theme.md`, `development/frontend.md`; code `src/Sfe.Web/src/app/admin/` (`admin-sections.ts`,
`admin-search.ts`), `styles/base.css`, `styles/admin.css`. Ported patterns are named as such in the feature notes.

## What SFE does that Librarion takes (the approach)

- **Layered surfaces:** a page background, surfaces in three steps (`--surface`, `-2`, `-3`), a quiet border, one
  accent used sparingly (focus, current item, primary action), soft shadow only on what floats (menus, dialogs).
  Rounded corners in two sizes (12 px cards, 8 px controls). System fonts, a clear type scale (page title, section
  heading, small upper-case group labels in muted text).
- **Administration as sections:** a sidebar on desktops (sticky, its own scroll) with **groups** (small upper-case
  headings) of **sections** (icon + title); the open section lists its **sub-pages** under it. `/admin` alone is an
  **overview**: a card per section, by group, each card with its icon, description and links to its sub-pages.
  Breadcrumbs (Administration › group › section › sub-page). A sub-page has a description under its heading.
- **Phones (≤ 760 px):** `/admin` is the **list of sections** (grouped, each with its description and a chevron); a
  section is a page of its own with its sub-pages as **chips** in one sideways-scrolling line (the chosen one scrolled
  into view); back returns to the list.
- **Search in the administration:** a box at the top of the sidebar (on phones, on the list of sections), searching
  the client's own catalog of sections and settings (title, where it is, hint, key, choices) for the sections the
  session sees, no request; every word must match (accents and case ignored), title matches first, the words marked
  in the results. Opening a result goes to its sub-page, scrolls to the setting, focuses it and flashes it once.
- **Settings forms:** settings in titled groups with a hint each; unsaved changes mark their section in the nav and
  a save bar holds Save / Discard; leaving with unsaved changes asks first. Old addresses of moved sub-pages keep
  working (a moved-addresses table, never emptied).
- **Rules from SFE's frontend note** already partly ported ([frontend](frontend.md)), applied to every new screen:
  phone sheets for popups opening low, overflow rows instead of wrapping buttons, tap targets, `title` hints on every
  button, `document:pointerdown` to close popups, battery-friendly polling.

## Decisions for this stage (confirmed by the user, 2026-11-01)

Confirmed explicitly: the "ink and paper" palette (D-5.2), the administration groups as proposed (D-5.3), and the
order: design foundation (5.0), then the administration with its search (5.1), then browse, book page and search.

- **D-5.1 Design foundation first (5.0):** new tokens in `styles/tokens.css` (layered surfaces, two radii, shadow,
  type scale, motion durations with `prefers-reduced-motion` honoured), an **icon sprite** of inline SVGs shipped with
  the client (no icon font, nothing fetched), shared components restyled: buttons (primary, secondary, quiet, danger),
  fields, selects, chips, cards, badges, empty states, skeleton placeholders while loading, toasts. Every pair of text
  and background colours ≥ 4.5:1 (AA) in both themes, checked by a test over the tokens; focus rings visible in both.
- **D-5.2 Palette:** Librarion's own, not SFE's amber: a calm "library" look, ink-blue accent on warm paper in light,
  deep blue-grey with a soft blue accent in dark (as today, refined); the user may choose another direction.
- **D-5.3 Administration as SFE:** `admin-sections.ts` with groups, sections (icon, title, description) and sub-pages
  (each with its rights; a section without a visible sub-page isn't listed); routes `admin/:section/:page`; overview,
  sidebar, breadcrumbs, phone list and chips as above. Proposed groups:
  - **Account:** My account (preferences, password).
  - **Libraries:** Libraries (list, folders, scans and schedules, imports, missing books), Library settings.
  - **People and access:** Accounts, Groups (rights, content filters).
  - **Server** (server administrators): Tenants, Server settings, Security log, System (version, database).
  The library panel's long page becomes sub-pages of a library (Folders, Runs, Import, Missing books, Schedules).
- **D-5.4 Settings search:** client-side over `admin-sections.ts` × the settings catalog the server already sends
  (`api/settings` definitions with their texts from `en.json`); a test checks every setting key is found.
- **D-5.5 Browse:** the home page becomes the library: a virtual grid of covers (or a dense list), sort and view
  remembered per browser, keyset pages loaded while scrolling, the scroll restored on back. Field pages (authors,
  series, categories, languages, publishers, custom fields) and tags, each value with its count.
- **D-5.6 Book page:** cover, title, authors, series and number, fields as chips linking to their pages, description,
  properties (imported custom columns), copies and formats with **Download** (Read comes with the reader stage),
  possible duplicates (from the merge work, read only here).
- **D-5.7 Search language:** `field:value` for every field, `tag:` with descendants, `format:`, `-` negation, quoted
  phrases, hidden tags honoured; parsed on the server into the same keyset queries; suggestions as you type (fields,
  then values by prefix through the `(TenantId, FieldId, NormalizedName)` index).
- **D-5.8 Facets with capped counts:** counts from `TagCounts` when nothing narrows the list; within a search, counted
  over at most the first 10,000 matches and shown "1000+" past the cap (never a count over the catalog per request).

## Todo (the order of work; the user's rule, 2026-11-01: one chunk at a time, commit it, then ask before the next)

A session may end early: this list is the state of the work. Tick a chunk when it is committed. Chunks are commits on
`edge` only: the version is bumped once, when the stage is complete and merged (the user, 2026-11-01).

1. [x] Tokens: layered surfaces, radii, shadow, type scale, motion (reduced motion honoured), ink and paper palette;
       contrast test over the tokens (both themes, AA).
2. [x] Icon sprite (inline SVG, shipped) and the icon component; existing icons moved to it.
3. [ ] Controls restyled: buttons (primary, secondary, quiet, danger), fields, selects, checkboxes, chips, badges,
       cards, focus rings.
4. [ ] Dialogs as phone sheets, toasts, empty states, skeleton placeholders.
5. [ ] The shell: top bar (library switcher, search box, account menu), desktop sidebar, phone menu sheet.
6. [ ] Administration structure: `admin-sections.ts` (groups, sections, sub-pages, rights), routes
       `admin/:section/:page`, overview cards, sidebar, breadcrumbs, moved-addresses table.
7. [ ] Administration on phones: list of sections, section pages with chips, back to the list.
8. [ ] Library pages split into sub-pages (folders, runs, import, missing books, schedules).
9. [ ] Settings forms in groups with hints, save bar, unsaved marks, leave guard.
10. [ ] Administration search (normalize, every word, marks, reveal and flash; every key found).
11. [ ] Browse: library grid and list, sort and view, keyset scrolling, scroll restored.
12. [ ] Field and tag pages with counts.
13. [ ] Book page: cover, metadata, fields as links, properties, files with Download, possible duplicates.
14. [ ] Search: parser and server query, results page, suggestions.
15. [ ] Facets with capped counts.
16. [ ] Docs, CHANGELOG, feature notes; p95 budgets through the UI's calls; e2e on every device; axe AA; close the
        stage (merge 0.6.0).

## Work items

### 5.0 Design foundation
- [ ] Tokens, type scale, motion, icon sprite; restyle of shell, buttons, fields, chips, cards, badges, dialogs as
      phone sheets, empty states, skeletons, toasts; contrast test over the tokens (both themes).
- [ ] The shell: top bar with the library switcher and search, account menu; sidebar on desktops, bottom-sheet menu
      on phones.

### 5.1 Administration as SFE, with search
- [ ] `admin-sections.ts`, overview cards, sidebar with groups and sub-pages, breadcrumbs, phone list and chips,
      moved-addresses table; the existing sections moved in (accounts, groups, libraries, tenants, settings, security
      log) and the library panel split into sub-pages.
- [ ] Settings forms in groups with hints, save bar, unsaved marks, leave guard.
- [ ] Administration search (port of `admin-search.ts`: normalize, every word, marks, reveal and flash); spec: every
      setting key found.

### 5.2 Browse
- [ ] Library grid / list, sort, view, keyset scrolling, scroll restored; field and tag pages with counts.

### 5.3 Book page
- [ ] Cover, metadata, fields as links, properties, files with Download, possible duplicates.

### 5.4 Search
- [ ] Parser and query (server), suggestions, results page, facets with capped counts.

### 5.5 Docs, tests, budgets
- [ ] User docs (`docs/user-guide/browsing.md`, `searching.md`, `docs/administration/README.md` with finding a
      setting), CHANGELOG, feature notes (`admin-page`, `admin-search`, `design`, `browse`, `search`).
- [ ] p95 budgets through the UI's API calls on the 2M catalog; e2e on every phone project and desktop with layout
      checks; axe AA on the main screens (library, book page, search, administration overview and a settings page).

## Exit criteria

The administration grouped as SFE's on desktops and phones, with a working settings search; the library browsed,
searched and a book downloaded from its page on every device; p95 budgets met through the UI's calls on 2M books;
axe AA clean on the main screens in both themes; contrast test green.
