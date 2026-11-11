# Accounts, sign-in and sessions

Code: `src/Librarion.Core/Access/` (`AccessService` accounts and groups, `IUserDirectory` / `LocalUserDirectory`,
`SignInService`, `PasswordHashing`, `AccountRecovery`), `src/Librarion.Api/Security/` (`LibrarionSession`,
`CurrentAccess`, `SignInThrottle`), `Api/Endpoints/SessionEndpoints.cs`, `UserEndpoints.cs`, `GroupEndpoints.cs`;
client `account/` (sign-in, account page), `admin/users-section.ts`, `admin/groups-section.ts`,
`core/session.service.ts`. Ported from SFE (`accounts.md`, `sessions-and-pipeline.md` there). Rights:
[rights](rights.md); tenants: [tenants](tenants.md).

## Accounts

- **User names are unique server-wide**, whatever the case (OPDS Basic sends a name without a tenant); no colon or
  control characters (`userName.invalid`: Basic sends `name:password`).
- An account belongs to **one tenant**, or to none when it is a **server administrator** (no groups; works in the
  tenant it picks: `POST api/session/tenant`, the tenant in the cookie). Its tenant and `IsServerAdmin` are fixed at
  creation (`user.adminFixed`).
- The last server administrator can't be disabled or deleted (`user.lastServerAdmin`); a tenant that had an enabled
  administrator keeps one (`tenant.lastAdministrator`), checked on every save of accounts and groups.
- A tenant administrator (`tenant.users`) sees and changes its tenant's accounts and groups only (others: not found);
  `server.users` reaches every account and makes server administrators (`api/users?admins=true` lists them).
- Lists page by name, 100 at a time (`after` = last name), `search` = start of name or display name.
- `IUserDirectory` (D14): the local directory today; LDAP/OIDC would be another, rights still hang on the `Users` row
  (`Source`).

## Passwords and sign-in

- PBKDF2-SHA512, `pbkdf2-sha512$iterations$salt$hash`; older iteration counts verify and are rehashed. Minimum
  length `Librarion:Security:MinimumPasswordLength` (8).
- **Throttle** (`SignInThrottle`, memory): `MaxFailedSignIns` (5) wrong passwords for one name from one address →
  that address waits `LockoutMinutes` (15) for that name; unknown names count the same. The **account** locks in the
  database after `AccountLockoutFailures` (20) in a row from all addresses; an administrator unlocks it.
- Reader apps' Basic sign-ins on `/opds` go through the same throttle and account lockout ([opds-auth](opds-auth.md)).
- **Rate limit** `SignInsPerMinute` (20) per address on sign-in, setup, recovery and password change (429).
- Answers never tell a wrong name from a wrong password (`sign-in-failed`); `locked-out` carries `until`.

## Sessions

- A cookie (images and the reader send cookies only): account id, **security stamp**, tenant. A new password,
  disabling or enabling gives a new stamp: every session of the account ends at its next request. Changing one's
  own password renews the current session.
- "Keep me signed in": a persistent cookie, `SessionDays` (30) sliding; otherwise a browser-session cookie.
- Cookie names carry the port (`InstanceCookieManager`): two instances on one host don't sign each other out.
- Cookie keys in `Librarion:Security:KeysDirectory` (`data/keys`): kept, so a restart signs nobody out.
- `CurrentAccess` works out the account, tenant and rights once per request. Visitors have no rights (no guests yet).

## Recovery

- A server administrator locked out or without the password: on the sign-in page, "Recover the account" →
  `POST api/session/recovery` prints a one-time code in the console and writes it to `recovery-code.txt` beside the
  setup code file, valid 15 minutes; the answer is the same whatever the name. The code with a new password
  (`recovery/complete`) unlocks the account and signs it in. Other accounts ask an administrator.
- Codes are never logged (loggers are stored and shown in the app): the console only.
- The e2e recovery spec reads the code from the file ([testing-e2e](../development/testing-e2e.md)).
