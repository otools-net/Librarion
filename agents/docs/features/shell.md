# The shell: top bar, navigation, menus

Code: `app/shell/shell.ts` + `shell.html`, `app/core/library-context.ts`, `styles/shell.css`; tests `shell.spec.ts`,
e2e `smoke.spec.ts`. User docs: `docs/user-guide/finding-your-way.md`. Plan: [stage 5](../development/stage-5-browse-plan.md)
(D-5.9).

- Top bar: menu button (phones) or back arrow (administration), brand, the place (library switcher, or "Administration"),
  search (disabled until the search chunk), account menu (an initial in a disc). On phones the brand's name and the
  switcher go away; the libraries are a line of chips at the top of the navigation sheet instead (scrolling sideways,
  the chosen one scrolled into view: a list pushed the navigation out of the sheet once the e2e tests had made ten).
- `LibraryContext` holds the session's libraries (`GET api/libraries`: browsable ones, all for managers) and the one
  being browsed (`librarion.library` in localStorage; an unreachable id falls back to the first). The shell loads it
  when the account or tenant changes and the session has browse or library management; `LibrariesSection` reloads it
  after adding, renaming or deleting. Browse, search and book pages read `current()`.
- Account menu: name and tenant, My account, Administration (any `ADMIN_RIGHTS`), theme toggle (this browser only),
  tenants (server administrators with several; `aria-pressed` marks the current), Sign out. Menus are disclosure
  panels (`aria-expanded` on the button, `role=group`), not ARIA menus: Tab moves through them.
- One `popup` signal: `nav` | `account` | `library`. Closed by Esc, navigation, a choice, a press outside its
  `[data-popup]` anchor (`document:pointerdown`, SFE's rule) or the backdrop.
- Sidebar (wider than 640 px): sticky under the top bar with its own scroll, groups with `.group-label`s, pages not
  built yet shown with a **Soon** badge (`aria-disabled`), Administration alone at the foot (D-5.9: no other admin entry).
- Phones: the navigation and the menus are sheets from the bottom (grab handle, 44 px rows, backdrop, page held).
  Gotcha: the top bar is sticky with a z-index, so its menus can't rise above a backdrop outside it: the menus'
  backdrop is rendered inside the top bar, the navigation's in `.layout`.
- Administration (`/admin…`, `.admin-frame`): no sidebar, switcher, search or menu button; the back arrow returns to
  the last library page seen (else `/`). Its own left column comes with the administration structure (chunk 6).
- Screenshots tool: `#account`, `#library`, `#menu` open them.
