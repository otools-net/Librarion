# Tags (planned)

Code (planned): `src/Librarion.Core/Tagging/` (`TagService`, `TagGraph`, `TagClosure`, `ImportedTags`, `ScopeTagger`,
`BookTagRows`, `BuiltInTags`), entities in `Data/Entities/Tags/`; client `src/app/tags/`. Ported from SFE
(`agents/docs/features/tags.md`, `scope-tagging.md`, `default-tags.md` there; model from
https://github.com/TagStudioDev/TagStudio), extended for tags that arrive with imported books.

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
- Picker suggestions by prefix on name/alias/shorthand with an index, capped.
- Tag names are not copied into the books' search documents: free text reaches tags through the tag-name index (D20,
  [metadata-tags](metadata-tags.md)), so a rename or merge never re-indexes books.
- `TagCounts` changes are summed per batch and written once per tag and batch (no hot row for `category:Fiction`
  under parallel imports).

## Where tags show

- Search: `tag:`, `-tag:`, ancestors included, `special:untagged`, hidden rule; `tag:"name (parent)"` for
  disambiguation.
- Content filters ([rights](rights.md)): allowed / denied tags per group, through `TagClosure`, joined at query time
  (D22).
- OPDS: a Tags navigation feed by category → children, only tags the user can see, counts from `TagCounts`; Atom
  `<category>` per book.
- Web: chips on cards and the book page grouped by category, sidebar of tags, tag manager.

## Rights (planned)

`tags.assign` (Write: tag books), `tags.manage` (Write: create, edit, merge, delete definitions),
`tags.importMappings` (Write: imported tags page), `tags.defaults` (Write: library/folder default tags). Reading
tags comes with browsing.

## Tests to have

- Re-import keeps manual links, merges, ignores and suppressions; a removed source tag goes away; hierarchical names
  make the right parents; normalization merges case/spacing variants; a merge moves links in batches; closure stays
  correct after re-parenting (cycle refused); hidden and filtered tags never leak through search, feeds or counts.
