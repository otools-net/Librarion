# Versioning and changelog

Files: `Directory.Build.props` (`<Version>`), `CHANGELOG.md`. Same workflow as SFE.

## Branches

- **`edge`**: all work is committed there until it is ready. No version bump there.
- **`main`**: gets `edge` when the user says it is ready, with the version bump in the merge. Never commit work
  directly on `main`.
- Commit messages: `claude: <short summary>`; commit only when asked.

## Versioning

`<Version>` is semantic `major.minor.patch`. It **starts at `0.0.0`** (decided 2026-10-31) and changes **only when
`edge` is merged into `main`**, from the `## Unreleased` section of `CHANGELOG.md`:

- only `### Fixed` → bump patch;
- any `### Added` / `### Changed` → bump minor, reset patch;
- docs, tests, build scripts, `AGENTS.md`, `agents/` only → no bump.
- **`1.0.0` is the MVP**, and only the user declares it: until then the major stays `0`, whatever is merged (so
  stage merges give `0.1.0`, `0.2.0`, …). After 1.0.0, a major only when the user asks.

## Changelog

- Each commit on `edge` with a user-visible change adds its lines under `## Unreleased` at the top of `CHANGELOG.md`:
  `### Added` / `### Changed` / `### Fixed`, one short user-facing line per item. Not for docs, tests or build scripts
  alone.
- The merge into `main` renames `## Unreleased` to `## x.y.z`.
- Tags `v<Version>` (which publish images) are pushed only when the user asks.
