# OPDS and API authentication

Code: `src/Librarion.Api/Opds/OpdsAuthentication.cs` (the `Opds` scheme); planned: Basic checks, API keys in
`src/Librarion.Core/Access/ApiKeys`. Decisions: [stage 7 plan](../development/stage-7-opds-plan.md) (D-7.2–D-7.5). Feeds:
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

## Tests (planned)

- Each way in signs in; wrong password / revoked key / disabled user → 401; key scopes narrow rights; key in path
  never in logs; no cookie on `/opds`; throttle after N failures; cache dropped on password change.
