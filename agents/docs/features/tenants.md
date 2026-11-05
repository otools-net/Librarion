# Tenants and libraries

Code: `src/Librarion.Core/Data/TenantDb.cs`, `LibrarionDbContext.cs` (query filters, `TenantScope`),
`src/Librarion.Core/Access/AccessService.cs` (tenants, libraries), `Api/Endpoints/TenantEndpoints.cs`,
`LibraryEndpoints.cs`, `ApiResults.TargetTenant`; client `admin/tenants-section.ts`, `admin/libraries-section.ts`,
the tenant switcher in `shell/`. Decision D4 in [foundation-plan](../development/foundation-plan.md); tests
`TenancyTests`.

- **Tenant = organisation** (a family, a club, a company): its own libraries, accounts, groups, settings, security
  events, in **one shared database**. Every tenant-owned row has `TenantId`.
- **Library**: id, tenant, name (roots and scanning come in stage 3). A new tenant gets its built-in groups (default
  rights, every library) and a first library (`LibraryName`, default "Library").
- Slug: lower-case ASCII, digits, dashes, made from the name, fixed once created.
- A tenant can be disabled (its accounts can't sign in and their sessions end: `SessionUser.Disabled`), or have
  rights switched off for everyone in it (`TenantDisabledRights`; tenant editor in `admin/tenants-section.ts`;
  server administrators working there lose them too).
- Deleting a tenant deletes its libraries, accounts, groups and settings; the client asks to type its name.
- Managing tenants needs `server.tenants`, which also reaches every tenant's accounts, groups and libraries; a tenant
  administrator manages its own libraries with `tenant.libraries`.

## Isolation in the database

- Code working for a tenant takes `TenantDb.ForTenantAsync(id)`: global query filters on every tenant-owned set hide
  other tenants' rows, so a forgotten `Where` can't leak them; an id from another tenant is simply not found.
- Server-wide code (sign-in, tenants, server administrators, the log cleanup) takes `ForServerAsync`, by name.
- **Two models** (`ScopeModelCacheKeyFactory`): tenant contexts get the filters as `TenantId = @tenant`, server
  contexts none. One model with `TenantScope == null || TenantId == TenantScope` was sent by EF as
  `(@p OR "TenantId" = @s)`: no index serves it, so every big query scanned and sorted the whole table (stage 2
  benchmark, 2026-11-01). `TenantScope` must be set before the context's first use (`TenantDb` does).
- `NoQueryCrossesTenants` checks that every entity with `TenantId` has the filter and that a tenant's context sees no
  row of another. A new tenant-owned entity gets the filter in `LibrarionDbContext` or the test fails.

## Server administrators and tenants

- They belong to no tenant and work in the one their session picked (the first one by default); the account menu lists
  the tenants when there are several.
- Tenant routes take `?tenant=` for them: own tenant needs the tenant right, another tenant the server right;
  refused = 404.
