# OPDS and API authentication

Code: `src/Librarion.Api/Opds/OpdsAuthentication.cs` (the `Opds` scheme, Basic), `BasicVerificationCache.cs`;
`Api/Security/ApiKeyAuthentication.cs` (`ApiKeySignIn`, the `ApiKey` scheme), `CurrentAccess.cs` (narrowing);
`Core/Access/ApiKeys.cs`, `Core/Data/Entities/Access/ApiKey.cs`; `Api/Endpoints/ApiKeyEndpoints.cs`; client
`account/api-keys.ts`. Tests: `OpdsTests`, `ApiKeyTests`, `api-keys.spec.ts` (client and e2e). Decisions: [stage 7 plan](../development/stage-7-opds-plan.md) (D-7.2–D-7.5). Feeds:
[opds](opds.md).

## Built (stage 7 chunk 1)

- `/opds` routes authenticate with the `Opds` scheme only (`OpdsAuthenticationHandler`): the cookie is never their
  user, even when sent (`TheFeedsAskForBasicAndNeverTakeTheCookie`). No credentials: 401 with
  `WWW-Authenticate: Basic realm="Librarion", charset="UTF-8"`.
- Rights (stage 7, Users and up): `library.opds` (the feeds of a library), `account.apiKeys` (own keys),
  `account.shelves` (own shelves): [rights](rights.md).

## Built (stage 7 chunk 2): Basic with the password

- `OpdsAuthenticationHandler` reads `Authorization: Basic` (UTF-8; the name ends at the first colon, user names have
  none), checks it with `SignInService` (local directory, account lockout), and gives the cookie's claims (id, name,
  stamp), so `CurrentAccess` and `RequireRight` work as in the app. No tenant claim: a server administrator works in
  the first tenant until feeds name a library (chunk 5).
- **`BasicVerificationCache`** (singleton, memory): a success remembered 5 minutes by the upper-cased name and an HMAC
  of the password (key made per process); each hit checks the account's current stamp and state
  (`GetSessionUserAsync`, cached by `AccessChanges`), so a new password or a disabled account ends it at once. At most
  10,000 entries. Failures never remembered.
- Failures: `SignInThrottle` per name and address (5 → the address waits 15 min, even with the right password),
  the security log (`SignInFailed` / `LockedOut`, detail `opds`), 401 with the challenge again. Successes are not logged
  (every request of a reader app signs in).
- **Rate limit** `opds` on the whole group: `Librarion:Security:OpdsRequestsPerMinute` (600) per Basic name (read from
  the header, before authorization), else per address; 429 beyond.
- Tests: `OpdsTests` (right password and case of the name, wrong ones throttled and logged, new password and disabled
  account, the right disabled → 403, malformed headers, the rate limit).

## Built (stage 7 chunk 3): API keys

- **Stored** in `ApiKeys` (`TenantId` nullable as `Users`: a server administrator's keys have none; the tenant filter)
  and `ApiKeyLibraries` (the optional subset, with the library's `TenantId`). Text `lib_<id>_<secret>`: 32 random bytes
  base64url (43 characters); kept as the SHA-256 hex of the secret, compared with `PasswordHashing.SameSecret`; the
  whole text only in the create answer. Each key keeps the account's **stamp** at creation: a new password, disabling
  or enabling the account ends its keys (the user's D-7.3), as its sessions.
- `ApiKeys.VerifyAsync` keeps the row found per id while `AccessChanges.Version` holds (create and revoke move it):
  a revoke counts at the next request; expiry and the account's state are checked at each use. Last use (time and
  address) written at most once a minute per key (`UsedAsync`). At most 50 keys per account (`apiKey.tooMany`).
- **Ways in:** the default scheme is a policy scheme (`Librarion`): a request with `Authorization: Bearer` or
  `X-Api-Key` is the `ApiKey` scheme's, whatever cookie it has; others the cookie's. On `/opds` the `Opds` scheme
  takes the same headers, and a Basic secret shaped like a key (`ApiKeys.LooksLikeKey`) as a key, the Basic name
  having to be its owner's. A key request needs no `X-Librarion-Request` (no ambient credential, no CSRF).
- A server administrator's tenant on `/opds/l/<library>` is the library's ([opds](opds.md#built-stage-7-chunk-5-the-first-feeds)).
- **Narrowing** in one place, `CurrentAccess`: the handler puts the `ApiKeyGrant` in the request's items, the
  principal carries `librarion:key`; out of scope (`opds` for `/opds`, `api:read` for GET / HEAD / OPTIONS on `/api`,
  `api:write` for all of `/api`) the account keeps no right (403); a library subset keeps library rights only there
  (`ApiKeyGrant.Narrow`). `AccessState.KeyId` says a key signed the request.
- **Managing keys needs the web session** (`apiKey.needsSession`, 403): a key could otherwise make a wider key.
  Routes: `api/account/api-keys` (GET, POST, DELETE `{id}`: own, `account.apiKeys`), `api/users/{userId}/api-keys` (GET,
  DELETE `{id}`: `tenant.users` / `server.users`, the account routes' tenant rule).
- **Failures:** throttled per Basic name, else **per key id** (a stale key left in a reader app locks out itself, not
  the other keys used from that address), logged `SignInFailed` / `LockedOut` with the detail `api key`; never the key.
  Security events `ApiKeyCreated` (name and scopes) and `ApiKeyRevoked` (name; "of <user>" by an administrator).

## Built (stage 7 chunk 4): the pages

- `ApiKeys` component (`account/api-keys.ts`), two modes: **own** (My account › API keys, sub-page `apiKeys`,
  `account.apiKeys`): the new-key form (name, used for, libraries when the session has several, ends on, "can be used
  in the address" only with the `opds` scope), the key shown once in a read-only field with **Copy** (the clipboard,
  else selected with a toast: plain http on another computer has no clipboard), the catalog address
  (`new URL('opds', document.baseURI)`, so a proxy's folder too) and the user name; **an account's** (`userId`, inside
  the account editor of Administration › Users): list and revoke only, made anew per account opened (`@for` over the
  id).
- Rows: name, scopes, libraries (names from `LibraryContext`, `#id` for one the viewer doesn't see), "in the address",
  made / last used and from where / ends or ended / revoked; ended keys dimmed with a badge instead of Revoke.
  Revoke asks first.
- "Ends on" is a date: the end of that day in the browser's zone, sent as UTC.
- e2e `api-keys.spec.ts` on every device: make a key, the reader app's Basic with it (200), Done, revoke (401); axe in
  both themes on android.

## Ways in

- **Basic** (`Authorization: Basic user:secret`): what every OPDS client supports. `secret` is the password **or** an
  API key of that user. Answer 401 with `WWW-Authenticate: Basic realm="Librarion"` so clients prompt.
- **Bearer** (`Authorization: Bearer <key>`) and `X-Api-Key: <key>` for scripts and the REST API.
- **Key in the path** (`/opds/k/<key>/…`), opt-in per key, for clients that can't send credentials; every link in the
  feed keeps the prefix. The key is redacted from logs.
- The web cookie session is never accepted on `/opds` (no CSRF surface there); OPDS routes are GET only.
- Tenant: the user's own; for users of several libraries the feed root lists them, or `/opds/l/<library>/…`.

## API keys

- Format `lib_<id>_<secret>` (≥ 160 bits of secret); stored as the id + SHA-256 of the secret (high entropy: no slow
  hash needed, constant-time compare). Shown once at creation.
- Per key: name, scopes (`opds`, `api:read`, `api:write`), libraries (optional subset), path-key allowed, expiry,
  created, last used (+ address), revoked. Scopes narrow the user's rights, never widen them.
- Revoked or expired keys, a disabled user, or a changed security stamp end access at once.

## Cost and abuse

- Passwords are PBKDF2 (slow on purpose) and OPDS clients send them on **every** request: cache a successful
  verification for a few minutes keyed by a fast hash of (user, secret, security stamp); a failure is never cached.
- Failed Basic sign-ins go through the same throttle as the web sign-in (per name + address) and account lockout.
- Rate limit per key / user on feeds and downloads.
- Security events (failed sign-ins, key created/revoked, lockouts) logged without the secret.

## Tests

- Built: each way in, wrong password / revoked / expired / malformed key / disabled user → 401, scopes and libraries
  narrow rights, a key can't manage keys, no cookie on `/opds`, throttles, the cache dropped on a password change.
- Planned (chunk 8): the key in the path never in logs.
