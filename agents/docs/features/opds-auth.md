# OPDS and API authentication

Code: `src/Librarion.Api/Opds/OpdsAuthentication.cs` (the `Opds` scheme); planned: Basic checks, API keys in
`src/Librarion.Core/Access/ApiKeys`. Decisions: [stage 7 plan](../development/stage-7-opds-plan.md) (D-7.2–D-7.5). Feeds:
[opds](opds.md).

## Built (stage 7 chunk 1)

- `/opds` routes authenticate with the `Opds` scheme only (`OpdsAuthenticationHandler`): the cookie is never their
  user, even when sent (`TheFeedsAskForBasicAndNeverTakeTheCookie`). No credentials: 401 with
  `WWW-Authenticate: Basic realm="Librarion", charset="UTF-8"`. It finds nobody yet: Basic passwords come in chunk 2,
  keys in chunk 3.
- Rights (stage 7, Users and up): `library.opds` (the feeds of a library), `account.apiKeys` (own keys),
  `account.shelves` (own shelves): [rights](rights.md).

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
