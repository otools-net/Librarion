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

### 1.1 Data and access core

- [ ] Entities + migrations (both providers): `Tenants`, `Libraries`, `Users`, `Groups`, `GroupMembers`,
      `GroupRights`, `GroupLibraries`, `TenantDisabledRights`, `Settings`, `SecurityEvents`; query filters.
- [ ] `RightCatalog` / `Rights`, `RightsEvaluator` (cached until `AccessChanges` moves; `Librarion:Rights:Disabled`).
- [ ] `PasswordHashing` (PBKDF2-SHA512 210k, rehash), `IUserDirectory` + `LocalUserDirectory` (lockout),
      `SignInService`, `AccessService` (tenants, libraries, users, groups; last-administrator rules).
- [ ] `SetupService`, `GeneratedSetupCode`, `AccountRecovery`, `SecurityLog` (+ retention), `SettingsService`.

### 1.2 API

- [ ] Cookie session (`librarion.session-<port>`, stamp checked per request, Data Protection keys in `data/keys`),
      `CurrentAccess`, `RequireRight` policies, `SignInThrottle` + sign-in rate limit, `RequestGuard` 503
      `setup-required`.
- [ ] Routes: `api/setup`, `api/session` (get, login, logout, tenant, recovery, password), `api/tenants`,
      `api/libraries`, `api/users`, `api/groups`, `api/rights`, `api/settings`, `api/me/preferences`,
      `api/security-events`; `api/system/info` and `/openapi` behind rights.

### 1.3 Client

- [ ] Session service (`can(right)`), 401 → sign-in, 503 → setup; setup page, sign-in page with recovery, account page
      (password, preferences), admin section: tenants, users, groups (rights by area, libraries), libraries, settings by
      level, security log. Phone and desktop, compact, i18n keys.

### 1.4 Tests and docs

- [ ] Backend: rights evaluation, catalog invariants, lockout and throttle, setup and recovery, session ends on stamp
      change, last administrator kept, `NoQueryCrossesTenants`, tenant administrator can't reach another tenant or a
      server administrator (every admin route), settings levels and locks, event log filtering, both providers.
- [ ] Client specs for every page and service; e2e: setup, sign-in, admin flows (user, group, library), sign in as
      the new user, on phone and desktop with layout checks and axe.
- [ ] Feature notes (rights, accounts, setup, settings, security log), `docs/` (first start, accounts and rights),
      changelog.

## Exit criteria

`EveryRouteNamesARight` and `NoQueryCrossesTenants` green; a tenant administrator can't reach other tenants or server
administrators; e2e setup, sign-in and admin flows green on phone and desktop projects; both providers in CI.
