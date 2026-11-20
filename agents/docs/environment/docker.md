# Docker image

Files: `Dockerfile`, `.dockerignore`, `docker-compose.yml` (PostgreSQL behind an existing Traefik),
`docker-compose.env.example`, `.github/workflows/docker-image.yml`; user doc [docs/docker.md](../../../docs/docker.md).
Ported from SFE's (its `agents/docs/environment/docker.md`), without its portable build and entry point.

- Why now (2026-11-20): the Kobo's browser wouldn't open a LAN IP address, so the e-reader checks by hand
  ([ereader-mode](../features/ereader-mode.md#checked-by-hand-the-stages-exit-criterion-the-user-before-the-merge)) need
  a test server on a real domain; the user runs it behind Traefik (TLS there) with PostgreSQL.
- Three stages: `node:24-bookworm-slim` builds the client into `src/Librarion.Api/wwwroot`; the SDK stage restores
  with only the project files first (layer cache), copies `en.json` (embedded by the Api) and the built client, and
  publishes **framework-dependent without a RID** (`-p:LibrarionSkipWebClient=true -p:UseAppHost=false`): the native
  libraries (SkiaSharp, SQLite) of every platform come along, so both build stages run once on `$BUILDPLATFORM`;
  `aspnet:10.0` holds the result and **runs no `RUN`**, so the arm64 image needs no QEMU.
- Content root `/app` (the web files are there): every data default is relative to it (`data/librarion.db`,
  `data/keys`, `data/setup-code.txt`, `data/cache`, `data`), so one volume on **`/app/data`** holds them all.
- Gotcha: `/app/data` must belong to `$APP_UID` (1654) for a new named volume to be writable. A `COPY --chown` into a
  folder an earlier `COPY` already made leaves that folder root's (SQLite "unable to open database file"); it's made
  as `/empty/data` in the build stage and copied with `--chown` onto `/app/`.
- `Urls=http://+:8080` and `ASPNETCORE_HTTP_PORTS=` empty: `appsettings.json`'s `Urls` (localhost) would win over the
  base image's port. `LIBRARION_COMMIT` (build argument) → `SourceRevisionId`, as `.git` isn't in the context.
- Compose: `Librarion__Security__RequireHttps=true`, `TrustedProxies__0` = `LIBRARION_TRUSTED_PROXIES` (default
  `172.16.0.0/12`), `AllowedHosts` = the domain, Traefik labels (router `librarion`, entry point / cert resolver /
  network from `.env`), Traefik's network external; PostgreSQL 17 only on the project's own network, its health check
  before Librarion starts.
- Workflow: a test job (Linux SQLite `dotnet test` and the client's specs), then buildx for both platforms to GHCR
  with `GITHUB_TOKEN`: push to `edge` → `:edge`, to `main` → `:main`, tag `vX.Y.Z` → version, `X.Y`, `latest` (the tag
  must equal `Directory.Build.props`); "Run workflow" may add a tag. A new GHCR package is **private**: make it public
  (the package's settings) or `docker login ghcr.io` on the server.
- Smoke test (2026-11-20, this machine): image built, the compose stack with a throwaway Traefik on a `traefik`
  network: setup over https, a Kobo's User-Agent sent from `/` to `/e/`, cookies `secure`, sign-in through `/e/signin`,
  44 tables in PostgreSQL, the security log with the forwarded address. **Traefik 3.5 fails on Docker Engine 29**
  ("API returned a 400" from its Docker provider: too old an API version); 3.6 works.
