# The administration page

Code: `app/admin/admin-sections.ts` (the catalog), `admin-page.ts` (`AdminPage`, `adminMatcher`), the sections'
components (`*-section.ts`, `account/account-section.ts`); styles `styles/admin.css`; tests `admin-sections.spec.ts`,
`admin.spec.ts` (`AdminPage` through `RouterTestingHarness`), e2e `admin.spec.ts`, `smoke.spec.ts`. User docs:
`docs/administration/README.md`. Port of SFE's admin page (`admin-page.md` there); the user's rules, D-5.3 and D-5.9 under
[Decisions](#decisions-stage-5-confirmed-by-the-user-2026-11-01).

- `ADMIN_SECTIONS`: groups (`account`, `libraries`, `people`, `server`) › sections (icon) › sub-pages (rights). A
  sub-page shows with one of its rights, a section with one of its sub-pages (`visibleSections`). Texts:
  `adminGroup.<g>`, `adminSection.<s>` + `.hint`, and for sections of several sub-pages `adminPage.<s>.<p>` + `.hint`
  (`admin-sections.spec.ts` fails on a missing one). The search reads the same catalog ([admin-search](admin-search.md)).
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
- Unsaved settings: the open section and sub-page carry a dot (`admin-mark`) while `UnsavedChanges.dirty()`; the
  route's `canDeactivate: [leaveWithUnsaved]` asks before leaving ([settings](settings.md#the-forms-client)).
- e2e: `adminSection(page, title)` (`fixtures/admin.ts`) finds a section link by its exact title: on phones the
  link's text includes the description. `libraryTab(page, name)` presses a library's tab.
- The shell is in its admin frame there ([shell](shell.md)): `.admin-frame .page` has no padding, the column is
  sticky under the top bar with its own scroll, in place of the library's sidebar.
- Gotcha: a Python edit script turned the CSS escape `\203A` into an octal escape (U+0083 + "A"): write CSS escapes
  with the Edit tool or a raw string.
- `ClientI18nTests` skips `@let x = …;` declarations (they looked like literal text).

## Decisions (stage 5, confirmed by the user 2026-11-01)

- The user's rules: the administration will grow, so it is grouped **as SFE's**, on desktops and on phones; it has a
  search that finds settings ([admin-search](admin-search.md)); good solutions for intuitive use, accessibility and
  visual appeal.
- **D-5.3 Administration as SFE:** `admin-sections.ts` with groups, sections (icon, title, description) and sub-pages
  (each with its rights; a section without a visible sub-page isn't listed); routes `admin/:section/:page`; an
  overview of cards by group, the sidebar with the open section's sub-pages, breadcrumbs (Administration › group ›
  section › sub-page); on phones (≤ 760 px) the list of sections, then a section with its sub-pages as chips. Groups:
  Account, Libraries, People and access, Server. A library's long panel became sub-pages (Folders, Runs, Import,
  Missing books, Schedules). Old addresses of moved sub-pages keep working (the moved-addresses table, never
  emptied).
- **D-5.9 A page of its own (the user):** `/admin` replaces the library's sidebar with its own left column (search
  box, groups, sections, the open section's sub-pages), as SFE's `AdminPageComponent`; the top bar keeps only a back
  arrow to the library (history back when it came from there) and the account menu. The library's sidebar shows one
  link to it, no administration entries.
- Settings forms: settings in titled groups with a hint each; unsaved changes mark their section in the nav, a save
  bar holds Save / Discard, leaving with unsaved changes asks first.
