# C# code style

Settings: `.editorconfig`. As SFE's.

- Primary constructors, file-scoped namespaces, collection expressions, `[GeneratedRegex]`.
- XML doc comments explaining *why*; match the surrounding comment density.
- Private fields `_camelCase`; `var` where the type is apparent.
- Warnings are errors on CI (`CI=true` in `Directory.Build.props`): fix them locally too.
- Package versions only in `Directory.Packages.props`; a new package is checked for telemetry first
  ([privacy](privacy.md)).
- Client style: [frontend](frontend.md).

## Minimal API gotchas (found in stage 1)

- A route lambda whose only parameter is `HttpContext` binds as a `RequestDelegate`: its returned result is dropped
  (200, empty body). Add another parameter (`CancellationToken _`), as SFE does.
- Enum query parameters bind case-sensitively (`level=tenant` → 400): take a `string` and `Enum.TryParse(…, ignoreCase: true)`.
