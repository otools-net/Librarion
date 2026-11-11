# <img src="src/Librarion.Web/public/logo.svg" alt="" width="40" height="40"> Librarion

A self-hosted e-book library server for **huge collections**: millions of books, as loose files or by the hundred
thousand inside ZIP archives, browsed, searched and read from any device, and served to reader apps over OPDS (with a
password or an API key). Accounts, groups, rights and several tenants on one server.

> [!WARNING]
> **Early development (0.x).** Librarion is being built in stages; nothing is usable yet beyond the first start.
> Version 1.0.0 will be the first usable release.

## Principles

- **Private:** fully local. No CDN, no telemetry, nothing sent anywhere unless an administrator turns it on.
- **Fast on huge libraries:** millions of entries without slowing down.
- **For every screen:** one compact app for phones and desktops.
- **Tested:** unit tests for every function, end-to-end tests for the important flows.

## Run from source

Needs the .NET 10 SDK and Node.js 22+.

```sh
dotnet run --project src/Librarion.Api
```

Then open http://localhost:5090. The first time, the setup page asks for the setup code the server printed in its
console (also in `src/Librarion.Api/data/setup-code.txt`) and makes the first administrator:
[docs/first-start.md](docs/first-start.md). It uses SQLite by default; PostgreSQL and other settings:
[docs/configuration.md](docs/configuration.md).

## Documentation

- [docs/](docs/README.md): installation, first start, configuration, accounts, browsing, shelves, reader apps (OPDS)
  and administration.
- Contributors and AI agents: [AGENTS.md](AGENTS.md).

## License

MIT, see [LICENSE](LICENSE).
