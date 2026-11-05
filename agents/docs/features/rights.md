# Rights

Code: `src/Librarion.Core/Access/` (`RightCatalog`, `Rights`, `RightsEvaluator`, `AccessChanges`),
`src/Librarion.Api/Security/` (`RightMetadata` = `RequireRight`, `CurrentAccess`), `Api/Endpoints/GroupEndpoints.cs`
(`api/rights`); client `core/rights.ts`, `admin/groups-section.ts`. Ported from SFE (`rights.md`, `right-kinds.md`
there), for tenants holding libraries. Who has them: [accounts](accounts.md); where: [tenants](tenants.md).

## Built (stage 1)

- **Catalog** (`RightCatalog.All`): id, scope, `Kind`, default built-in groups, area (the heading in the group
  editor, `rightArea.<area>`). Texts `right.<id>` and `right.<id>.hint` in `en.json`
  (`ClientI18nTests.EveryNameTheServerSendsHasItsText`).
- **Scopes:** `server` (`server.tenants`, `server.users`, `server.settings`, `server.securityLog`: server
  administrators only, never in a group), `tenant` (`tenant.users`, `tenant.libraries`, `tenant.settings`,
  `tenant.securityLog`, `account.*`, `system.*`), `library` (`library.browse`, `library.settings`).
- **Kind** Read/Write: Write when any route it opens changes something (shown as "Changes things"); the server can
  switch every write off (`kind:write`).
- **Built-in groups** per tenant: Administrators (every tenant and library right), Users (`library.browse`,
  `library.download`, `account.preferences`, `account.password`, `system.info`) and Curators (stage 6: the Users'
  rights and the catalog area's), given at tenant creation, then changed by hand. Built-in groups can't be deleted
  (`group.builtIn`). `RightCatalogTests` keeps Administrators ⊇ Curators ⊇ Users and Curators' tenant rights to
  `tenant.tags` and the own-account / system ones. Tenants made before a new built-in group don't get it (no
  upgrade before 1.0).
- **Curation rights** (stage 6, D-6.1 of [stage-6-curation-plan](../development/stage-6-curation-plan.md)), area
  `catalog`, all Write: `library.tag` (plain tags on books), `library.edit` (a book's metadata and cover),
  `library.merge` (merge, split, undo, not a duplicate), `library.importedTags` (the mappings page),
  `library.defaultTags` (library and folder default tags), `tenant.tags` (the tag and field vocabulary, per tenant:
  D9). The plan named them `tags.assign`, `books.edit`…; ids start with their scope (`EveryRightIsWellFormed`), so
  they were named by scope.
- **Evaluation** (`RightsEvaluator.GetAsync`): union of the account's groups' rights in its tenant; library rights only
  in the group's libraries (`AllLibraries`, or `GroupLibraries` rows); minus the tenant's `TenantDisabledRights`;
  minus the server's `Librarion:Rights:Disabled` (ids, `prefix*`, `kind:write`), applied at each read so a
  configuration change counts at once. Server administrators: every right everywhere but the disabled ones; before
  any tenant exists, every non-library right.
- `EffectiveAccess.Has(right)`: tenant/server right held, or a library right held in **some** library (what
  `RequireRight` checks); `HasIn(right, libraryId)` for one library, `LibrariesWith(right)` to filter queries.
- **Cache:** per account and tenant, valid while `AccessChanges.Version` is the one it was read at; every change to
  accounts, groups, tenants or libraries bumps it, so it applies to the next request.

## Rules

- Every route names its right(s) with `.RequireRight(Rights.X, ...)` (any of them) or `.AllowAnonymous()`;
  `EveryRouteNamesARight` fails otherwise.
- A new function gets a right in `RightCatalog` (scope, kind, default groups, area), the same id in `core/rights.ts`,
  its two texts, and the client hides what needs it (`SessionService.can`). Before 1.0, no `since` revisions
  ([production-upgrades](../development/production-upgrades.md)).
- A tenant route reached by a server administrator for another tenant (`?tenant=`) needs the server right; refused =
  404, so a tenant administrator doesn't learn other tenants exist (`ApiResults.TargetTenant`).

## Planned

- More library rights as features come: read, upload, delete; `library.convert` (needs `library.download`; keeping
  the copy as a book file also needs `library.edit`): [conversion](conversion.md).
- `GuestAllowed` and a Guests group, once OPDS decides on anonymous access.
- OPDS has its own right (`opds.access`) so an admin can allow the web UI without feeds or the reverse; API keys carry
  scopes that can only narrow the user's rights, never widen them ([opds-auth](opds-auth.md)).
- **Content filters** (allowed/denied tags, languages, max rating) per group: narrow which books a right applies to,
  applied in every query (`Visible()`), 404 for a hidden book (never told apart from missing). **Joined at query time**
  through `TagClosure` (D22): always current, no precomputation. Known cost: a narrow allow-list (a group seeing
  0.1 % of the books) makes keyset pages scan far to fill a page; measured in stage 2 (within budget at 2M, past it at 10M
  on SQLite: [scale](../development/scale.md#measured)); accepted for now, precomputed visibility (a row or bitmask
  per book and filter profile) if it matters later.
