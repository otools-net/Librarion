# Design: tokens, palette, the look of every screen

Code: `src/Librarion.Web/src/styles/tokens.css` (tokens per theme), the other partials of `src/styles/`; test
`ClientDesignTests` (contrast). Plan: [stage 5](../development/stage-5-browse-plan.md).

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

