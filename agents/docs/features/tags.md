# Tags

Tables built (stage 2): `Tags`, `TagAliases`, `TagParents`, `TagClosure`, `BookTags`, `TagCounts`, built-in tags
([catalog](catalog.md)). Built (stage 3): imported tags at scan time (`Core/Import/ImportTagResolver`,
`ImportTagMappings`; tests `LibraryScannerTests`): a source's mapping first, then a tag of that name or alias, else a
new plain tag under the source's "Imported: <source>" category, a batch at a time with a bounded name cache. Built
(stage 6, chunks 2-3): the tag service and merges; (chunk 4) the tag manager, below. Tagging books in the UI is planned.

Code (planned): `src/Librarion.Core/Tagging/` (`TagService`, `TagGraph`, `TagClosure`, `ImportedTags`, `ScopeTagger`,
`BookTagRows`, `BuiltInTags`), entities in `Data/Entities/Tags/`; client `src/app/tags/`. Ported from SFE
(`agents/docs/features/tags.md`, `scope-tagging.md`, `default-tags.md` there; model from
https://github.com/TagStudioDev/TagStudio), extended for tags that arrive with imported books.

## The tag service (built, stage 6)

Code: `Core/Tagging/TagService.cs` (definitions, delete), `TagClosures.cs` (closure upkeep), `TagLinks.cs` (counts and
sort columns per batch), `Core/Merging/TagMerger.cs` (merges, undo: [merging](merging.md#duplicate-tags)),
`Api/Endpoints/TagEndpoints.cs`; tests `TagCurationTests` (both providers).

- Routes (`tenant.tags`): `GET api/tags/{id}/definition`, `POST api/tags`, `PUT api/tags/{id}`, `DELETE api/tags/{id}`,
  `POST api/tags/{id}/merge`, `POST api/fields/{key}/merge`, `GET api/merges`, `POST api/merges/{id}/undo`. A change of
  more than 10,000 links (D-6.4; the sum of the tag's `TagCounts`, never a count of links) or one asked while a run
  goes in the tenant answers **202** with a tenant run ([tasks](tasks.md#runs)); otherwise 200 when done.
- Definitions: name, sort name (people's fields: "Last, First" by default), shorthand, color (`#rgb` / `#rrggbb`),
  category, hidden, aliases (at most 50; one equal to the name dropped), parents (at most 20) and a disambiguation
  parent (one of the parents). A plain tag's parents are plain tags; a value's are values of its own field (the field
  itself is its `FieldId`, no `TagParents` row); a field has no parents. Cycles refused through the closure. A value's
  name is unique in its field unless a disambiguation parent tells them apart. Built-in fields keep their names (the
  client translates them). A changed sort name rewrites `Books.SortAuthor` of its books in one statement.
- **Closure upkeep** (`TagClosures.RebuildAsync`): after the parents of some roots change, the ancestors of everything
  under them (D) are rebuilt set-based: pairs inside D kept, pairs from outside dropped, then made again from each
  edge leaving D (a parent, or a value's field) times that parent's ancestors. Valid for several roots at once while
  every changed edge points out of D (re-parenting, a delete's children, a merge's moved children). No tag graph is
  kept in memory yet, so there is no version to bump; tag changes bump `AccessChanges` (cached visibility reads
  closure-derived lists).
- **Delete:** links removed a batch of 1,000 books at a time (`TagLinks`: counts by delta, sort columns refreshed),
  then the tag (closure, counts, aliases, mappings by cascade), its children's closure rebuilt. Refused: built-in tags
  (`tag.builtIn`), a field with values (`field.notEmpty`: merge it), a tag a content filter names (`tag.inFilter`:
  deleting it would change who sees what). A deleted imported tag comes back with the next import of its books
  (ignoring a raw name is the Imported tags page's job, chunk 9).
- `TagCounts` hold two counts per library of present books (not missing): `BookCount`, the tag's **direct** links
  (what search sizes a term by, what a delete or merge moves), and `TotalCount` (stage 6, 4a: the user asked
  2026-11-05), its books **with those of every tag under it**, each once (a field's row: its books with any value).
  Lists, a tag's page, suggestions, Refine and the tag manager show the total; the manager adds "N tagged with it"
  when the two differ.
- How the total is kept (`TagCountChanges`, `TagAncestors`, `TagClosures.CountAsync`): every change gives each
  touched book's tags before and after; their ancestors come from `TagClosure` through a cache per import (cleared
  past 200k tags, and when `TagClosures.Version` moved: a rebuild while a scan runs); a tag gains a book it is now
  above and wasn't, loses one it no longer is. Both counts go in the one upsert per thousand tags the importer wrote
  before. A closure rebuild records each changed tag's ancestors outside the subtree before and after; only the
  ancestors that differ are recounted, over the present books under the changed tags (their other tags' ancestors
  counted in), so the cost follows the books moved, never the size of a big category above them. Re-parenting a tag
  with more books under it than the threshold rebuilds the closure at once and counts in a tenant run (`TagCounts`).
- Known limit: a closure change while an import writes a batch can leave that batch counted with the old ancestors
  (the version is read per batch). No full recount exists yet: a drift stays until the books are counted again.

## The tag picker (built, stage 6 chunk 5)

Code: `shared/tag-picker.ts` (`TagPicker`, `lib-tag-picker`), styles `styles/tags.css` (with the color dot); server
`Core/Tagging/TagPicker.cs`, `GET api/tags/pick?field&q&limit` (`library.browse`; the caller's content filters apply);
tests `shared/tag-picker.spec.ts`, `TagPickerTests` (both providers), e2e `specs/tags.spec.ts` (a tap on phones, the
arrows and Enter on the desktop). A port of SFE's `tag-picker.component.ts`.

- The same component wherever a tag is chosen: `field` scopes it (a field's key; null: plain tags), `exclude` leaves
  out those already chosen, `allowCreate` adds "Create “name”" unless the server says a tag has that name (`exact`);
  `picked` and `create` are the host's to act on (the host makes the tag, with its own right).
- Matches, each capped at 100 candidates and read by index: the normalized name's prefix (`(TenantId, FieldId,
  NormalizedName)`), another name's prefix (`TagAliases (TenantId, NormalizedName)`, then kept to the field), the
  shorthand whole as typed or in either case (`(TenantId, Shorthand)`, filtered on not null), the name's words (the
  tag-name index, last word a prefix). Ranked: the typed name first, then by books (the totals of 4a, every library),
  then by name; nothing typed: the first by sort name (an arrow in an empty box lists them).
- Each option: color dot, name, the disambiguating parent in brackets, "Also called …" when found only by another
  name, the shorthand, the books. The list is in the flow under the box (not floating: dialogs and phone sheets scroll
  with it), at most 8 options.
- Behaviour from SFE: 150 ms after typing, a later answer wins (earlier ones dropped), Escape closes the list and not
  the dialog (`stopPropagation`), a press elsewhere waits for its release before the list closes (else the tap misses
  the option that moved), `mousedown` on an option keeps the focus in the box.

## The tag manager (built, stage 6 chunk 4)

Code: `admin/tags-section.ts` (Administration › Catalog › Tags: pages `tags` and `merges`), `admin/tag-editor.ts`,
`admin/tag-merge.ts` (parents and a merge's target chosen with the shared [picker](#the-tag-picker-built-stage-6-chunk-5)),
styles at the end of `styles/admin.css`; server `Core/Tagging/TagLists.cs`, routes `GET api/tags/manage/fields`, `GET api/tags/manage`;
tests `tags.spec.ts` (client), `TagCurationTests.TheManagerListsEveryTagByKeyAndFindsThem`, e2e `specs/tags.spec.ts`
(every device), axe on the list, the editor (both themes) and the merges.

- A field chooser (a select: plain tags, then each field with its value count, capped at 10,000 as "10,000+"), the
  list of every tag (books or not) by sort name 50 at a time by key, a search (name prefix on `NormalizedName`, then
  words through the full-text index, one capped page). Each row: color dot, name, books (the sum of `TagCounts`
  across libraries: direct links) and tags directly under it (a `GROUP BY` over `TagParents` for the page's ids).
- Editor: the definition's fields, parents as chips added through `lib-tag-search` (same kind only), the
  disambiguation parent among them; Delete and Merge in its footer, both asking with the number of books first. A
  202 is followed by `TaskWatch.follow` and a toast. Merges: rows with Undo (asks first); a refusal (`merge.later`)
  stays shown.
- Texts `tagAdmin.*` (the `tags.*` keys are the browse pages'). The list is named "Tag list": the navigation's list of
  the section's sub-pages is "Tags", and two lists of one name broke the e2e locators.
- Seen once in a full e2e run (2026-11-05, iPhone): after saving a tag, the search for another one still showed the
  first search's rows; passed alone 4 times and in the next full run. Watch for it.

## Model (as SFE)

- `Tags`: name (not unique), shorthand, color, `IsCategory` (groups its children when a book's tags are listed),
  `IsHidden` (books with it left out of search and feeds unless the search names it or an ancestor), disambiguation
  parent ("Freddy (FNAF)"). `TagAliases`: other names. `TagParents`: child "is a" parent, many parents allowed, no
  cycles; a book tagged with the child is found by the parent.
- Built-ins (edited, never deleted): **Meta Tags** (category), **Archived** (hidden). SFE's "Favorite" is personal in
  a multi-user library: it becomes a per-user shelf, not a tag.
- **Vocabulary per tenant**, shared by its libraries (one merge or filter applies everywhere); counts per library.
- **Metadata is tags too:** authors, series, categories, publishers, languages… are tags under a **field**
  (`author:"Stephen King"`, `category:Horror`), with order and series number on the link: see
  [metadata-tags](metadata-tags.md). This note covers plain tags and the machinery both share.

## Imported tags

Books bring their own tags: Calibre tags (and hierarchical ones, `Fiction.Fantasy`), EPUB `dc:subject`, FB2 genres,
PDF keywords, authors, series and every other metadata field, BISAC-like paths (`Fiction / Fantasy / Epic`). Thousands of messy names (`fiction`, `Fiction`,
`Fiction - General`) are the norm at 2M books.

- Every book–tag link has a **source**: `Imported` (from the book or Calibre), `Manual` (a user), `Rule` (library or
  folder default tags, scope tagging by a rule). Same tag, several sources possible.
- Raw names go through **`ImportTagMappings`** (tenant, source kind, normalized raw name → tag id, or *ignored*):
  normalized = trimmed, case-folded, Unicode NFKC, collapsed spaces. Lookup order: mapping → tag name or alias
  (unique match) → new tag. New tags go under a category per source ("Imported: Calibre", "Imported: EPUB") so they
  never clutter curated categories until someone moves them.
- FB2 genre codes (`sf_fantasy`), EPUB `dc:subject` and PDF keywords become values of the **`category:`** field
  ([metadata-tags](metadata-tags.md)), FB2 genres named from a bundled genre list (as MyHomeLib's
  `Installer/GenreLists/`), in the UI's languages; still through the mappings. Calibre tags and FB2 `<keywords>` stay
  plain tags.
- Hierarchical raw names (`A.B` for Calibre, `A / B / C`, `A > B`) create or reuse the parents chain (separators
  configurable per library; off by default for EPUB, whose subjects contain dots).
- **Curating keeps through re-imports** (finding duplicate tags and fields to merge: [merging](merging.md)):
  merging tag X into Y moves X's links (batches) and turns X's names into aliases of Y and its mappings to Y; *ignore* drops the links and maps the raw name to nothing; renaming or
  re-parenting changes nothing for the importer.
- **Re-import** recomputes only `Imported` links of the books it reads: adds new ones, removes those the source no
  longer has. `Manual` and `Rule` links are never touched. A user removing an imported tag from a book writes a
  **suppression** (book, tag) so the next import doesn't bring it back (SFE's rule: a tag taken off stays off).
- Admin page **Imported tags**: raw names with book counts, their mapping, bulk map/merge/ignore, sorted by count
  (curating the top few hundred covers most books).
- Writing tags back into book files or Calibre: not planned.

## Tagging (as SFE)

- Book, selection, and **scope tagging** (a search, an author, a series, a whole library): counted with one
  `GROUP BY`, changed in batches of 1000 keyed on `Books.Id`, confirmed in the dialog, never listing the books.
- **Default tags** of a library and, for folder-scanned libraries, of folders (inherited by sub-folders): given once
  when a book enters the catalog or moves in, as `Rule` links; adding one asks "new books only or also those there".

## Scale

- **`TagClosure`** (ancestor, descendant, depth), kept by the tag service: "has tag or a descendant" (search, content
  filters, hidden rule) is one join, not an `IN` list of thousands of ids.
- `BookTags (BookId, TagId, Source)` with indexes `(TagId, BookId)` and `(BookId)`; counts per library in
  `TagCounts (LibraryId, TagId, BookCount)` (direct + via descendants), kept by imports and tag changes, recomputed by
  a task.
- `TagGraph` (names, aliases, parents) in memory per tenant, reloaded on a version change (50k tags ≈ a few MB); the
  big fields' values (authors, series) stay in the database, looked up by index ([metadata-tags](metadata-tags.md)).
- Picker suggestions by prefix on name/alias, whole shorthand and words, each by an index and capped (`TagPicker`).
- Tag names are not copied into the books' search documents: free text reaches tags through the tag-name index (D20,
  [metadata-tags](metadata-tags.md)), so a rename or merge never re-indexes books.
- `TagCounts` changes are summed per batch and written once per tag and batch (no hot row for `category:Fiction`
  under parallel imports).

## Where tags show

- Search ([search](search.md)): `tag:`, `-tag:`, the tags under it included, the hidden rule (built); `special:untagged` and
  `tag:"name (parent)"` for disambiguation (planned).
- Content filters ([rights](rights.md)): allowed / denied tags per group, through `TagClosure`, joined at query time
  (D22).
- OPDS: a Tags navigation feed by category → children, only tags the user can see, counts from `TagCounts`; Atom
  `<category>` per book.
- Web: chips on cards and the book page grouped by category, sidebar of tags, tag manager.

## Rights (built in stage 6, chunk 1: [rights](rights.md))

`library.tag` (Write: tag books), `tenant.tags` (Write: create, edit, merge, delete definitions),
`library.importedTags` (Write: imported tags page), `library.defaultTags` (Write: library/folder default tags). Reading
tags comes with browsing.

## Tests to have

- Re-import keeps manual links, merges, ignores and suppressions; a removed source tag goes away; hierarchical names
  make the right parents; normalization merges case/spacing variants; a merge moves links in batches; closure stays
  correct after re-parenting (cycle refused); hidden and filtered tags never leak through search, feeds or counts.
