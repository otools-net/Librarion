# Configuration

Settings are read, in this order (later ones win):

1. `appsettings.json` next to the program;
2. `appsettings.Local.json` next to it, for your own values (create it; it is never overwritten by an update);
3. environment variables, with `__` between parts: `LIBRARION__DATABASE__PROVIDER=postgres`;
4. the command line: `--Librarion:Database:Provider=postgres`.

## Database

| Setting | Values |
|---|---|
| `Librarion:Database:Provider` | `sqlite` (default) or `postgres` |
| `ConnectionStrings:Librarion` | the connection string; for SQLite optional (default: `data/librarion.db` next to the program) |

SQLite needs nothing installed and suits one person or a family. For libraries of hundreds of thousands of books or
more, or many people at once, use PostgreSQL 15 or later:

```json
{
  "Librarion": { "Database": { "Provider": "postgres" } },
  "ConnectionStrings": { "Librarion": "Host=localhost;Database=librarion;Username=librarion;Password=..." }
}
```

The database and its tables are created at the first start.

Librarion turns off PostgreSQL's parallel workers for its own connections (every page it asks for is read by index,
and starting workers only slowed it down). A connection string with its own `Options=...` keeps what it sets.

## Address

`Urls` sets where the server listens (default `http://localhost:5090`, this computer only). To reach it from other
devices: `"Urls": "http://0.0.0.0:5090"`.

## Libraries

| Setting | Values |
|---|---|
| `Librarion:Libraries:Folders` | folders under which tenant administrators may add library folders (a list: `Librarion__Libraries__Folders__0=/srv/books`); empty (the default): only server administrators add folders |
| `Librarion:Libraries:MaxParallelScans` | scans running at once on the server (default 2); more wait their turn |

## Cache

| Setting | Values |
|---|---|
| `Librarion:Cache:Directory` | the folder for cache files (default `data/cache` next to the program; environment `LIBRARION__CACHE__DIRECTORY`). Book covers go in its `covers` folder. Everything in it can be deleted: it is made again when needed. Put it on a fast disk with room to spare |
| `Librarion:Covers:MaxParallel` | covers made at once (default: half the processor cores, at least 2) |

## Data

| Setting | Values |
|---|---|
| `Librarion:Data:Directory` | the folder for files people add (default `data` next to the program; environment `LIBRARION__DATA__DIRECTORY`): uploaded covers go in its `covers` folder, one folder per tenant. Unlike the cache it can't be made again: back it up with the database |

## Setup

| Setting | Values |
|---|---|
| `Librarion:Setup:Code` | the code the setup page asks for, at least 12 characters; without one the server makes one ([first start](first-start.md)) |
| `Librarion:Setup:CodeFile` | where a generated code is kept (default `data/setup-code.txt`); recovery codes go beside it |
| `Librarion:Setup:Recover` | `true` opens the setup page again to reset or add a server administrator; turn it off afterwards |

## Security

| Setting | Default | |
|---|---|---|
| `Librarion:Security:MinimumPasswordLength` | 8 | shortest password accepted |
| `Librarion:Security:MaxFailedSignIns` | 5 | wrong passwords for one name from one address before that address waits |
| `Librarion:Security:LockoutMinutes` | 15 | how long it waits |
| `Librarion:Security:AccountLockoutFailures` | 20 | wrong passwords in a row, from anywhere, before the account itself is locked (0: never) |
| `Librarion:Security:SignInsPerMinute` | 20 | sign-in attempts per address and minute |
| `Librarion:Security:OpdsRequestsPerMinute` | 600 | requests per minute of one reader app's account (or address without one) on the OPDS catalog, covers and downloads included; more answer 429 |
| `Librarion:Security:SessionDays` | 30 | how long "keep me signed in" lasts without use |
| `Librarion:Security:KeysDirectory` | `data/keys` | the keys signing sessions; keep them, or everyone is signed out |
| `Librarion:Security:RequireHttps` | `false` | marks the session cookie secure (the server is only reached over https) |
| `Librarion:Security:TrustedProxies` | none | reverse proxies whose forwarded addresses are believed, besides this computer |
| `Librarion:Security:EventDays` | 365 | days the [security log](administration/security-log.md) keeps its events |

## Rights

`Librarion:Rights:Disabled` switches rights off for everyone, server administrators too: a list of right ids
(`library.settings`), prefixes (`tenant.*`), or `kind:write` for a read-only server. The rights:
[reference](reference/rights.md).

```json
{ "Librarion": { "Rights": { "Disabled": [ "kind:write" ] } } }
```
