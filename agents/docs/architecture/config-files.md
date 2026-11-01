# Configuration files and their order

Code: `src/Librarion.Api/Program.cs`, `appsettings.json`, `Core/Data/DatabaseOptions.cs`. User docs:
`docs/configuration.md`.

Later sources win:

1. `appsettings.json` (neutral, committed)
2. `appsettings.Local.json` (this machine's values, git-ignored; inserted right after `appsettings.json`, reloads on
   change)
3. `appsettings.{Environment}.json` (`appsettings.Development.json` is git-ignored too)
4. environment variables (`LIBRARION__DATABASE__PROVIDER`, `CONNECTIONSTRINGS__LIBRARION`…: double underscore for `:`)
5. the command line (`--Librarion:Database:Provider=postgres`)

- The watcher of `appsettings.Local.json` is disposed at `ApplicationStopped`: the configuration doesn't dispose a
  provider it didn't create, and on Linux each holds one of the user's 128 inotify instances (tests start many
  servers in one process; SFE's finding).
- **Cache files** (covers now, the extract cache later) all go under one configurable folder,
  `Librarion:Cache:Directory` (default `data/cache`, env `LIBRARION__CACHE__DIRECTORY`; `CacheOptions.PathOf(root,
  name)` gives each cache its subfolder). The user's rule (2026-11-01): no cache path is fixed in code. Tests point it
  into their temp folder (`TestCatalog.Settings`).
- Keys so far: `Librarion:Database:Provider` (`sqlite` default | `postgres`), `ConnectionStrings:Librarion` (SQLite
  default `data/librarion.db` next to the app; required for PostgreSQL), `Urls` (default `http://localhost:5090`).
