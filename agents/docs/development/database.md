# Database: two providers, migrations each

Code: `src/Librarion.Core/Data/` (`LibrarionDbContext`, `DatabaseOptions`, `LibrarionDatabase`, `Migrations/Sqlite`,
`Migrations/Postgres`). Tests: `DatabaseTests`. Decision D2 in [foundation-plan](foundation-plan.md).

- **One model, two contexts:** the model is in the abstract `LibrarionDbContext`; `SqliteLibrarionDbContext` and
  `PostgresLibrarionDbContext` only exist so each provider keeps its own migrations. Code asks for
  `IDbContextFactory<LibrarionDbContext>` and never knows which provider it got.
- **Every model change = two migrations**, same name:

  ```sh
  dotnet ef migrations add <Name> --context SqliteLibrarionDbContext --project src/Librarion.Core --output-dir Data/Migrations/Sqlite
  dotnet ef migrations add <Name> --context PostgresLibrarionDbContext --project src/Librarion.Core --output-dir Data/Migrations/Postgres
  ```

  `EveryModelChangeHasItsMigrationForBothProviders` fails otherwise (`HasPendingModelChanges`, no database needed).
- **Don't pass `--namespace`**: with it, `dotnet ef` wrote the snapshot into `Librarion/Core/Data/Migrations/...`
  (a folder named after the namespace) instead of the output folder. The folder gives the right namespace already.
- `dotnet ef` writes CRLF with a BOM: normalise new files to LF without BOM (`sed -i '1s/^\xEF\xBB\xBF//; s/\r$//'`).
- The design-time factories (`SqliteDesignTimeFactory`, `PostgresDesignTimeFactory`) open no database.
- **The schemas must agree** (tables, columns, nullability, lengths, keys, indexes, foreign keys):
  `ModelsOfBothProvidersAgree`. Provider-specific column types are fine; anything else a provider needs (an index
  type, a collation) goes through configuration both providers understand, or the test learns the difference with a
  comment why.
- Start: `LibrarionDatabase.InitializeAsync` sets SQLite to WAL, applies pending migrations, records the version in
  `ServerProperties`. Before the MVP nothing gates this ([production-upgrades](production-upgrades.md)).
- Indexed strings need a max length (PostgreSQL `character varying(n)`, SQLite ignores it but the model agrees).
- Order every `Take`; keyset paging only ([scale](scale.md)).
- Before 1.0 the `Initial` migrations (both providers) are regenerated on a model change instead of adding new ones:
  no installs exist ([production-upgrades](production-upgrades.md)):
  [regen-migrations](../../tools/regen-migrations/README.md) does it (a plain `rm` of the migration folders' globs is
  stopped by the agent's safety check; the tool removes them by `git rm` of their listed names).
