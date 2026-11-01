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
- The shell is in its admin frame there ([shell](shell.md)): `.admin-frame .page` has no padding, the column is
  sticky under the top bar with its own scroll, in place of the library's sidebar.
- Gotcha: a Python edit script turned the CSS escape `\203A` into an octal escape (U+0083 + "A"): write CSS escapes
  with the Edit tool or a raw string.
- `ClientI18nTests` skips `@let x = …;` declarations (they looked like literal text).
