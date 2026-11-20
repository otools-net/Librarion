# Docker

Librarion is published as a Docker image, `ghcr.io/otools-net/librarion`, for `amd64` and `arm64`:

| Tag | What it is |
|---|---|
| `latest`, `0.9.0`, `0.9` | a release |
| `main` | the last finished stage, not released yet |
| `edge` | the work in progress: new every time it changes, for testing |

## On an address of the internet, behind Traefik

The repository's `docker-compose.yml` runs Librarion with PostgreSQL behind a [Traefik](https://traefik.io/) you
already run, which gets the HTTPS certificate. You need:

- a domain or subdomain (`books.example.com`) whose DNS points at the machine running Traefik;
- Traefik with its own Docker network, an entry point for https and a certificate resolver (Let's Encrypt).

1. Put `docker-compose.yml` and `docker-compose.env.example` in a folder of the server, and copy the example to
   `.env`.
2. Fill in `.env`: the domain, the folder of your books, a setup code of your own (at least 12 characters), a long
   PostgreSQL password, and Traefik's network, entry point and certificate resolver as named in its configuration
   (default `traefik`, `websecure`, `letsencrypt`).
3. `docker compose up -d`, then open `https://books.example.com` and set up the first administrator with your code
   ([first start](first-start.md)). Without a code of your own, `docker compose logs librarion` shows the one the
   server made.
4. In **Administration › Libraries**, add the folder **`/books`** (your books folder, read-only inside the
   container) to a library and scan it.

- **Updates:** `docker compose pull && docker compose up -d`.
- **Back up** both volumes: `librarion-postgres` (the catalog) and `librarion-data` (sign-in keys, uploaded covers;
  losing the keys signs everyone out). Caches in `librarion-data` can be deleted.
- **Visitors' addresses:** Librarion believes the forwarded address only from Traefik, by default anywhere in
  Docker's ranges (`172.16.0.0/12`). If your Docker uses other ranges, set `LIBRARION_TRUSTED_PROXIES` to the range of
  Traefik's network (`docker network inspect traefik`), or the sign-in throttle and the security log see Traefik's
  address instead of each visitor's.
- The session cookie is marked secure: open it only by `https://`.

## On your own network only

```sh
docker run -d --name librarion -p 5090:8080 -v librarion-data:/app/data -v /path/to/books:/books:ro \
  ghcr.io/otools-net/librarion:latest
docker logs librarion        # the setup code
```

Then `http://<the machine's address>:5090`. The database is SQLite in the `librarion-data` volume; for large
libraries use PostgreSQL ([configuration](configuration.md#database)).

## Settings

Every setting of [configuration](configuration.md) can be given as an environment variable, with `__` between the
parts: `Librarion__Security__SessionDays=60`. Inside the container the server listens on port 8080.
