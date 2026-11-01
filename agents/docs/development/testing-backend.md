# Backend test gotchas

Rules and infrastructure: [testing](testing.md). Code: `tests/Librarion.Tests/Infrastructure/`.

- **Two runs, one per provider:** `dotnet test` uses SQLite; with `LIBRARION_TEST_POSTGRES` set to a server
  connection string (a user that may create databases) every `TestCatalog` makes its own database there and drops it
  (`WITH (FORCE)`) at the end. Local server: `agents/tools/postgres-dev/` (Docker). CI runs both.
- On this machine Docker can't pull images (2026-10-31: Docker Desktop's proxy refuses `registry-1.docker.io`), so
  the PostgreSQL run is CI's until an image is available locally.
- Never `SqliteConnection.ClearAllPools()` in tests: under method-level parallelism it disposes handles other tests are
  opening; `TestCatalog` clears its own pool only (SFE's finding).
- `OutboundGuard` listens to the runtime's `System.Net.Http` and `System.Net.NameResolution` activity sources: it
  sees every outbound HTTP request and name lookup in the process, whatever library makes them. The tags (`url.full`)
  are set after the start: it records on `ActivityStopped`. Process-wide: its tests are `[DoNotParallelize]`; the test
  server's in-memory client never shows. `TheOutboundGuardSeesARequest` proves it still sees one (port 9 on
  127.0.0.1, refused at once).
- Tests reading the built client (`RepositoryFiles.WebRoot()`) are inconclusive without `wwwroot` locally
  (`-p:LibrarionSkipWebClient=true`) and fail on CI.
- Angular's router holds `http://fake` (a dummy base for `new URL`): allowed by exact match in
  `TheWebClientHasNoExternalUrls`, never as a prefix.
- `dotnet run --no-build` after adding a migration runs the old build: "no such table". Build first.
- Git Bash paths (`/c/...`) passed to .NET (a `Data Source=`) don't open: use `cygpath -m`.
