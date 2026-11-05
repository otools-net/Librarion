# Design: tokens, palette, the look of every screen

Code: `src/Librarion.Web/src/styles/tokens.css` (tokens per theme), the other partials of `src/styles/`; test
`ClientDesignTests` (contrast). Decisions D-5.1 and D-5.2 [below](#decisions-stage-5-confirmed-by-the-user-2026-11-01).

**The user's rules (2026-11-01):** the interface must not look bare: SFE's approach is the reference (its palette is
not); good solutions for intuitive use, accessibility and visual appeal. Palette chosen: **ink and paper**.

## Approach (ported from SFE's `styles/base.css`)

- Layered surfaces: `--bg` (the page), `--surface` (panels, cards, the sidebar), `--surface-2` (fields, hover,
  chips), `--surface-3` (pressed, selected); `--border` quiet, `--border-strong` for the edges of controls.
- One accent, used sparingly: focus, the current item, the primary action, links; `--accent-soft` as its background
  tint (a selected row, a found setting). `--danger` the same way for what destroys.
- Shadows only on what floats (`--shadow` menus and dialogs, `--shadow-sm` raised cards); flat otherwise.
- Radii: `--radius` 8 px controls, `--radius-lg` 12 px cards and dialogs, `--radius-pill` chips and badges.
- Type scale `--text-xs` … `--text-2xl`; group labels small, upper case, muted (as SFE's sidebar headings).
- Motion `--duration-fast` / `--duration` with `--ease`, set to 0 under `prefers-reduced-motion`; animate transform
  and opacity only.
- System fonts; nothing loaded from elsewhere ([privacy](../development/privacy.md)).

## Palette: ink and paper

- Light: warm paper (`--bg #f5f2ec`, `--surface #fffdf9`), ink text `#1f2328`, ink-blue accent `#2b5885`.
- Dark: deep blue-grey (`--bg #12161b`, `--surface #191e24`), soft blue accent `#8db8e6`.
- Both themes define the same tokens (tested). Contrast (tested, WCAG): every text colour 4.5:1 on the surfaces it
  appears on (`--text` on all four, `--text-muted`/`--accent`/`--danger` on bg, surface, surface-2, the text on accent
  and danger buttons), focus ring and strong borders 3:1. A new colour token gets its pairs in `ClientDesignTests`.
- The first strong border (#b9b0a2) read 2.1:1 on paper: control edges need 3:1 (WCAG 1.4.11).

## Icons

- One sprite of inline SVG symbols (`app/shared/icon.ts`, `IconSprite` once in the shell), used as
  `<svg libIcon="name">`: no icon font, nothing fetched. Drawn on a 24 × 24 grid, `currentColor` strokes 1.8 wide,
  round ends and joins (`.icon`, base.css); 20 px in use. Decorative (`aria-hidden`): the button or link around carries
  the text or the `title`.
- `ICON_NAMES` lists them and types `IconName` (an unknown name doesn't compile); `icon.spec.ts` checks each name has
  its symbol and none is left over, all on the 24 grid.
- Groups: shell, books and fields, actions, navigation, administration, status. A new icon: its name in the list, its
  symbol in the sprite, then a look with the contact sheet ([icon-sheet](../../tools/icon-sheet/README.md)).

## Controls

Code: `styles/forms.css` (controls), `styles/admin.css` (panels, rows, tabs).

- Buttons: secondary by default (framed by `--border-strong`, `--surface`, a light shadow), `.primary` the one main
  action of a view (accent), `.quiet` without a frame (toolbars), `.danger` outlined in the danger colour (destroys;
  still asks first), `.active` a chosen toggle; hover a step up the surfaces, pressed `--surface-3`; disabled half
  transparent. An icon inside is 18 px.
- Fields (every text-like `input`, `select`, `textarea`, wherever they are): `--surface` on a `--border-strong` edge
  (3:1), hover muted, focus the accent edge with a soft 3 px ring; `aria-invalid="true"` turns the edge red.
- `.check` checkboxes 18 px in the accent; `.chip` pills (`.active` / `aria-selected` in the accent tint); `.badge`
  pills with `.accent`, `.success`, `.warning`, `.danger` tints; `.notice` messages; `.card` / `a.card` framed
  blocks with `.card-title`; `.group-label` small upper-case headings (SFE's).
- Panels and row lists are cards (`--radius-lg`, light shadow); a clickable row tints on hover unless it is open.
- Looked at with the screenshots tool in both themes (`LIBRARION_SCREENS_THEME=light`,
  [screenshots](../../tools/screenshots/README.md)).

## Dialogs and feedback

Code: `app/shared/sheet.ts`, `shared/confirm.ts`, `core/toast.service.ts` + `shared/toasts.ts`, `shared/placeholders.ts`;
styles `styles/dialogs.css`, `styles/states.css`. Mounted once in `app.ts` (with the icon sprite), for every page.

- `Sheet` (port of SFE's `SheetComponent`): a modal `<dialog>` with title, close button, scrolling body and a footer
  (`sheetFooter`); Esc and a click that starts and ends on the backdrop close it, focus returns where it was. ≤ 640 px
  it is a sheet from the bottom (full width, grab handle, buttons filling the width). jsdom lacks `showModal`: the
  component falls back to the `open` attribute.
- Never `window.confirm()` / `prompt()`: `ConfirmService.ask({ title, message, action, danger, typeToConfirm })` resolves
  true only on the action button; `danger` puts the focus on Cancel; `typeToConfirm` keeps the button off until the
  text is typed (deleting a tenant). Tests: `vi.spyOn(ConfirmService.prototype, 'ask').mockResolvedValue(true)`.
- `ToastService.show()` / `.error()` (port of SFE's): 5 s, errors 10 s, all held while the pointer or focus is on them
  (WCAG 2.2.1), each with Close. The box is a manual popover shown again on each new toast, so it is above an open
  dialog; it stays in the page empty as a `role=status` live region (a region added with its text isn't announced),
  so e2e tests find a status by its text (`getByRole('status').filter({ hasText })`). Use for the outcome of an action
  that leaves no other mark (saved, deleted, a run started); form errors stay next to the form.
- Empty lists: `<li class="row" libEmpty="folder">text</li>` (icon in a disc, then what to do); `class="large"` for a
  page. Loading: `<lib-skeleton [rows] shape="rows|form" [label]>` (`role=status`, `aria-busy`, hidden label read
  once, shimmer off under reduced motion).
- Screenshots tool `#delete` shows a confirmation dialog.

## Decisions (stage 5, confirmed by the user 2026-11-01)

- **D-5.1 Design foundation first:** tokens in `styles/tokens.css` (layered surfaces, two radii, shadow, type scale,
  motion with `prefers-reduced-motion` honoured), an icon sprite of inline SVGs shipped with the client (no icon font,
  nothing fetched), shared components restyled. Every pair of text and background colours ≥ 4.5:1 (AA) in both
  themes, checked by a test over the tokens; focus rings visible in both.
- **D-5.2 Palette:** Librarion's own, not SFE's amber: ink-blue accent on warm paper in light, deep blue-grey with a
  soft blue accent in dark (confirmed explicitly by the user).
