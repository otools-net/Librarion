# Catalog: books, files, tags in the database; lists by key

Code: `src/Librarion.Core/Data/Entities/Catalog/Book.cs`, `Data/Entities/Tags/Tag.cs`,
`Data/LibrarionDbContext.Catalog.cs` (indexes), `Core/Catalog/` (`BookQueries`, `TagQueries`, `BookVisibilities`,
`KeysetCursor`, `BookTitles`, `FileTitles`, `PathKeys`), `Core/Tagging/` (`BuiltInTags`, `TagNames`),
`Api/Endpoints/BookEndpoints.cs`. Built in stage 2; measured in [scale](../development/scale.md#measured).

## Tables (all tenant-owned, query-filtered)

- `Books`: library, `Title` (null = none in the metadata), `SortTitle` (never null), `SortAuthor` (first author's
  sort name, empty without one: the providers sort nulls at opposite ends), `SeriesId` + `SeriesNumber` (first series),
  description, rating, added / updated, cover columns ([book-display](book-display.md)), `MergedIntoId` and `WorkKey`
  ([merging](merging.md)).
- `BookFiles`: a book's copies and formats; `ArchiveId` null = loose file, `Path` relative to the root or the entry
  name, `PathKey` (64-bit hash, indexed instead of the string), size, CRC, ZIP locator and method
  ([archives](archives.md)). `Archives`: one row per ZIP container (filled in stage 3).
- `BookIdentifiers` (scheme `isbn`, `uuid`, `fb2-id`, `calibre-uuid`, `inpx`…; index tenant, scheme, value).
- `BookProperties` (book, name → value): imported values that are neither tags nor columns (Calibre custom columns);
  `Books.MetadataSource` (`Heads`, `Calibre`, `Inpx`) and `ImportHash` ([imports](imports.md)).
- `Tags` (plain tags, fields and field values in one table, D18), `TagAliases`, `TagParents`, `TagClosure` (ancestor,
  descendant, depth), `BookTags` (key BookId, TagId, Source; `Ordinal`, `Number`), `TagCounts` (library, tag, books),
  `Libraries.BookCount`; `GroupTagFilters` (group, tag, allow / deny): content filters
  ([rights](rights.md)). Model in [tags](tags.md) and [metadata-tags](metadata-tags.md).
- Built-in fields made with each tenant (`author`, `translator`, `editor`, `illustrator`, `series`, `category`,
  `publisher`, `language`, `year`) and the built-in tags Meta Tags and Archived; a field is found by `FieldKey`
  (unique index), never by scanning for `BuiltIn`.
- Deleting a library or tenant with millions of books by cascade is one huge transaction: stage 3 deletes in batches
  first (a task), the cascade only finishes the job.

## Indexes that carry the lists

- Each sort an index ending in `Id`: `(TenantId, SortTitle, Id)`, `(TenantId, SortAuthor, Id)`,
  `(TenantId, Added, Id)` for every library of a tenant, the same with `LibraryId` for one.
- `BookTags (TagId, BookId)`, its key `(BookId, TagId, Source)` for the per-book tests; partial `(TagId, Number, BookId) WHERE Number IS NOT NULL` for a
  series by number.
- `Tags (TenantId, FieldId, SortName, Id)` for a field's values A–Z.

## Queries

- **Visibility** (`BookVisibilities`, `BookQueries.VisibleAsync`): the libraries the caller may browse
  (`library.browse`), not merged away, and per group its content filters (allow: `EXISTS`, deny: `NOT EXISTS`, through
  `TagClosure`); several groups = union; a group without filters sees all of its libraries.
- **Pages by key** (`KeysetCursor`: base64url JSON of the last row's sort value and id, garbage = first page):
  `sort` = `title`, `author`, `added` (newest first), `number` (with a tag: the partial index walked in order).
- **Books with a tag** (a field value's page, a content filter's allowed tags) joined **by size** (D22, query time):
  a tag under √(page × library books) books (from `TagCounts`) starts from its links and sorts them, a broader one
  walks the sort index and tests each book; descendants are listed first (at most 500), so a test is one seek
  (`BookQueries.NarrowAsync`).
- A page's authors, series and formats: one query each for the whole page, never per book.
- Book detail: tags by field, files (with their archive's name and `missing`), identifiers, properties, `mergedIntoId`,
  `canDownload` (set by the route); a book the caller can't see is not found. Possible duplicates on demand, bounded
  per book ([book-page](book-page.md)).

- Field values and plain tags (`TagQueries.ValuesAsync`, `PlainTagsAsync`) by sort name with the library's counts, those
  without books there skipped (at most 2,000 rows read per request); a tag's detail (`GetAsync`); details in
  [browse](browse.md#fields-and-tags-chunk-14).

## Routes (all `library.browse`)

- `GET api/books?library&sort&after&tag&limit` (limit up to 200), `GET api/books/{id}`, `GET api/books/{id}/duplicates`,
  `GET api/fields`, `GET api/fields/{key}/values?library&from&after&limit`, `GET api/tags?library&from&after&limit`,
  `GET api/tags/{id}?library`. Documented by OpenAPI.

## Known limits (accepted 2026-11-01, measure again before the release)

- A **narrow tag's page by title** grows with √(page × books): at 10M, the biggest series by title (111 ms on
  PostgreSQL, 219 ms on SQLite) and a group seeing 0.1 % of the books (68 / 160 ms) pass the 100 ms budget; everything
  else stays flat. The user chose to keep query-time joins.
- Fallbacks if it matters: a sort key on the link, `BookTags (TagId, SortTitle, BookId)` (≈ 0.5 GB at 2M, 2.5 GB at
  10M, kept on every title change), or precomputed visibility per filter profile (D22).

## Tests

- `CatalogSchemaTests` (filters, cascade, closure), `CatalogGeneratorTests` (deterministic, shapes),
  `CatalogQueryTests` (paging walks every book once in order, filters never leak, routes), `TenancyTests`
  (`NoQueryCrossesTenants` with `CatalogRows`).
