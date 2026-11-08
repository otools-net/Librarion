# Administration

**Administration** (at the foot of the navigation, or in the account menu) is a page of its own. Its left column
lists the sections your account may use, in groups (each needs a [right](../reference/rights.md)); a section with
several pages lists them under it once open. The page itself opens on an **overview**: a card per section with what it
holds and links to its pages. Above each page, the path back (Administration › group › section › page) links to the
overview, its group and its section. The arrow at the top left returns to the library.

| Group | Section | What | Who |
|---|---|---|---|
| Account | My account | your preferences and password ([your account](../user-guide/account.md)) | everyone |
| Libraries | [Libraries](libraries.md) | the tenant's libraries | tenant administrators, server administrators |
| Libraries | [Library settings](settings.md) | the settings of one library | whoever holds the library settings right |
| Catalog | [Tags](tags.md) | the tags and fields: edit, merge, undo a merge, delete | whoever holds the manage tags right (Administrators, Curators) |
| Catalog | [Duplicates](duplicates.md) | books that may be the same one: compare and merge, or set apart | whoever holds the merge books right (Administrators, Curators) |
| Catalog | [Cover styles](cover-styles.md) | the drawn cover styles new books get, the books of a style turned off given another | whoever holds the manage cover styles right (Administrators, Curators) |
| Catalog | [Imported tags](imported-tags.md) | the tags and genres books came with, the most used first: keep, map, ignore | whoever holds the imported tags right (Administrators, Curators) |
| People and access | [Accounts](accounts.md) | people who sign in | tenant administrators (their tenant), server administrators (all) |
| People and access | [Groups](groups.md) | what accounts may do, and in which libraries | tenant administrators, server administrators |
| Tenant and server | [Background tasks](background-tasks.md) | scans and imports of every library: running now, history, schedules | whoever holds the scan right |
| Tenant and server | [Tenants](tenants.md) | organisations sharing the server | server administrators |
| Tenant and server | [Settings](settings.md) | defaults of this tenant and of the whole server | whoever holds the matching settings right |
| Tenant and server | [Security log](security-log.md) | sign-ins and access changes | tenant administrators (their tenant), server administrators (all) |
| Tenant and server | System | the server's version, build and database | tenant administrators, server administrators |

Each page has an address of its own (`/admin/settings/server`, `/admin/users`…) to bookmark; addresses of pages
that move later keep working.

## Finding a setting

The box at the top of the left column (on a phone, above the list of sections) finds sections, pages and
settings by the words typed, in their names, descriptions and choices; capitals and accents don't matter, and every
word must be there. A setting shows once for each page where it can be set (your preferences, a library, the tenant,
the whole server) with where it is. Choosing one opens its page with the setting in view and selected. **Enter** opens
the first one found, **Esc** clears the box.

## On a phone

On a phone, the administration opens on the list of sections, each with what it holds; a section opens as a page of
its own, its pages as tabs in a row at the top (slide it sideways when there are many). The arrow at the top left goes
back to the list, and from the list back to the library.

## Server and tenant administrators

- A **server administrator** runs the whole server: every tenant, every account, the server's settings. It belongs to
  no tenant and picks the one it works in from the account menu in the top bar (shown when there are several tenants). The
  first one is made at the [first start](../first-start.md).
- A **tenant administrator** is an account in its tenant's **Administrators** group: it manages that tenant's
  accounts, groups, libraries and settings, and never sees other tenants or server administrators.
