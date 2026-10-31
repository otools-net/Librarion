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

## Signed-in tests (stage 1)

- Every route but `api/setup` and `api/session` answers 503 `setup-required` until the server is set up: start with
  `ApiClients.SetUpAsync(factory, token)` (server administrator `admin`, tenant "Home"; returns the client and the
  session), then `ApiClients.CreateUserAsync(admin, new NewUser(...))` and `ApiClients.SignInAsync(factory, name, password)`.
- An account created without groups has no rights at all (not even `account.password`): put it in the built-in
  Users group (`GET api/groups`, `BuiltIn == Users`) when the test needs member rights.
- `TestCatalog.Settings()` points the setup code file and the Data Protection keys into the test's folder and raises
  the sign-in rate limit; a test needing other security values passes them to `CreateFactory`
  (`settings["Librarion:Security:MaxFailedSignIns"] = "3"`).
- PBKDF2 (210k iterations) makes each account creation and sign-in cost ~0.1 s: create only the accounts a test needs.
