# Stage 1: access and tenancy (plan)

Roadmap and decisions: [foundation-plan](foundation-plan.md) (D4 tenants, D14 local sign-in behind `IUserDirectory`).
Goal: who may do what, from the first real route. Ported from SFE (`accounts.md`, `rights.md`, `right-kinds.md`,
`setup.md`, `settings-levels.md`, `sessions-and-pipeline.md` there), adapted to **one shared database with tenants
holding several libraries**. Version stays `0.0.0` on `edge`; the merge into `main` makes it `0.2.0`.

## Decisions for this stage

- **Tenant = organisation** (`Tenants`), holding **libraries** (`Libraries`: id, tenant, name; roots and scanning come
  in stage 3). Every tenant-owned row has `TenantId`.
- **Accounts:** user names are **unique server-wide** (OPDS Basic sends a name without a tenant). An account belongs
  to one tenant (`TenantId`), or to none when it is a **server administrator** (every right everywhere but the
  disabled ones; works in the tenant it picks).
- **Groups per tenant**, built-in **Administrators** and **Users** (no Guests in v1: no anonymous access until OPDS
  needs a decision). A group grants tenant rights tenant-wide and library rights **in its libraries** (`AllLibraries`
  or `GroupLibraries` rows).
- **Rights catalog** with three scopes: server, tenant, library; `Kind` Read/Write, default groups. No `since`
  revisions before 1.0 (no installs to upgrade: [production-upgrades](production-upgrades.md)).
- **Tenant isolation in the database:** a context made for a tenant (`TenantDb.ForTenantAsync`) filters every
  tenant-owned set by global query filters; server-wide code uses an unfiltered one by name (`ForServerAsync`).
  `NoQueryCrossesTenants` checks every tenant-owned entity has the filter and that a tenant's context sees no row of
  another.
- **Settings in the database, by level** server → tenant → library → user (`Settings` rows: level, owner, key, JSON
  value, locked); catalog of keys with their lowest level and the right that edits them; SFE's resolve/lock rules.
- **Security event log** (`SecurityEvents`): sign-ins and failures, lockouts, setup, recovery, password changes,
  account/group/tenant/library changes; never a secret; kept `Librarion:Security:EventDays` (365) days; tenant
  administrators see their tenant's, server administrators all.
- Setup code generated into `data/setup-code.txt` and printed to the console (never logged), as SFE; SFE's "set up from
  the server's own computer without a code" is left for later.

## Work items

### 1.1 Data and access core — done (`ad3a49f`)

- [x] Entities + migrations (both providers): `Tenants`, `Libraries`, `Users`, `Groups`, `GroupMembers`,
      `GroupRights`, `GroupLibraries`, `TenantDisabledRights`, `Settings`, `SecurityEvents`; query filters
      (`TenantScope` on the context, `TenantDb.ForTenantAsync` / `ForServerAsync`).
- [x] `RightCatalog` / `Rights`, `RightsEvaluator` (cached until `AccessChanges` moves; `Librarion:Rights:Disabled`).
- [x] `PasswordHashing`, `IUserDirectory` + `LocalUserDirectory` (lockout), `SignInService`, `AccessService`
      (tenants, libraries, users, groups; last server administrator kept; a tenant keeps an administrator).
- [x] `SetupService`, `GeneratedSetupCode`, `AccountRecovery`, `SecurityLog` (+ `SecurityLogCleanup`), `SettingsService`.

### 1.2 API — done (`ad3a49f`)

- [x] Cookie session, `CurrentAccess`, `RequireRight` policies, `SignInThrottle` + sign-in rate limit, `RequestGuard`
      503 `setup-required`, `SetupCodeNotice` (console box at start).
- [x] Routes: `api/setup`, `api/session/*`, `api/tenants`, `api/libraries`, `api/users`, `api/groups`, `api/rights`,
      `api/settings`, `api/me/preferences`, `api/security-events`; `api/system/info` (`system.info`) and `/openapi`
      (`system.apiDocs`) behind rights.

### 1.3 Client — done (committed with this note)

- [x] `SessionService` (`can`, `signedIn` / `needsRight` guards), `Api` with `ApiError` (401 → sign-in, 503 → setup),
      `errors.ts` (server codes → `error.<code>`), setup page, sign-in page with recovery, account page (password,
      preferences), administration (`/admin/users|groups|libraries|tenants|settings|security`), shared
      `SettingsPanel`, shell: admin link, tenant switcher (server administrators), account button.
- [x] Styles: `forms.css`, `account.css`, `admin.css` (SFE's, on Librarion's tokens).

### 1.4 Tests and docs — tests done, notes and docs left

- [x] Backend (45 green on SQLite): `AccessTests`, `TenancyTests` (`NoQueryCrossesTenants`), `SettingsTests`,
      `RightCatalogTests`, `ClientI18nTests.EveryNameTheServerSendsHasItsText`; earlier tests moved to signed-in clients.
- [x] Client specs (44 green): session/guards/errors, sign-in, setup, account, settings panel, admin sections, shell.
- [x] e2e (47 green on 5 devices + `first-start`): the first start through the setup page (wrong code, then the
      right one), sign-in and back where it was, sign-out, recovery with the code from `recovery-code.txt`, an account
      given one right through a group using it (and seeing only that section), axe on setup, sign-in, recovery,
      account and every admin section ([testing-e2e](testing-e2e.md)).
- [x] Looked at every page at 360 px and 1280 px (`agents/tools/screenshots`): fixed the two "Libraries" headings of
      the group editor, the stretched "Changes things" badges, and the sign-out button touching the panel below.
- [ ] Feature notes: update `features/rights.md` (no longer planned: catalog, scopes, evaluation as built), new
      `features/accounts.md`, `features/tenants.md`, `features/setup.md`, `features/settings.md`,
      `features/security-log.md` (code comments already point at these names), and the AGENTS.md index lines.
- [ ] `docs/`: first start (setup code), accounts, groups and rights, settings levels; README first-run lines.
- [ ] PostgreSQL run: never run locally (Docker can't pull images on this machine); push `edge` when the user says.
- [ ] When all is green: tick the exit criteria, commit "stage 1 done", ask the user about merging into `main` (0.2.0).

## Decisions taken while building (keep in the feature notes)

- Server administrators have no tenant (`Users.TenantId` null) and are in no group; they pick the tenant they work in
  (`POST api/session/tenant`, claim in the cookie). Before any tenant exists they get every non-library right.
- `IsServerAdmin` and an account's tenant are fixed at creation (`user.adminFixed`).
- Tenant routes take `?tenant=` for server administrators (`ApiResults.TargetTenant`): own tenant needs the tenant
  right, another tenant the server right; refused = 404 (a tenant administrator doesn't learn other tenants exist).
- Validation and conflict answers carry codes (`userName.taken`, `tenant.lastAdministrator`), translated by the client
  as `error.<code>`; problem `type` is the code for 409s.
- Settings: four `ui.*` keys only so far (theme, density, language, page size); server level stored in the database
  too (not a settings file as SFE).
- Pre-1.0: the `Initial` migration is regenerated rather than adding migrations (no installs exist).
- No guests and no "set up from this computer without a code" (SFE has both): left for later decisions.

## Exit criteria

`EveryRouteNamesARight` and `NoQueryCrossesTenants` green; a tenant administrator can't reach other tenants or server
administrators; e2e setup, sign-in and admin flows green on phone and desktop projects; both providers in CI.
