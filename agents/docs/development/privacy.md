# Privacy: fully local

Priority 1 of the project. Ported from SFE's `fully-local.md` and extended to the server.

## Rules

- **Client:** no external URL anywhere in the built app (scripts, styles, fonts, icons, images, readers' workers):
  everything is bundled by npm and served by the app. CSP `default-src 'self'` enforces it in browsers.
- **Server:** no outbound request by default: no update check, no metadata or cover lookup, no Gravatar, no crash
  reporting, no analytics. A feature that needs the internet (later: metadata from Open Library, an update check) is
  **off by default**, switched on by an administrator, says what it sends and where, and is listed in
  `docs/privacy.md`.
- **No telemetry of our own or of a library:** no NuGet/npm package that phones home is added (check before adding);
  framework telemetry is off in the build and the image (`DOTNET_CLI_TELEMETRY_OPTOUT=1`, Angular CLI
  `analytics: false`), the Docker image sets the same.
- **Logs stay on the server**, keep no secrets (passwords, API keys, keys in OPDS paths are redacted) and are purged by
  a retention setting.
- Personal data (reading progress, downloads, shelves) is per user, visible only to that user and, if the tenant
  allows it, to its administrators; a user can export and delete their own.

## Tests guarding it

- `TheWebClientHasNoExternalUrls`: scans the built `wwwroot` for `http(s)://` outside an allow-list (XML namespaces,
  licence texts).
- `TheServerMakesNoOutboundRequests`: an `OutboundGuard` (the runtime's `System.Net.Http` / `NameResolution`
  activity sources: every HTTP request and name lookup of the process) watches while the server's flows run; each
  stage adds its flows (import, browse, OPDS…). `TheOutboundGuardSeesARequest` proves the guard sees one.
- `TheWebClientHasNoInlineScripts`, `TheAngularCliSendsNoAnalytics`.
- e2e guard (`tests/e2e/fixtures/test.ts`): a page request to another origin fails the test.
- Use dotless host names in client specs, so no real domain appears in test data.
