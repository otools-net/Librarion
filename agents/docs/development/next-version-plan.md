# After 1.0: open topics for the next version

Features discussed but **not built before the MVP (1.0.0)**, kept here so none is lost in a feature note. Roadmap to
1.0: [foundation-plan](foundation-plan.md).

- A feature decided (by the user or a stage plan) to wait past 1.0 gets a bullet here: what it is, when and where it
  came up, the note with the details.
- Nothing here is promised: when the user picks a topic, it moves into a plan of its own (decisions, chunks) and its
  bullet is removed.
- A topic that would cost at scale is brought to the user with its cost and alternatives before it is planned, as
  every feature ([scale](scale.md)).

## Access

- **Guests / anonymous browsing and OPDS:** a Guests group whose rights the administrator chooses, turned on per
  tenant (the user's answer 2026-11-10: no guests before 1.0; [rights](../features/rights.md),
  [opds-auth](../features/opds-auth.md)).
- **LDAP / OIDC sign-in** behind `IUserDirectory` (D14; [accounts](../features/accounts.md)).
- **Upload and delete books**, each behind its own right ([rights](../features/rights.md), planned rights).

## Sharing

- **Shared / public shelves:** shelves shared with a group or the whole tenant, as calibre-web's public shelves
  (offered with the stage 7 questions, 2026-11-10; stage 7 builds personal shelves only).

## Outbound (opt-in by an administrator only)

- Metadata and covers from the internet (e.g. Open Library); an update check ([privacy](privacy.md)).
- Send to Kindle by email; downloading from online libraries (non-goals for v1, [foundation-plan](foundation-plan.md)).

## Books and formats

- Editing book files: writing metadata back into the files (non-goal for v1).
- More formats: FB3, DOCX, CBR / CB7 pages ([book-formats](../features/book-formats.md),
  [reader](../features/reader.md)).
- First-page covers for PDF and DJVU (needs a page renderer; [book-display](../features/book-display.md)).
- A DJVU reader (djvu.js is GPL: a separate decision; [reader](../features/reader.md)).
- Generated cover templates for right-to-left scripts and CJK ([book-display](../features/book-display.md)).
- A tool to repack large 7z / RAR containers as ZIP ([archives](../features/archives.md)).

## OPDS

- OPDS-PSE (page streaming for comics; D6).
- OPDS 2.0, if stage 7's optional chunk is skipped ([stage 7 plan](stage-7-opds-plan.md)).

## Scale

- Precomputed visibility per content-filter profile for narrow allow-lists (D22; past the budget only at 10M,
  [catalog](../features/catalog.md), [scale](scale.md#measured)).
- Import speed-ups still to try: tag counts flushed every few batches, the links' `COPY` overlapped with reading the
  next batch ([imports](../features/imports.md)).

## Curation and imports

- Duplicate tags by spelling distance, and across fields (`genre:Horror` / `category:Horror`): not done by the user's
  answers in stage 6 ([merging](../features/merging.md)).
- Calibre tags with dots (`Fiction.Fantasy`) as a parent chain; Calibre custom columns named by their display name;
  INPX records marked deleted imported as hidden books ([imports](../features/imports.md#limits-later)).
- Removing missing copies of books that are still present ([missing-books](../features/missing-books.md#limits-later)).
