# Link check

Checks that the relative links of `AGENTS.md`, `README.md`, `agents/**/*.md`, `docs/**/*.md` and
`tests/e2e/README.md` point to files that exist (anchors aren't checked). Node only, no packages.

```sh
node agents/tools/check-links/check-links.mjs     # exit 1 with the list of broken links
```

Run it after moving, renaming or deleting a note (a finished plan especially: AGENTS.md rule).
