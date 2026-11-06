# Imported tags (the curation page)

Code: `Core/Import/ImportTagResolver.cs` (raw names recorded), `Core/Tagging/ImportedTags.cs` (list, coverage, keep),
`Core/Merging/TagMerger.Imported.cs` (map, ignore, undo of an ignore, the ignores), `Api/Endpoints/ImportedTagEndpoints.cs`;
client `admin/imported-tags-section.ts`, `admin/imported-map.ts`, styles at the end of `styles/admin.css`; tests
`ImportedTagTests` (both providers), `admin/imported-tags.spec.ts`, e2e `specs/imported-tags.spec.ts` (every device,
axe on the page in both themes). Built in stage 6 chunk 9; the model and the import rules are in
[tags](tags.md#imported-tags). User docs: `docs/administration/imported-tags.md`.

## The user's answers (2026-11-06)

- The importer records each raw name as an automatic mapping, for plain tags and every field but people
  (`author`, `translator`, `editor`, `illustrator`) and `series` (~600k values at 2M books: curated by merges and the
  duplicate suggestions of chunk 14). `ImportTagResolver.UnrecordedFields` is the list.
- The page is per library: counts are `TagCounts.BookCount` (direct links, manual and rule ones included: links
  don't say which raw name gave them), paged by key on the new index `TagCounts (LibraryId, BookCount, TagId)`.
- Ignore is logged and undoable; the tag stays, its manual and rule links stay.
- The exit criterion's coverage is measured with today's generator and reported, not forced (see Scale).

## Raw names

- `ImportTagMappings` gained `Name` (the raw name as first met, trimmed) and `Reviewed`. A name settled by a tag name, an
  alias or a new tag gets a row (`Reviewed` false), written once per batch with the resolver's other rows; a name
  settled by its mapping writes nothing. A re-import of books from before this change writes the missing rows.
- No unique index: two imports of one tenant at once can both write a name (the lookup takes either; the page shows
  the tag once). Ignored rows have `TagId` null, so deleting the tag doesn't delete the ignore.
- A merge (from Tags, or Map here) moves the source's mappings to the target and marks them reviewed; its undo puts
  back the ones that weren't (`TagData.Unreviewed`).

## The list (`GET api/imported-tags`)

- A row is a **tag** with imported names (several raw names, from several sources, can give one tag): its books in
  the library, its names with their sources. Filters: field (`field` key, none = plain tags), `source`, `status`
  (`review`: a name not reviewed; `reviewed`: all are; `all`), on the mappings that stand (not ignored).
- **Small field** (at most `SmallField` = 1,000 tags with such names, a capped distinct count on
  `(TenantId, FieldId, TagId)`): its tags read whole, their counts in the library by the `TagCounts` key, sorted in
  memory; exact.
- **Big field** (plain tags: 50k at 2M): `TagCounts` of the library walked by `(BookCount, TagId)` descending a chunk
  of 2,000 candidates at a time, each chunk checked against the mappings by id; at most 10 chunks per request. A walk
  that stops before filling the page answers the rows found and a cursor where it stopped (the client's More goes on),
  so a filter that matches little never reads the whole table in one request. People and series values sit in the
  same index and are walked past (about 2-3 candidates per plain tag at 2M).
- `GET api/imported-tags/summary`: tags, links, the first 500's links, the reviewed tags' links (four aggregates over
  the field's mapped tags in the library: proportional to the field's vocabulary, asked once per choice, never per
  page).

## Changes

- Rights: reading needs `library.importedTags` in that library; a change (Keep, Map, Ignore, undo) touches the
  tenant's mappings and links in every library, so it needs the right in **every** library of the tenant (403
  otherwise; `GET api/imported-tags/context` says `canChange`), and Map, a merge, also `tenant.tags` (`canMap`).
- At most 100 tags per change (`tags.count`). More than 10 tags, or more than 10,000 links, run as one tenant run:
  Map as `TagMerge`, Ignore as `TagIgnore`.
- **Keep** (`POST …/review`): the tags' standing mappings `Reviewed` (or not, Review again).
- **Map** (`POST …/map`): `TagMerger.MapAsync`, every tag checked as a merge first (refused whole), then one
  `MergeLog` per tag, undone one by one under Tags › Merges.
- **Ignore** (`POST …/ignore`): per tag a `MergeLog` of kind `Ignore` (source = target = the tag). First its standing
  mappings become ignored (`TagId` null, `Reviewed`; the ids and the unreviewed ones in the log's data), so an import
  running meanwhile stops giving it at its next batch (a resolver's cache may still hold it until then); then its
  `Imported` links are taken off by batches of 1,000 books, each kept in `MergeLogLinks`, counts by delta. Refused
  for a tag without a standing mapping (`tag.notImported`).
- **Undo of an ignore** (`POST …/ignored/{id}/undo`, or Tags › Merges): mappings back to the tag (unreviewed where
  they were; one mapped again since keeps that), its logged links back to the books still there and without one.
  Blocked only when undone or when the tag is gone: what came to the tag later stays. A later ignore of a merge's
  target blocks that merge's undo (`merge.later`), as any later change of the target does.
- `GET …/ignored`: the ignores that stand, newest first, with the names they ignored.

## Scale (measured 2026-11-06, see [scale](../development/scale.md#measured))

- Imports with the new index and the mapping rows (PostgreSQL): INPX 1M 9.7 min (9.1 before), Calibre 2M 31.3 min
  (28.9 before, **over its 30 min budget**), unchanged re-imports 35 s and 2.6 min. The user kept the index; imports
  were made faster: Calibre 2M 25.4 min with the new files read in parallel
  ([stage 6 plan](../development/stage-6-curation-plan.md), [imports](imports.md#scale)).
- The page's queries on the generated 2M catalog (`run --only imported`, 2026-11-06), p95 PostgreSQL / SQLite: plain
  tags first page 23 / 28 ms, the page after the 5,000th 25 / 27 ms, a status none has (the bounded walk) 57 / 140 ms
  (SQLite over the list budget, recorded), languages 8 / 4 ms, categories 14 / 14 ms, the fields 46 / 5 ms; the
  coverage line computed 245 / 184 ms against a budget of 500 ms for figures asked once and kept (`OnDemandBudget`,
  set in this chunk), kept 0 ms.
- What the benchmark found: four aggregates as joins read `TagCounts` whole on PostgreSQL (480 ms: now the tags through
  the mappings' index and their counts by key, chunked; SQLite one join, as its long parameter lists took 2 s); a
  `long` cursor against the `int` column made SQLite compare a cast, off the index (each chunk read from the top);
  10 chunks a request took 225 ms (now 4: `MaxChunks`); an empty page asked `WHERE 0` of `Tags` (a scan to the plan check).
- Coverage, the stage's exit criterion (the generator as it is, the user's answer): the first 500 plain tags hold
  **59.1 %** of their 3.2M imported links (50k tags at Zipf 1.0), the first 500 categories (630) 95.4 %.
- The coverage line is kept per tenant, library, field and source until a tag change (`AccessChanges`) or for 2 minutes.

## Known limits

- Counts include manual and rule links (a tag tagged by hand counts those books too); fine for ordering, not an exact
  "imported" count.
- A run of the page (`TagIgnore`, `TagMerge`) is a tenant run, which only `tenant.tags` holders follow
  (`TaskEndpoints.RunOf`): an account with `library.importedTags` alone (no built-in group is one) starts it but its
  page isn't read again when it ends (the list is right after a reload).
