# Metadata as tags: `field:value` (planned)

Code (planned): `src/Librarion.Core/Tagging/` (`TagFields`, `FieldTagResolver`, `BuiltInFields`), the metadata
readers in `Core/Import/` hand `(field, value)` pairs to it; search syntax in `Core/Search/`. Builds on the tag model
in [tags](tags.md) (SFE/TagStudio). Decision D18 in [foundation-plan](../development/foundation-plan.md) (decided
2026-10-31).

What a book's metadata says about it (category, authors, series, publisher, language…) is stored as **tags under a
field**, written `field:value`: `category:Horror`, `author:"Stephen King"`, `series:"The Dark Tower"`,
`publisher:Scribner`, `language:en`, `my-column:some value`. One mechanism for all of them: the same tables, the same
curation (merge, aliases, mappings, suppressions), the same search, content filters and OPDS feeds. There are no
separate `Authors`, `Series` or `Publishers` tables.

## Model

- A **field** is a category tag (`IsCategory`) with a **key**: lower case `[a-z0-9-]`, unique per tenant (`author`,
  `series`). A **field tag** is a tag whose parent is that field: "Stephen King" under `author` is
  `author:Stephen King`. Plain tags (no field) stay as in [tags](tags.md).
- Columns on `Tags`: `FieldKey` (on the field's own row, null elsewhere), `FieldId` (on its values, null for plain
  tags), `SortName` ("King, Stephen"; null = the name). A value is unique within its field by normalized name, except
  for the disambiguation parent ("John Smith (1950)").
- **Link attributes** on `BookTags`, both nullable: `Ordinal` (order of the book's authors, the first is the one on
  cards) and `Number` (decimal: series index `2`, `2.5`). Roles are separate fields (`author`, `translator`,
  `editor`, `illustrator`), not a role column, so each is searched and curated on its own.
- Fields have the tags' features: aliases ("Steven King" → Stephen King), parents among values (`category:Horror`
  under `category:Fiction`), hidden, colors, content filters per group (`language:` and `category:` filters are just
  tag filters, D5).

## Built-in fields (edited, never deleted)

| Field | Values from | Card / page |
|---|---|---|
| `author` | FB2 `<author>`, EPUB `dc:creator` (role aut or none), MOBI EXTH 100, PDF `Author`, Calibre authors, ComicInfo `Writer` | first author on cards (by `Ordinal`) |
| `translator`, `editor`, `illustrator` | FB2 `<translator>`, EPUB `dc:creator`/`dc:contributor` roles trl/edt/ill, ComicInfo `Penciller`… | book page |
| `series` | FB2 `<sequence name number>`, EPUB `calibre:series` / `belongs-to-collection`, Calibre series, ComicInfo `Series` | with `Number` |
| `category` | FB2 `<genre>` (named from the bundled genre list), EPUB `dc:subject`, PDF `Keywords`, ComicInfo `Genre` | chips |
| `publisher` | FB2 `<publish-info>`, EPUB `dc:publisher`, Calibre publisher, ComicInfo `Publisher` | book page |
| `language` | FB2 `<lang>`, EPUB `dc:language`, Calibre languages; normalized to ISO 639 (`ru`, `en`), named per UI language | badge |
| `year` | publication year (FB2 `<date>`, `<publish-info><year>`, EPUB `dc:date`, Calibre pubdate) | book page |

- **Other keys become their own field**, created on first sight under the source's import category: Calibre custom
  columns (`#genre` → `genre:`, a yes/no column → `name:yes`), ComicInfo extras, FB2 `<custom-info info-type>`. An
  administrator renames, merges or ignores fields like any tag ("`genre:` is `category:`").
- Calibre's own tags and FB2 `<keywords>` stay **plain tags** (they are tags in the source), hierarchical names as in
  [tags](tags.md).
- **Not tags** (one value per book, measured, or unique): title and sort title, description, rating (number), page
  count, file size and dates, identifiers (ISBN, Calibre id, UUID: `BookIdentifiers`, one row each; a tag per ISBN
  would be 2M tags of one book each).

## Import

- A metadata reader yields `(field key, raw value, ordinal, number)`. The raw value goes through the import mappings
  of [tags](tags.md) with the **field as the key's scope**: `ImportTagMappings (tenant, source kind, field, normalized
  raw value) → tag | ignored`. Lookup: mapping → value name or alias **within the field** → new value under the field.
  So `author:King` never meets `category:King`.
- Author names are normalized before lookup (spaces, "Last, First" ↔ "First Last", FB2 first/middle/last joined);
  `SortName` is "Last, First" from FB2 parts or Calibre's `author_sort`, else derived.
- Links get `Source = Imported` and the re-import rules of tags apply: re-import recomputes imported field links,
  manual ones and suppressions stay; a merge of "Steven King" into "Stephen King" maps the raw name, so the next
  import lands on the right author.

## Search and browse

- `field:value` (name or alias, normalized; quotes for spaces), `field:value*` prefix, `-field:value`, `field:`
  alone = has any value (`-series:` = books outside any series); a value's descendants included as for tags
  (`category:Fiction` finds Horror). `tag:` keeps matching every tag, fields included (`tag:"author:Stephen King"`).
  Free text also matches field values, **without copying them into the books' search documents** (D20): the words
  are matched against titles (FTS) and, separately, against tag names and aliases (a small indexed table); books come
  through `BookTags` and the two results are combined. Renaming or merging `category:Fiction` (1M books) touches one
  row, never re-indexes books.
- Browse pages per field (authors A–Z, series, categories, publishers, languages) are one tag-list view with the
  field as a filter: keyset by `SortName`, counts from `TagCounts`, a value's page lists its books (series sorted by
  `Number`). New fields get a browse page without code.
- OPDS: navigation feeds per field (authors, series, categories…), Atom `<author>` from `author` links,
  `<category>` from `category` and plain tags, series in the entry metadata.

## Scale

- 500k authors + 100k series + publishers ≈ 650k field values per tenant beside ~50k plain tags: the in-memory
  `TagGraph` holds plain tags and fields' **small** value sets (categories, languages); big fields (author, series,
  publisher, any field over a threshold) are looked up by index `Tags (TenantId, FieldId, NormalizedName)`, aliases
  likewise, with a bounded LRU for the importer.
- `TagClosure` rows for a flat value: itself and its field (2 rows); "has a value of `author:`" is the field's
  closure join, never a list of ids.
- Sorting books by author or series needs a key on the book: `Books.SortAuthor` (first author's `SortName`) and
  `Books.SeriesId` + `SeriesNumber` (first series), denormalized, kept by imports and tag edits; keyset paging on
  them as on titles.
- `BookTags` grows to ≈ 2M × (authors + categories + series + language + publisher + year) ≈ 12–15M rows at 2M books:
  the `(TagId, BookId)` and `(BookId)` indexes carry it; the stage 2 generator makes this shape.

## UI

- Chips grouped by field on the book page (`Authors`, `Series`, `Categories`…; field names translated for built-ins,
  as stored for others); a chip opens the value's books. Cards: title, first author.
- Editing (stage 6, `metadata.edit` for fields, `tags.assign` for plain tags): the tag picker scoped to a field,
  author order by drag, series number in place; new values typed in create them under the field.
- Tag manager: fields listed as categories with counts; the big ones paged and searched, never loaded whole.

## Tests to have

- Each reader yields the right fields (samples per format, roles, sequences with numbers, Calibre custom columns);
  same name in two fields stays two tags; author name forms land on one value; mapping and merge carry through a
  re-import; `field:` search forms, prefixes, negation, descendants; browse pages and OPDS feeds per field page by key
  with correct counts; sorting by author and series at 2M within budget; a new custom field gets a browse page.
