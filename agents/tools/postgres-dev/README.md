# PostgreSQL for development

A local PostgreSQL 17 in Docker (container and volume `librarion-pg`, port 55432 on 127.0.0.1 only), for running the
app and the backend tests on the second provider. Docker is the only requirement.

```sh
# start or reuse it; prints the connection string
agents/tools/postgres-dev/start.sh

# the backend tests on PostgreSQL (each test makes and drops its own database)
LIBRARION_TEST_POSTGRES="$(agents/tools/postgres-dev/start.sh)" dotnet test

# the app on PostgreSQL
dotnet run --project src/Librarion.Api --Librarion:Database:Provider=postgres \
  --ConnectionStrings:Librarion="$(agents/tools/postgres-dev/start.sh | sed 's/Database=postgres/Database=librarion/')"

# stop it / remove it with its data
docker stop librarion-pg
docker rm librarion-pg && docker volume rm librarion-pg
```

CI uses a `postgres:17` service the same way (`.github/workflows/build-test.yml`).
