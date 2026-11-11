# Commands

```sh
dotnet build ; dotnet test                                       # the Api build runs npm ci + ng build into wwwroot
dotnet build -p:LibrarionSkipWebClient=true                      # without the client
LIBRARION_TEST_POSTGRES="$(agents/tools/postgres-dev/start.sh)" dotnet test   # the backend tests on PostgreSQL
LIBRARION_OPDS_SCHEMAS=D:/Temp/opds-validate/cache dotnet test   # also checks the OPDS feeds with jing (agents/tools/opds-validate: run its fetch once)
cd src/Librarion.Web ; npm test ; npm run build ; npm start      # client: Vitest, build into wwwroot, ng serve :4200
cd tests/e2e ; npm ci ; npm run browsers ; npm run e2e           # end-to-end (testing-e2e.md)
dotnet run --project src/Librarion.Api                           # :5090, SQLite in src/Librarion.Api/data/
dotnet tool restore                                              # once, for dotnet-ef
dotnet msbuild src/Librarion.Api/Librarion.Api.csproj -getProperty:Version   # current version
```

- Migrations (two per change): [database](../development/database.md).
- `npm start` proxies `/api`, `/openapi` and `/opds` to `:5090` (`proxy.conf.json`): run the Api alongside.
- Build mechanics and errors: [build](build.md).

## Long runs in the background

The user's rule (2026-11-08): a run in the background expected to take more than 10 minutes (a 2M benchmark, a full
test or e2e suite, a big import) gets a progress report every 10 minutes until it ends.

- Write the run's output to a log (`… > D:/Temp/librarion-bench/<name>.log 2>&1`), start it with `run_in_background`.
- Create a recurring cron job (`CronCreate`, e.g. `*/10 * * * *`) whose prompt reads the log's last lines and tells
  the user in a line or two what step it is on and the numbers so far; nothing else (no new work started by it).
- Delete the job (`CronDelete`) as soon as the run's completion arrives; the report of the end replaces it.
