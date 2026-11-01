# Rights (planned)

Code (planned): `src/Librarion.Core/Access/` (`RightCatalog`, `RightsEvaluator`, `AccessService`),
`src/Librarion.Api/Security/` (`RequireRight`, `CurrentAccess`); client `core/rights.ts`. Ported from SFE
(`agents/docs/features/rights.md`, `right-kinds.md` there).

## Model

- **Scopes:** server (server administrators: tenants, installation, logs), tenant (users, groups, settings, libraries
  of the tenant), library (browse, download, read, upload, edit metadata, delete).
- A user belongs to one tenant (server administrators to none); rights = union of their groups' rights, per library
  through `LibraryAccess`, minus the tenant's disabled rights, minus server-disabled rights (`Librarion:Rights:Disabled`,
  ids, `prefix*`, `kind:write`).
- **Content filters** (allowed/denied tags, languages, max rating) per group: narrow which books a right applies to,
  applied in every query (`Visible()`), 404 for a hidden book (never told apart from missing). **Joined at query time**
  through `TagClosure` (D22): always current, no precomputation. Known cost: a narrow allow-list (a group seeing
  0.1 % of the books) makes keyset pages scan far to fill a page; the stage 2 benchmark measures that case, and if it
  breaks the budgets the user decides on precomputed visibility (a row or bitmask per book and filter profile).
- Each right: id, scope, `Kind` (Read/Write: Write when any route it opens changes something), `GuestAllowed`,
  default groups, `since` (revision, for existing installs after the first release).
- Tag rights (`tags.assign`, `tags.manage`, `tags.importMappings`, `tags.defaults`): [tags](tags.md).
- `books.convert` (download a converted copy, needs `books.download`; keeping it as a book file also needs
  `books.edit`): [conversion](conversion.md).
- OPDS has its own right (`opds.access`) so an admin can allow the web UI without feeds or the reverse; API keys carry
  scopes that can only narrow the user's rights, never widen them.

## Rules

- Every route names its right(s) with `.RequireRight(Rights.X)` or `.AllowAnonymous()`; `EveryRouteNamesARight` fails
  otherwise.
- A new function gets a right in `RightCatalog`, the same id in `core/rights.ts`, and the client hides what needs it.
- Tenant administrators touch only users and groups of their tenant; never server administrators.
