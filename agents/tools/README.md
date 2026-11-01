# Agent tools

Reusable helpers for working on Librarion, one folder per job, each with its own `README.md` (AGENTS.md rule). Work
output goes to a temp folder, never into the repository.

| Tool | What |
|---|---|
| [postgres-dev](postgres-dev/README.md) | a local PostgreSQL 17 in Docker for the app and the backend tests |
| [check-links](check-links/README.md) | relative links of `AGENTS.md`, `agents/` and `docs/` resolve |
| [screenshots](screenshots/README.md) | every page at 360 px and 1280 px as PNGs, on its own set-up e2e server |
| [benchmark](benchmark/README.md) | generated 2M / 10M catalogs (both providers) and the query benchmark with budgets and plan checks |
