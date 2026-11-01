# First start (setup)

Code: `src/Librarion.Core/Access/SetupService.cs`, `GeneratedSetupCode.cs`, `AccessOptions.cs` (`SetupOptions`),
`src/Librarion.Api/Security/SetupCodeNotice.cs`, `RequestGuard.cs`, `Api/Endpoints/SetupEndpoints.cs`; client
`account/setup-page.ts`. Ported from SFE (`setup.md` there). User side: [first start](../../../docs/first-start.md).

- Until the first server administrator exists, every API route but `api/setup` and `api/session` answers **503
  `setup-required`** (`RequestGuard`); the client opens `/setup`.
- **Setup code:** `Librarion:Setup:Code` (at least 12 characters, compared exactly), or else the server makes one
  (`GeneratedSetupCode`: 4 groups of 4 from an alphabet without 0/O/1/I/L, compared without dashes, spaces or
  case), writes it to `Librarion:Setup:CodeFile` (`data/setup-code.txt`, relative to the content root) and prints it
  in a box on the console at start (`SetupCodeNotice`). Never logged.
- `POST api/setup` (code, name, password, tenant and library names) makes the server administrator, the first tenant
  with its built-in groups and library, logs `setupCompleted`, and signs the administrator in. A wrong code is logged
  as a failed sign-in; the route has the sign-in rate limit.
- **Recovery mode** `Librarion:Setup:Recover`: the page opens again on a set-up server to reset a local server
  administrator's password, or add one; turn it off afterwards (the log warns). The usual way is the sign-in page's
  recovery ([accounts](accounts.md)).
- `GET api/setup` says whether it is needed, available, and where a generated code is kept (only while needed).
- Not ported from SFE yet: setting up from the server's own computer without a code.
- The e2e tests set the code by environment and set up through the page once, in the `first-start` project
  ([testing-e2e](../development/testing-e2e.md)).
