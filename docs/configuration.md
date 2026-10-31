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

## Address

`Urls` sets where the server listens (default `http://localhost:5090`, this computer only). To reach it from other
devices: `"Urls": "http://0.0.0.0:5090"`.
