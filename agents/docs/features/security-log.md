# Security log

Code: `src/Librarion.Core/Access/SecurityLog.cs` (+ `SecurityLogCleanup`), `Core/Data/Entities` (`SecurityEvent`,
`SecurityEventKind`), `Api/Endpoints/SecurityEventEndpoints.cs`, the `log.LogAsync` / `WriteAsync` calls in the
endpoints; client `admin/security-section.ts`.

- **What:** sign-ins and refusals, lockouts, sign-outs, setup, recovery asked and done, password changes, accounts,
  groups, tenants, libraries and settings created, changed or deleted. Each event: kind, tenant (null = the
  server's own), account id, name, address, a short detail of what changed.
- **Never a secret:** no password, key or code; callers pass names and what changed only.
- Kept `Librarion:Security:EventDays` (365) days: `SecurityLogCleanup` deletes older events in batches at start and
  twice a day.
- **Who reads:** `tenant.securityLog` its tenant's events; `server.securityLog` every event (`all=true`) or the
  server's own.
- Newest first, keyset by id (`before` = last id of the page before), filtered by kind and name.
- A new kind: the enum value, its text `securityEvent.<kind>`, and the call where it happens.
- Reader apps (stage 7): a wrong Basic password on `/opds` writes `SignInFailed` / `LockedOut` with the detail `opds`
  (`, disabled` for a disabled account); successful Basic sign-ins are not logged (a reader app signs in with every
  request).
- Separate from the application log (loggers): that one is for the operator, this one is shown in the app.
