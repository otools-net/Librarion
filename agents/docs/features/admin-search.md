# The administration's search

Code: `app/admin/admin-search.ts` (`searchAdmin`, `markWords`, `settableAt`), `admin/marked-text.ts`, the box and
results in `admin/admin-page.ts`, `settings/setting-reveal.ts` and its use in `settings/settings-panel.ts`;
`GET api/settings/catalog` in `Api/Endpoints/SettingsEndpoints.cs` (`SettingInfo`). Styles at the end of
`styles/admin.css`. Tests `admin-search.spec.ts`, `admin.spec.ts`, `SettingsTests` (catalog route, the fakes' catalog
is the server's), e2e `admin.spec.ts`. Port of SFE's `admin-search.ts`; D-5.4 under
[Decisions](#decisions-stage-5-confirmed-by-the-user-2026-11-01).

- Looks in the sections the session sees (`visibleSections`), their sub-pages and, on each sub-page with a
  `settings` level (`settingsPage(...)` in `admin-sections.ts`: account/preferences = user, librarySettings = library,
  settings/tenant, settings/server), every key that level may set (`settableAt`: the level is at or above the key's
  `lowestLevel`). So a user-level key is found on four pages, a library-level one on three.
- Matches every word (`searchWords`: NFD, marks removed, lower case) in the title, where, hint, the key and its
  choices' texts; titles holding every word come first. At most `ADMIN_SEARCH_LIMIT` (40) listed, the count says how
  many were found.
- Texts come from `en.json` through `I18n.t` (`setting.<key>`, `.hint`, `.<choice>`, `settingSection.<s>`, the
  sections' texts): `searchAdmin` takes the translate function, so specs pass the raw `en.json`.
- The catalog (keys, sections, choices, `lowestLevel`; no values) is read once, when the box is first focused or
  typed in, and only when the session sees a settings page. Every level lists every key (`editable` says where it can
  be set), which is why the client needs `lowestLevel` from the catalog route.
- Picking a setting: `SettingReveal.request(key, level)`, then the link navigates. The `SettingsPanel` of that level,
  once its states hold the key, scrolls the row into view, focuses the control and flashes the row (`setting-found`,
  restarted when picked again). The level check keeps another level's panel (reused while the route changes) from
  taking the request.
- Enter opens the first result, Esc clears the box; the results replace the groups in the column (on phones, above
  the list of sections). On wider screens the query stays while the page changes (the route reuses `AdminPage`).
- `testing/fakes.ts` `settingCatalog()` mirrors `SettingCatalog.All` (`TheClientsFakeCatalogIsTheServers` fails
  otherwise): a new key goes in both.
- Phones: the result links override the section rows' grid (`.admin-nav .admin-search-results a`: a column).
- `<mark>` uses `--mark-bg`; each word is marked on its own ("Books", "per", "page").

## Decisions (stage 5, confirmed by the user 2026-11-01)

- **D-5.4 Settings search:** in the client, over `admin-sections.ts` × the settings catalog the server sends (with
  their texts from `en.json`), no request per search; every word must match (accents and case ignored), title matches
  first, the words marked; a result opens its sub-page scrolled to the setting, focused and flashed once. A test
  checks every setting key is found.
