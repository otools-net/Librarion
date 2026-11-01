# Changelog

User-visible changes per version (agents/docs/development/versioning-and-changelog.md).

## Unreleased

### Added

- A new logo and favicon: a temple front whose columns are book spines, its colours following the light or dark
  theme.
- First build: the server starts on SQLite or PostgreSQL and shows the app's frame in light and dark, on phones and
  desktops.
- First setup: the server prints a setup code; with it, the first server administrator and the first tenant (with
  its library) are created.
- Accounts, tenants, libraries and groups with rights (server, tenant, library), managed in the administration
  pages (rights can be switched off per tenant); sign-in with lockout and a per-address limit; server administrators
  recover their account with a one-time code shown where the server runs.
- Sign-in page, and a My account page to change the password and keep preferences (colors, density, language, books
  per page).
- Settings by level (server, tenant, library, own preferences) with locks, and a security log of sign-ins and access
  changes.
