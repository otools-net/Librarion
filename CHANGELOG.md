# Changelog

User-visible changes per version (agents/docs/development/versioning-and-changelog.md).

## Unreleased

### Added

- First build: the server starts on SQLite or PostgreSQL and shows the app's frame in light and dark, on phones and
  desktops.
- First setup: the server prints a setup code; with it, the first server administrator and the first tenant (with
  its library) are created.
- Accounts, tenants, libraries and groups with rights (server, tenant, library), managed through the API; sign-in
  with lockout and a per-address limit; server administrators recover their account with a one-time code shown where
  the server runs.
- Settings by level (server, tenant, library, own preferences) with locks, and a security log of sign-ins and access
  changes.
