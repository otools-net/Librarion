# The request pipeline

Code: `src/Librarion.Api/Program.cs`, `Security/` (`SecurityHeaders`, `RequestGuard`), `ClientPage.cs`. Sessions:
`Security/LibrarionSession.cs` (cookie), `CurrentAccess.cs`, `RightMetadata.cs` (`RequireRight`), `SignInThrottle.cs`;
Basic / API keys for OPDS come in stage 7 ([opds-auth](../features/opds-auth.md)).

- **Order:** `UseForwardedHeaders` (loopback proxies) → `SecurityHeaders` (first, so errors carry them too) →
  exception handler, status code pages → `RequestGuard` → authorization → `/openapi`, static files
  (`UseDefaultFiles` + `MapStaticAssets`), `/api` group (its own 404 fallback), then `ClientPage` for every other
  address.
- **CSP** `default-src 'self'` (`SecurityHeaders.AppPolicy`): only the app's own files load or run; styles allow
  `'unsafe-inline'` (Angular adds component styles as `<style>`). The built `index.html` must have no inline script or
  handler: `inlineCritical: false` in `angular.json` (Angular's critical CSS adds an `onload` handler);
  `TheWebClientHasNoInlineScripts` guards it.
- **`X-Librarion-Request`** on every non-GET `/api` request (400 without it): a page of another site can't add it
  without a CORS preflight, which never succeeds.
- **Deep addresses:** `index.html` has `<base href="./">`, right at the root only; `ClientPage` serves the page for
  client routes with `<base href="{PathBase}/">`, so `/a/b/c` loads `/main-….js`, under a virtual directory too
  (found by the e2e smoke set; `ADeepClientAddressGetsThePageWithTheAppsRootAsBase`).
- Every `/api` and `/openapi` route names its right or `.AllowAnonymous()` (`EveryRouteNamesARight`); in stage 0
  all are anonymous (no accounts yet).
- **Order (stage 1):** … → `UseAuthentication` → `RequestGuard` (header check, then 503 `setup-required` except
  `api/setup` and `api/session`) → `UseRateLimiter` (`sign-in` policy per address) → `UseAuthorization`.
- **Session cookie** `librarion.session-<port>` (SFE's per-port names): user id, security stamp, tenant; stamp checked
  on every request (`LibrarionSession.ValidateAsync`); keys in `Librarion:Security:KeysDirectory` (`data/keys`).
- `builder.WebHost.UseStaticWebAssets()` in Program.cs: `dotnet run` without a launch profile runs as Production and
  otherwise warned "Static Web Assets are not enabled" and couldn't serve the built client.
- `appsettings.json` `Urls` (5090) wins over `ASPNETCORE_URLS`: pass `--urls` to run on another port.
