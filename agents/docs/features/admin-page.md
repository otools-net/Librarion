# The administration page

Code: `app/admin/admin-sections.ts` (the catalog), `admin-page.ts` (`AdminPage`, `adminMatcher`), the sections'
components (`*-section.ts`, `account/account-section.ts`); styles `styles/admin.css`; tests `admin-sections.spec.ts`,
`admin.spec.ts` (`AdminPage` through `RouterTestingHarness`), e2e `admin.spec.ts`, `smoke.spec.ts`. User docs:
`docs/administration/README.md`. Port of SFE's admin page (`admin-page.md` there); the user's rules and D-5.3 / D-5.9 in
the [stage 5 plan](../development/stage-5-browse-plan.md).

- `ADMIN_SECTIONS`: groups (`account`, `libraries`, `people`, `server`) › sections (icon) › sub-pages (rights). A
  sub-page shows with one of its rights, a section with one of its sub-pages (`visibleSections`). Texts:
  `adminGroup.<g>`, `adminSection.<s>` + `.hint`, and for sections of several sub-pages `adminPage.<s>.<p>` + `.hint`
  (`admin-sections.spec.ts` fails on a missing one). The search (chunk 11) reads the same catalog.
- One route (`adminMatcher`: `admin`, `admin/:section`, `admin/:section/:page`) so `AdminPage` and its column stay
  while moving; `@switch` on the section renders its component. The route has no right guard: unknown or hidden
  sections go to `/admin` (replaceUrl), the API checks every call anyway. `/account` redirects to `admin/account`.
- `/admin` alone: the overview (cards by group, chips for sub-pages), or the only section a session sees. Plain
  members see My account only (System › About needs `system.apiDocs` or server settings: `system.info` is everyone's).
- Breadcrumbs: Administration › group (`/admin#admin-group-<g>`, scrolled to by an effect: no router anchor scrolling)
  › section › sub-page; the separator is CSS `content: "\203A" / ""` (silent for screen readers).
- Moving a sub-page: add `'oldSection/oldPage'` to `ADMIN_MOVED`, never remove an entry (bookmarks). The stage 4
  addresses (`admin/users`, `admin/libraries`…) kept their section ids, so they need none.
- Phones (`Media.phone`, `core/media.ts`, ≤ 640 px as the styles): `/admin` is the list of sections (`sectionList`:
  a card of rows with description and chevron, no overview cards); a section hides the list and shows its sub-pages
  as chips (`role=tablist`, arrows/Home/End move and open, the chosen one scrolled into view, `#admin-panel` its
  tabpanel). The breadcrumbs drop the group. Links from the list push history (back returns to it); on wider screens
  section and sub-page links replace it (back leaves the administration, as SFE). The shell's back arrow on a phone in
  a section goes to `/admin` ("Back to the sections of the administration").
- A library's pages: `admin/libraries/:id/:tab` (the matcher takes a 4th segment only there, a numeric id) render
  `LibraryPage` (`library-page.ts`) instead of the section's header: breadcrumbs › library name, `h1` the name, tabs
  as chip links (`LIBRARY_TABS`: folders, runs, import, missing, schedules; `TAB_RIGHTS`: the scan right for all but
  folders, plus import for Import; unknown or hidden tabs go to the first, replaceUrl), then `LibraryPanel [tab]`.
  The panel stays while the tab changes (no reload); the scan bar is above every tab. On a phone the shell's arrow
  goes from a library back to `/admin/libraries`.
- e2e: `adminSection(page, title)` (`fixtures/admin.ts`) finds a section link by its exact title: on phones the
  link's text includes the description. `libraryTab(page, name)` presses a library's tab.
- The shell is in its admin frame there ([shell](shell.md)): `.admin-frame .page` has no padding, the column is
  sticky under the top bar with its own scroll, in place of the library's sidebar.
- Gotcha: a Python edit script turned the CSS escape `\203A` into an octal escape (U+0083 + "A"): write CSS escapes
  with the Edit tool or a raw string.
- `ClientI18nTests` skips `@let x = …;` declarations (they looked like literal text).
