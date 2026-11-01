# Metadata readers: what a book's head says

Formats are dispatched through their `BookFormat` ([book-formats](book-formats.md)). Code: `src/Librarion.Core/Import/Metadata/` (`MetadataReaders` by format, `Fb2Reader`, `Fb2Genres`, `EpubReader` +
`InnerZip`, `ComicInfoReader`, `MobiReader`, `PdfReader`, `SafeXml`, `HeadStream`, `Languages`, `Isbns`);
`Core/Import/BookMetadata.cs` (`IMetadataReader`, `FieldValue`). Tests: `MetadataReaderTests`,
`LibraryScannerTests`. What the values become: [metadata-tags](metadata-tags.md#import).

- A reader gets `open()` (the book's data from the start, decompressed, bounded) and the size, and returns title,
  description, field values `(field, value, ordinal, number, sort name)`, plain tags and identifiers; null = nothing.
  A reader that throws keeps the book by its file name and lists the problem in the scan's result.
- **FB2**: `<description>` only, at most 1 MB (`HeadStream`): covers and text are never read at import. Authors and
  translators from their parts (sort name "Last, First Middle"), genres named by `Fb2Genres` (English; MyHomeLib's
  `Installer/GenreLists/genres_fb2_en.glst`, MIT, fills the codes ours lacked; unknown codes as they came; its
  group numbers could become parent categories later), `<sequence>` with its number, `<lang>`, year from `<date>` or `<publish-info>`, publisher, ISBN,
  `<document-info><id>` as `fb2-id`, `<keywords>` as plain tags. Code pages from the XML declaration
  (windows-1251).
- **EPUB / KEPUB**: `container.xml` → OPF `<metadata>`; creators by role (`opf:role` or EPUB 3 `refines`; `aut` or
  none → author, `trl`, `edt`, `ill`; others dropped), `file-as` as sort name, subjects → `category`, series from
  `calibre:series` / `series_index` or `belongs-to-collection` + `group-position`; ISBN, `urn:uuid`, Calibre ids.
  Descriptions are HTML: tags stripped, entities decoded.
- **CBZ**: `ComicInfo.xml` (Writer → author, Penciller / Inker / Colorist / CoverArtist → illustrator, Genre → category).
- EPUB and CBZ entries of a container are held in memory up to 64 MB (`InnerZip.MaxInMemory`); bigger ones keep
  their name. A loose file is read in place.
- **MOBI / AZW / AZW3**: PalmDB → record 0 → MOBI full name and EXTH (100 author, 101 publisher, 103 description,
  104 ISBN, 105 subject, 106 date, 113 ASIN, 503 title, 524 language); first 512 KB only.
- **PDF** (≤ 32 MB): the trailer's (or cross-reference stream's) `/Info` object when it is plain, literal and hex
  strings (UTF-16 with BOM), else the XMP packet when uncompressed; an Info object inside a compressed object stream is
  not unpacked. Titles that are a program's leftovers ("Microsoft Word - x.doc") are dropped.
- DJVU, TXT, RTF, DOC, DOCX, CBR: no reader, file name only.
- **XML from strangers** (`SafeXml`): DTDs ignored, no entity expanded, no resolver, a cap on characters.
- **Gotcha (a hang found in tests):** the readers walk `XmlReader` by hand, and every branch must move the reader.
  `year ??= Year(SafeXml.Text(xml))` doesn't call `Text` once `year` is set, so the reader stays put and the loop
  never ends: read into a variable first, then assign. Run suspect tests with `dotnet test --blame-hang-timeout 20s`
  (a hang otherwise just never returns).
