# C# code style

Settings: `.editorconfig`. As SFE's.

- Primary constructors, file-scoped namespaces, collection expressions, `[GeneratedRegex]`.
- XML doc comments explaining *why*; match the surrounding comment density.
- Private fields `_camelCase`; `var` where the type is apparent.
- Warnings are errors on CI (`CI=true` in `Directory.Build.props`): fix them locally too.
- Package versions only in `Directory.Packages.props`; a new package is checked for telemetry first
  ([privacy](privacy.md)).
- Client style: [frontend](frontend.md).
