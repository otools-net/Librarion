# Production upgrades

Librarion is **not in production yet**: until the MVP (1.0.0), the schema, stored files and settings may change without a
migration path (delete the dev database and re-import).

From the first release on, SFE's rules apply (its `production-upgrades.md`): every change to the database, caches,
settings or stored files ships with its tested migration path; migrations are only added, never edited; new columns
nullable or with a correct default; nothing writes before the update is confirmed; new rights reach existing installs
through `since`. Rewrite this note with the details when the release is made.
