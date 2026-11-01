# Commands

```sh
dotnet build ; dotnet test                                       # the Api build runs npm ci + ng build into wwwroot
dotnet build -p:LibrarionSkipWebClient=true                      # without the client
LIBRARION_TEST_POSTGRES="$(agents/tools/postgres-dev/start.sh)" dotnet test   # the backend tests on PostgreSQL
cd src/Librarion.Web ; npm test ; npm run build ; npm start      # client: Vitest, build into wwwroot, ng serve :4200
cd tests/e2e ; npm ci ; npm run browsers ; npm run e2e           # end-to-end (testing-e2e.md)
dotnet run --project src/Librarion.Api                           # :5090, SQLite in src/Librarion.Api/data/
dotnet tool restore                                              # once, for dotnet-ef
dotnet msbuild src/Librarion.Api/Librarion.Api.csproj -getProperty:Version   # current version
```

- Migrations (two per change): [database](../development/database.md).
- `npm start` proxies `/api` and `/openapi` to `:5090` (`proxy.conf.json`): run the Api alongside.
- Build mechanics and errors: [build](build.md).
