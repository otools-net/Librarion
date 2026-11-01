# Settings by level

Code: `src/Librarion.Core/Settings/` (`SettingCatalog`, `SettingsService`), `Api/Endpoints/SettingsEndpoints.cs`
(`api/settings`, `api/me/preferences`); client `settings/settings-panel.ts`, `admin/settings-section.ts`, the
preferences on `account/account-page.ts`, `core/session.service.ts` (applies theme, density). Ported from SFE
(`settings-levels.md` there), all levels in the database. Tests `SettingsTests`.

- **Levels** server → tenant → library → user (`Settings` rows: level, owner, key, JSON value, locked; owner 0 for
  the server). Each level inherits what it doesn't set; a row exists only where a level differs from what it
  inherits, or locks.
- A level may **lock** a key: the levels below can't change it (`LockedAbove`); the user level can't lock
  (`setting.notLockable`).
- **Catalog** (`SettingCatalog.All`): key, kind (choice, integer), default, choices or range, the **lowest level** that
  may set it, section. Texts `setting.<key>`, `setting.<key>.<choice>`, `settingSection.<section>`.
- The right editing a level comes from the level: `server.settings`, `tenant.settings`, `library.settings`,
  `account.preferences` (own preferences).
- A save applies all its changes or none; errors by key (`setting.unknown`, `setting.notEditable`, `setting.invalid`).
- `GET api/me/preferences` = the values in effect for the account (its own over its tenant's and the server's); the
  session carries them too, so the client applies theme and density at sign-in.
- Keys so far: `ui.theme` (system/light/dark), `ui.density` (compact/comfortable), `ui.language` (browser/en),
  `ui.pageSize` (20–200, 50); `library.keepMissingDays` (library level and above, 0–3650, 30:
  [missing-books](missing-books.md)).
- The theme button in the top bar changes only this browser (localStorage), not the account's preference.
- A new setting: one line in the catalog, its texts, and where the client uses it; no migration (rows hold JSON).
