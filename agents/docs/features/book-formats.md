# Book formats: one class per format

Code: `src/Librarion.Core/Formats/` (`BookFormat` the model, `BookFormats` the registry, `KnownFormats.cs` the formats
that read something). Parsers stay where they are: `Import/Metadata/` (heads), `Covers/CoverExtractor` (covers).
Tests: `MetadataReaderTests`, `CoverTests`, `LibraryScannerTests`.

More formats will come gradually (the user, 2026-11-01): everything Librarion knows about a format lives in its
`BookFormat`, so a new format is one class (or one line) in `BookFormats.All`, nothing else to touch.

## What a format says

- `Key`: lower case, stored in `BookFiles.Format`; **never renamed** once released (rows hold it).
- `Extensions`, with their dot. A compound one (`.kepub.epub`) wins over its last part; lookups are a dictionary by
  last extension plus the few compound ones (the scanner asks for every file of millions).
- `ContentType`: what downloads send (`application/octet-stream` for a key this version doesn't know).
- `ReadMetadata(open, size, cancel)`: the head only, bounded ([metadata-readers](metadata-readers.md)); `open` gives
  the bytes from the start as often as asked (a zip entry is not seekable: call it again rather than seek).
- `HasCovers` + `ExtractCover(...)`: the cover still encoded, at most `CoverExtractor.MaxCoverBytes`
  ([book-display](book-display.md)). `HasCovers` decides at import whether a book's cover is looked for
  (`CoverSource.Book`) or generated.
- A format known by name only is a plain `new BookFormat(key, type, extensions)`.

## Who asks

- `LibraryScanner`: `BookFormats.Of(name)` (is it a book?), `IsContainer` (a zip to look inside).
- `MetadataReaders` (the `IMetadataReader` the scanner uses; tests swap in fakes): `Find(format)?.ReadMetadata`.
- `CoverExtractor.Extract` / `HasCovers`: `Find(format)` with the read errors caught (a broken book has no cover).
- Download route: `BookFormats.ContentType`.

## Adding a format

1. A class deriving `BookFormat` in `Formats/` (its parser in `Import/Metadata/` or `Covers/` if it reads anything).
2. Its line in `BookFormats.All`.
3. Tests: a sample read back (metadata, cover), and its extension recognised by the scanner.
4. If it is a container of several books (not a book), it doesn't belong here: see [archives](archives.md).

Planned, as they come: FB3, DJVU covers (needs a renderer), CBR / CB7 pages, PDF first page (a renderer), DOCX,
ODT, conversions ([conversion](conversion.md)).
