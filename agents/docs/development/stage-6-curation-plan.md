# Stage 6: tag management and curation (plan)

Roadmap: [foundation-plan](foundation-plan.md) (D9, D15, D18, D19, D20, D21, D22). Goal: **curate thousands of
imported tags and fix what the metadata lacks**: tags and fields managed, books tagged and edited one at a time or by
the thousand, covers fixed, duplicate books and tags found and merged, every merge reversible. Version stays on
`edge`; the merge into `main` makes it `0.7.0`.

Written 2026-11-05. Decisions confirmed by the user's answers (2026-11-05), below.

Feature notes this stage builds (their "planned" parts become built): [tags](../features/tags.md),
[metadata-tags](../features/metadata-tags.md), [merging](../features/merging.md),
[book-display](../features/book-display.md) (cover upload, Change cover style, templates on / off),
[rights](../features/rights.md) (tag and edit rights).

Reference: SimpleFileExplorer (`C:\workspace\SimpleFileExplorer`): `agents/docs/features/tags.md`,
`scope-tagging.md`, `default-tags.md`, `duplicates.md`, `selection-and-keys.md`, `uploads.md`; code
`src/Sfe.Web/src/app/tags/` (`tag-manager.dialog.ts`, `tag-editor.dialog.ts`, `tag-picker.component.ts`,
`tag-chip.component.ts`, `file-tags.dialog.ts`, `files-tags.dialog.ts`, `scope-tags.dialog.ts`,
`default-tags-badge.component.ts`). Ported patterns are named as such in the feature notes. calibre-web for how
metadata editing and cover upload behave (behaviour only, its GPL-3 code is never copied).

## What SFE does that Librarion takes (the approach)

- **Tag manager:** one place listing tags (search box, categories as groups, counts), a tag editor dialog (name,
  shorthand, color, category, hidden, aliases, parents with a picker, disambiguation parent), merge and delete from
  it, both asking first and saying how many items change.
- **Tag picker:** type to find by name, alias or shorthand (prefix, capped), arrows and Enter, a chip per chosen tag
  with its color, "Create *name*" when nothing matches; the same component everywhere a tag is chosen.
- **Tagging one, a selection, a scope:** one item's tags in a dialog; a selection's tags show which all / some have
  (tri-state); scope tagging (everything under a folder, here a search, an author, a library) counts first, confirms,
  then runs in batches without listing the items.
- **Default tags:** a folder's tags given to what enters it, a badge on the folder, "new items only or also those
  there".
- **Selection:** long press on phones, click / Ctrl / Shift on desktops, a selection bar with the count and its
  actions; Escape clears.
- **Duplicates:** groups found by keys, a page of them, compare side by side, keep one.

## Decisions for this stage (confirmed by the user, 2026-11-05)

- **D-6.1 Rights first:** `tags.assign` (tag books with plain tags), `tags.manage` (create, edit, merge, delete tag
  and field definitions), `tags.importMappings` (the Imported tags page), `tags.defaults` (library and folder default
  tags), `books.edit` (title, fields, description, rating, series number, cover; this is the `metadata.edit` of the
  notes, named as the other book rights), `books.merge` (merge, split, undo, not a duplicate). Library-scoped except
  `tags.manage` (tenant: the vocabulary is per tenant, D9). Default groups: Administrators all of them, and a new **built-in Curators group** (Q1) with all of them but no
  account, group or tenant rights. **Built (chunk 1) with ids by scope** (the catalog's rule): `library.tag`,
  `library.edit`, `library.merge`, `library.importedTags`, `library.defaultTags`, `tenant.tags`; below, the old
  names stand for these. Curators also hold the Users' rights.
- **D-6.2 Where curation lives:** a new administration group **Catalog** (Tags, Imported tags, Duplicates, Cover
  styles), sub-pages as stage 5's; editing a book stays on its page (an Edit button for `books.edit`), tagging a
  selection or scope from the library and search lists.
- **D-6.3 Edits survive re-imports:** an edited title, description, rating, series number or field link is marked as
  `Manual` (links) or kept in an edited-values row (columns); re-imports recompute only `Imported` values (the rule of
  [tags](../features/tags.md#imported-tags)); a removed imported tag writes a suppression. Nothing is written back
  into book files or Calibre.
- **D-6.4 Big changes are background runs:** a merge, delete, scope tagging or bulk merge touching more than a
  threshold of **10,000 links** (Q2) runs as a task in the existing task system (progress panel, cancel, history),
  batches of 1,000 keyed on `Books.Id`; below it, inline. `TagCounts` and `Libraries.BookCount` adjusted per batch,
  never recomputed whole.
- **D-6.5 Undo through `MergeLog`:** every tag merge, field merge, book merge and split writes what moved; undo while
  no later change conflicts (else it says what blocks it). Logs kept (small rows); listed in the Duplicates and Tags
  sections.
- **D-6.6 Duplicate groups stored (Q3):** `DuplicateGroups` / `DuplicateMembers` built once by a background run over the
  catalog (blocking keys only, groups above 50 dropped as noise), then kept by imports and edits; `NotDuplicates`
  pairs and "unique" marks respected across re-imports. The Similar level (edit distance) stays on demand (D21).
- **D-6.7 Covers:** upload (JPEG / PNG / WebP, size and pixel caps, decoded and re-encoded, hostile images refused)
  stored as files under the work folder per tenant, the database keeping the path and hash (Q5); Change cover style picks a template and palette (kept across re-imports);
  templates on / off per tenant with "reassign books of this template" as a background run (D15).

## The user's answers (2026-11-05)

- **Q1 Curators:** a built-in Curators group (tag, edit, merge, imported tags; not accounts, groups or tenant
  settings), as Administrators: can't be deleted.
- **Q2 Threshold:** 10,000 links; above it a background run (progress panel, Cancel, history), below it inline.
- **Q3 Duplicate groups:** stored, built once by a background run, then kept by imports and edits.
- **Q4 Same-file merge at import:** not asked; today's behaviour kept as the default (on), the tenant setting turns it
  off. The user may change it.
- **Q5 Uploaded covers:** files in the work folder per tenant; the database keeps the path and hash.

## Todo (the order of work; as stage 5: one chunk at a time, commit it, then ask before the next)

A session may end early: this list is the state of the work. Tick a chunk when it is committed. Chunks are commits on
`edge` only: the version is bumped once, when the stage is complete and merged.

0. [x] The user confirms D-6.1–D-6.7 and answers Q1–Q5; this plan updated.
1. [x] Rights (D-6.1): catalog entries, default groups, texts, `core/rights.ts`, the client hiding what needs them;
       `EveryRouteNamesARight` and the rights editor show them.
2. [x] Tag service: create and edit (name, shorthand, color, category, hidden, disambiguation parent), aliases,
       parents with `TagClosure` kept and cycles refused, delete (links in batches, counts adjusted), the tag graph's
       version bumped; routes; both providers.
3. [x] Tag merge and field merge: links moved in batches (D-6.4), names become aliases, import mappings follow,
       closure and counts kept, `MergeLog` with undo (D-6.5); `genre:` merged into `category:` keeps working in search
       and mappings.
4. [x] Administration › Catalog › Tags (D-6.2): fields as categories with counts, big ones paged and searched (never
       loaded whole), the tag editor dialog, merge and delete dialogs saying how many books change; phones as stage 5.
       Each tag's row also says how many child tags it has (one indexed count per page of tags).
4a. [x] **A parent tag's count includes its children's books** (asked by the user 2026-11-05): today `TagCounts` holds
       a tag's own links only, so "Fiction" shows 0 when its books are tagged "Fantasy" and "Horror". **Cost at scale
       (to confirm with the user before building):** the count must be of distinct books (a book under two children
       counts once), so every link change (import batches, tagging, merges, re-parenting) works out, per batch of
       books, which ancestors each book is under before and after through `TagClosure`: about 10-20 (book, ancestor)
       pairs per book read twice per batch, an import estimated 20-40 % slower (to measure on the 2M catalog);
       re-parenting a tag recounts the books under it (a category over 100k tags: a background run). Fields'
       own rows would get counts too ("books with any author"). **Alternatives:** (a) on demand for one tag, capped
       ("10,000+ books"), nothing stored: cheap to keep, a few hundred ms for a big category, unfit for lists; (b) a
       sum of the children's counts without de-duplication, cheap but wrong for books under two children.
       **Recommendation:** stored totals in a second column `TagCounts.TotalCount` kept per batch as above, after
       measuring the import cost at 2M on both providers; `BookCount` (direct) stays what search sizes terms by.
       **Built** with a cached ancestor lookup in the importer's one upsert per batch; measured 2026-11-05 on
       PostgreSQL: INPX 1M 9.0 min (9.2 before), Calibre 2M 28.4 min (28.2; budget 30), unchanged re-imports 38 s
       and 2.7 min: no measurable cost, far under the 20-40 % estimated.
5. [x] The tag picker (port of SFE's): prefix on name, alias and shorthand through the index, capped; scoped to a
       field; "Create"; chips with colors; keyboard and touch.
6. [x] A book's tags and metadata edited on its page (D-6.3): plain tags (`tags.assign`), fields with the scoped
       picker, author order by drag, series number in place, title, description, rating; suppression when an imported
       tag is removed; search text, sort keys and counts kept; a re-import keeps the edits. **Built:** edited columns as
       flags on `Books` (not a row of values), see
       [book-page](../features/book-page.md#editing-a-book-built-stage-6-chunk-6-d-63).
7. [x] Selection in the library and search lists (long press, click / Ctrl / Shift, the selection bar) and tagging a
       selection (tri-state); scope tagging of a search, an author or a library: counted, confirmed, a background run
       past the threshold.
8. [ ] Default tags of a library and of folders (inherited by sub-folders) as `Rule` links, given when a book enters
       or moves in; "new books only or also those there"; a badge on the folder rows. **The user's answers
       (2026-11-06):** there was no folder view, so one is added (as SFE's, the badge there); folders are indexed
       (a `Folders` table, one row per directory and per ZIP, `FolderId` on files) so "books under a folder" is an
       indexed join, not a read of the root's file rows; a ZIP is a folder (its entries inherit from it and the
       folders above); books from Calibre / INPX imports get the library's and their folders' defaults too. Split:
   - 8a. [x] `Folders` (tenant, library, root, parent, name, path key; per provider migrations), `FolderId` on
         `BookFiles` (a ZIP's folder points to its archive instead of the archive to it), kept by scans, relinks,
         imports and removals: [folders](../features/folders.md). No stored counts: 8b decides how a row's books are
         counted (capped on demand through `(FolderId, BookId)`, or stored) and measures it at 2M.
   - 8b. [ ] The folder view: a library's roots, then folders and ZIPs with their counts, then the books of a folder
         (the book list, keyset, selection and Tag all as other lists); phones and desktop; deep addresses.
   - 8c. [ ] Default tags (`FolderDefaultTags` / library's, `library.defaultTags`): given at entry and on moves as
         `Rule` links, "new only or also those there" (a scope tagging of the folder's books, D-6.4), removing asks
         whether the books keep them; the badge (SFE's `default-tags-badge`) on folder rows and the library.
9. [ ] Administration › Catalog › Imported tags: raw names by source with book counts and their mapping, sorted by
       count, bulk map / merge / ignore; the coverage shown ("the top 500 cover 93 % of imported links"); the
       benchmark's generator makes raw imported names so the exit criterion can be measured.
10. [ ] Covers (D-6.7): upload with Replace / Remove on the book page, Change cover style (template and palette
        picker with previews), Administration › Catalog › Cover styles (templates on / off, reassign as a run).
11. [ ] Duplicate detection (D-6.6): blocking keys stored at import and on edits, the first build as a background run
        (resumable), groups kept by imports and edits, the noise cap, `NotDuplicates`; the same-file setting (Q4: on by default);
        benchmark: the build and the planted duplicates found at 2M within budget. **`fb2-id` is a weak key** (see
        [Found with real books](#found-with-real-books-2026-11-06)): the stored groups keep the on-demand rule (a shared
        FB2 id joins a group only with the same title or first author), never the id alone.
12. [ ] Administration › Catalog › Duplicates and Compare and merge: groups by level and size, compare side by side
        (stacked on phones), choices per single value, merge (files, links, identifiers move; the old id redirects in
        web, API and covers), Not a duplicate; the book page's panel gets the actions for `books.merge`.
13. [ ] Bulk merge (a run over chosen groups, or every same-file group), undo and split (files into a new book, its
        metadata re-read from them).
14. [ ] Duplicate tag and field suggestions: normalized names, name forms ("King, Stephen", "S. King"),
        transliteration, the same name in two fields; ranked by books; spelling distance on demand on a tag's or
        field's page, capped (D21); Not the same remembered; shown on the tag page and in the picker.
15. [ ] Search additions: `special:untagged`, `special:duplicates` (curators), `tag:"name (parent)"`.
15a. [x] **Readable addresses for tag pages** (asked by the user 2026-11-05): a field value's page at
       `/<field key>/<id>/<name>` (`/author/1234/Stephen King`, `/series/88/The Dark Tower`, `/category/9/Horror`), a
       plain tag at `/tag/<id>/<name>`, instead of `/tags/1234` today (`app.routes.ts`, `tags/tag-page.ts`). The id
       finds the tag; the name is decorative: `/author/1234` (or a stale name after a rename or merge) is replaced
       (`replaceUrl`, no history entry) by the address with the current name, and a wrong field key by the right one.
       The name written as typed, the browser encoding it (spaces as `%20`), cut to a sane length. Every link to a tag
       (book page, browse lists, Refine, suggestions, the tag manager) builds it with one helper; `/tags/<id>` keeps
       working as a redirect (bookmarks), as do the addresses of a merged tag (the merge's target, through the
       `MergeLog`). Field keys that clash with an app route (`books`, `search`, `admin`, `fields`, `tags`, `account`,
       `login`) can't be used as a field key, or the route is `/fields/<key>/<id>/<name>` for those: decide when built.
       **Built:** the `/fields/<key>/…` form for clashing keys (imports make such keys); details in
       [browse](../features/browse.md#readable-tag-addresses-stage-6-chunk-15a-asked-by-the-user-2026-11-05).
       Tests: the redirects (no name, a stale name, a wrong key, a merged id), every link's shape; e2e on a phone and
       the desktop.
16. [ ] Docs, CHANGELOG, feature notes; budgets through the UI's calls on 2M; e2e for each flow on every device; axe AA
        on the new screens in both themes; close the stage (merge 0.7.0, this plan removed as stage 5's was).

## Found with real books (2026-11-06)

- **False duplicates by FB2 id (fixed):** `Fb2Reader` took every `<id>` under `<document-info>`, so the file maker's
  `<author><id>` (and `<publisher><id>`) became a second `fb2-id`, shared by every file that person made: the book page
  listed them all as possible duplicates (groups under 50 escape the noise cap). Now only the direct child is read
  ([metadata-readers](../features/metadata-readers.md)); books imported before keep the wrong id until read again
  (before 1.0: rescan or start a new database).
- **FB2 ids are weak keys (decided by the user):** no rule makes them; FB Editor and converters make a GUID per new
  file (copies of one book differ), large sources (LitRes-style time UUIDs) are reliable, hand-made files copy them
  from templates or write constants (unrelated books share one). `BookQueries.DuplicatesAsync` counts a shared
  `fb2-id` only with the same `SortTitle` or first author (`SortAuthor`), checked on the capped group
  ([book-page](../features/book-page.md#possible-duplicates-read-only-until-merging-merging)). Chunk 11 keeps the rule.

## Work items

### 6.0 Rights and the tag service
- [x] Rights (D-6.1) with their texts and default groups.
- [x] `TagService`: definitions, aliases, parents and closure, delete; `MergeLog`; tag and field merge with undo.

### 6.1 Curation screens
- [ ] Administration › Catalog: Tags (manager, editor, merge, delete), Imported tags (mappings, bulk actions,
      coverage), Cover styles, Duplicates.
- [x] The tag picker, shared by every screen that chooses a tag (the tag manager's parents and merges so far).

### 6.2 Tagging and editing books
- [ ] A book's tags and metadata on its page; edits kept through re-imports; suppressions.
- [ ] Selection, selection tagging, scope tagging (built, chunk 7); default tags of libraries and folders.
- [ ] Cover upload, Change cover style, reassign a template's books.

### 6.3 Duplicates
- [ ] Stored groups, the first build and its upkeep, Not a duplicate, the same-file setting.
- [ ] Duplicates page, compare and merge, bulk merge, undo and split.
- [ ] Duplicate tag and field suggestions.

### 6.4 Docs, tests, budgets
- [ ] User docs: `docs/user-guide/` (editing a book, tagging, selections), `docs/administration/` (Catalog: tags,
      imported tags, duplicates, cover styles), `docs/reference/rights.md`; CHANGELOG; feature notes (`tags`,
      `metadata-tags`, `merging`, `book-display`, `rights`).
- [ ] Benchmark at 2M on both providers: tag page lists and picker suggestions (list budget), a tag merge of a big
      author and a scope tagging of 1 % of the library (runs: time recorded), the duplicate build, the Duplicates
      page (list budget); e2e for each flow on every phone project and desktop with layout checks; axe AA.

## Exit criteria

From the roadmap: the top 500 raw tags of the generated catalog curated in minutes, covering > 90 % of imported
links; duplicate detection over the generated 2M catalog within budget, its planted duplicates found; e2e for each
flow. Also: every new route names its right; edits, merges and suppressions survive a re-import (tests on both
providers); every merge undone in a test; p95 budgets met through the new screens' calls on 2M books; axe AA clean on
the new screens in both themes.
