# OPDS feed validation

Checks OPDS catalog feeds against the official RELAX NG schemas (OPDS 1.2 and Atom, RFC 4287) with **jing**, the
reference RELAX NG validator (stage 7 D-7.10, agents/docs/features/opds.md). Needs Node and Java 11+.

```sh
# once: jing, opds.rnc and RFC 4287 into the cache folder, each checked by SHA-256; atom.rnc cut out of the RFC
node agents/tools/opds-validate/validate.mjs fetch

# feeds saved as .xml (files, or folders of them)
node agents/tools/opds-validate/validate.mjs feeds/ root.xml

# the backend test that writes one feed of every kind and checks it with jing (else it ends inconclusive)
LIBRARION_OPDS_SCHEMAS=<cache folder> dotnet test --filter EveryFeedKindMatchesTheOpdsSchemas
```

- **Cache folder:** `LIBRARION_OPDS_SCHEMAS`, else `<tmp>/librarion-opds-validate` (on this machine `D:\Temp\...`:
  big temporary files go to D:). Nothing goes into the repo; validating never downloads.
- **Pinned sources:** `org.relaxng:jing:20241231` (Maven Central, BSD), `schema/1.2/opds.rnc` of
  `opds-community/specs` at commit `6823741`, `rfc4287.txt` from the RFC Editor. A changed file is refused: update
  the URL and the SHA-256 in `validate.mjs` together.
- **Downloads:** Node's `fetch`, else `curl`: this machine's firewall may let one through and not the other (Node got
  `EACCES` while curl worked, 2026-11-11).
- **The schema's bug, mended:** `opds.rnc` (1.1 and 1.2 alike) excludes `[ <>{}|^`"\nrt]` from IRIs: the backslashes
  of `\r` and `\t` were lost (RELAX NG compact strings have no such escapes), so every address or id holding the
  letter r or t is refused. `fetch` writes `opds-fixed.rnc` with `\n\r\t`, and validation uses it; it fails loudly if
  the upstream text changes.
- **One known difference, by choice:** each feed with search links the OpenSearch description and also the template
  itself (`…/search?q={searchTerms}`, for reader apps that read only that form; chunk 7). Braces make it no IRI, so
  jing reports that one link; the backend test removes exactly it (checking the description link is there) before
  validating. Run raw over a server's feeds, expect that one error per feed.
- CI (`.github/workflows/build-test.yml`) runs `fetch` into `$RUNNER_TEMP` and sets `LIBRARION_OPDS_SCHEMAS`, so the
  test checks the schemas on every backend job.
