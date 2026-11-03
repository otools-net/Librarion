# Stage 5: browse and search, with a designed interface (plan)

Roadmap: [foundation-plan](foundation-plan.md) (D5, D15, D22). Goal: **the daily UI, fast on phones and desktops**,
and an interface that looks designed, not bare. Version stays on `edge`; the merge into `main` makes it `0.6.0`.

**The user's rules (2026-11-01):**
- The current design is too bare: take SFE's interface as the reference for the approach (its palette may differ).
- The administration will grow: group it **as SFE does**, on desktops and on phones.
- Look for good design solutions: **intuitive use, accessibility and visual appeal**.
- The administration has a **search that finds settings**, as SFE's.
- (Added 2026-11-01) The administration **opens as a page of its own with its own left menu column**, as SFE's (not
  inside the library's sidebar).
- (Added 2026-11-01) The **task scheduler is redesigned**: a schedule must say clearly what it runs (a folder scan, a
  Calibre import, an INPX import), not only when.

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
- **D-5.9 The administration is a page of its own (the user, 2026-11-01):** `/admin` replaces the shell's library
  sidebar with its own left column (search box, groups, sections, the open section's sub-pages), as SFE's
  `AdminPageComponent`; the top bar keeps only a back arrow to the library (history back when it came from there) and
  the account menu. The library's sidebar (chunk 5) never shows administration entries beyond one link to it.
- **D-5.10 Schedules say what they run (the user, 2026-11-01):** a schedule has a **task** first (Folder scan, Calibre
  import, INPX import, each with its icon), then its own fields (an import: its source file and files folder, checked
  as the import form does), then when (every N hours / days of the week at a time). The list shows each schedule as
  "task · source · when · next run" with its icon and an enabled switch; adding opens a dialog (a phone sheet) in
  steps rather than a one-line toolbar. Server: `TaskSchedule` gains the import's `Source` and `Files` (validated on
  save as `POST …/import` does); a due import schedule starts the same import run.
- **D-5.11 Scan progress as SFE's scan panel (the user, 2026-11-02):** while a library is scanned (or imported) the
  user sees it going and what it found so far, looking like SFE's `ScanPanelComponent`
  (`Sfe.Web/src/app/library/scan/scan-panel.component.ts`, `styles/scan-panel.css`, `scan.service.ts`): a panel
  sticky under the top bar (`aria-live="polite"`), a thin indeterminate bar along its foot (moved by `transform`, not
  `left`; reduced motion honoured), a title ("Scanning Comics…" in accent; finished / cancelled / failed in danger
  colour), the elapsed time ticking each second (only while running and the page is visible), the counts so far
  (files seen, archives, added, changed, missing, removed; for an import its records, added, changed, skipped) and the
  file being read, Cancel while it runs; afterwards the result stays until dismissed (remembered per browser by the
  run's start time) and a toast says it finished. Seen from the library pages and from browsing the library being
  scanned, not only the library's admin tab. Data: the run's `progress` the server already sends (`filesSeen`,
  `archivesDone`, `added`, `changed`, `missing`, `removed`, `returned`, `skipped`, `current`) plus its start and end
  times; Cancel uses the existing `POST …/runs/{run}/cancel` (`CancelTask`). Polling stays as it is (one run's row, not the
  catalog). Replaces the one-line `scan-bar` text in `library-panel.ts`.
  **Also in the administration, as SFE's "Background tasks" (the user, 2026-11-02;
  `Sfe.Web/src/app/tasks/tasks-section.component.ts`):** a section under Tenant and server (`admin/tasks/...`) with
  sub-pages Tasks, Schedules and History; the sub-page chips carry badges (runs going on, number of schedules).
  How SFE shows it (`tasks-section.component.ts`, `styles/tasks.css`, `task-history.component.ts`):
  - A summary line on top, muted (`aria-live="polite"`): "Running: scan of Comics · next: import of Fiction
    tomorrow 03:00", or "Nothing running · nothing planned".
  - A card per task kind (folder scan, Calibre import, INPX import) in a column: a head with a 36 px tinted icon
    tile, the name (h3) and under it the state ("Idle", or "Running · 2:14" in accent, bold, after an 11 px spinner),
    and the actions on the right (Cancel in danger style, asking first; Run now, asking which library; Schedule,
    opening the schedule dialog for that task). Then a muted line on what the task does; while running a 6 px
    rounded bar (a width from the server's `progress` fraction, animated; indeterminate when the fraction is
    unknown, as a scan's is) and a detail line "**Comics:** 12,400 files seen, 3 archives, 1,200 added, …" (the
    server words the counts, the same text the run's summary keeps when it ends). At the foot a `dl` of Last run
    (an outcome pill and "today 03:00", a link opening that run in History) and Next run (bold time, "in 5 h" in
    accent, and which schedule plans it; else "Not planned: only when started by hand"). A running card has an
    accent-tinted border and icon; a task that can't run is dimmed.
  - Schedules: SFE's list (switch, task icon, task and library, when, next and last run, edit and delete), here the
    rows of every library with the dialog of chunk 9.
  - History: the runs newest first across libraries (outcome icon, task, library, trigger, when and how long, its
    summary); a row opens on its log when runs keep one, and a running run's log follows it.
  - Refreshed every 2 s while shown and the page is visible (SFE's `visibleInterval`); the elapsed times tick with
    each refresh. On phones the actions take the card's full width. Server: one route across the tenant's libraries (`GET /api/tasks/overview`: the running
  runs, the latest run of each kind, the schedules with their next times; bounded by the libraries, not the catalog)
  and the history as a keyset page across libraries; only libraries where the account holds `Rights.scan` (or
  `Rights.import` for imports) appear.

## Todo (the order of work; the user's rule, 2026-11-01: one chunk at a time, commit it, then ask before the next)

A session may end early: this list is the state of the work. Tick a chunk when it is committed. Chunks are commits on
`edge` only: the version is bumped once, when the stage is complete and merged (the user, 2026-11-01).

1. [x] Tokens: layered surfaces, radii, shadow, type scale, motion (reduced motion honoured), ink and paper palette;
       contrast test over the tokens (both themes, AA).
2. [x] Icon sprite (inline SVG, shipped) and the icon component; existing icons moved to it.
3. [x] Controls restyled: buttons (primary, secondary, quiet, danger), fields, selects, checkboxes, chips, badges,
       cards, focus rings.
4. [x] Dialogs as phone sheets, toasts, empty states, skeleton placeholders.
5. [x] The shell: top bar (library switcher, search box, account menu), desktop library sidebar, phone menu sheet;
       the administration outside it (D-5.9).
6. [x] Administration structure: a page of its own with its own left column (D-5.9), `admin-sections.ts` (groups,
       sections, sub-pages, rights), routes `admin/:section/:page`, overview cards, breadcrumbs, moved-addresses table.
7. [x] Administration on phones: list of sections, section pages with chips, back to the list.
8. [x] Library pages split into sub-pages (folders, runs, import, missing books, schedules).
9. [x] Schedules redesigned (D-5.10): task first (scan, Calibre import, INPX import) with its fields, server stores
       and runs import schedules, list as task · source · when · next, add/edit in a dialog, enable switch.
10. [x] Settings forms in groups with hints, save bar, unsaved marks, leave guard.
11. [x] Administration search (normalize, every word, marks, reveal and flash; every key found).
12. [x] Scan progress panel as SFE's (D-5.11): sticky panel, indeterminate bar, title, elapsed, counts so far,
        current file, Cancel, result until dismissed, toast when done; shown on library pages and while browsing;
        and the administration's Background tasks section (task cards with progress, schedules and history across
        libraries; overview route on the server).
13. [x] Browse: library grid and list, sort and view, keyset scrolling, scroll restored.
14. [x] Field and tag pages with counts.
15. [x] Book page: cover, metadata, fields as links, properties, files with Download, possible duplicates.
16. [x] Search: parser and server query, results page, suggestions.
17. [ ] Facets with capped counts.
18. [ ] Docs, CHANGELOG, feature notes; p95 budgets through the UI's calls; e2e on every device; axe AA; close the
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
      log) and the library panel split into sub-pages; the page has its own left column (D-5.9).
- [ ] Schedules redesigned: task first, import schedules on the server, dialog to add and edit (D-5.10).
- [ ] Settings forms in groups with hints, save bar, unsaved marks, leave guard.
- [ ] Administration search (port of `admin-search.ts`: normalize, every word, marks, reveal and flash); spec: every
      setting key found.
- [ ] Scan progress panel as SFE's (D-5.11); e2e: a scan started shows the panel with counts, then its result.
- [ ] Background tasks section (D-5.11): overview route (both providers), task cards, schedules and history across
      libraries; e2e: a running scan shows on its card with Cancel.

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
- [ ] The 1 % filtered group's page on PostgreSQL: 127 ms on the catalog generated 2026-11-03 (62 at stage 2), the same with
      the previous commit's code: PostgreSQL estimates the tag at 744 links (20k) and starts from them. Make the start a
      decision of ours as search does (a list when narrow, a per-book test otherwise) and measure ([scale](scale.md#measured)).

## Exit criteria

The administration grouped as SFE's on desktops and phones, with a working settings search; the library browsed,
searched and a book downloaded from its page on every device; p95 budgets met through the UI's calls on 2M books;
axe AA clean on the main screens in both themes; contrast test green.
