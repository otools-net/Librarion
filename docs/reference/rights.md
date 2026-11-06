# Rights

Groups give rights to their members ([groups](../administration/groups.md)). Library rights apply in the group's
libraries; the others tenant-wide (Manage tags too: a tenant's libraries share its tags). "Changes" marks a right that lets its holder change something.

| Right | Area | Changes | Built-in group | Lets its holder |
|---|---|---|---|---|
| Browse | Libraries | | Administrators, Users, Curators | see the library and its books |
| Download | Libraries | | Administrators, Users, Curators | download the books' files |
| Browse by folder | Libraries | | Administrators, Curators | browse the library by its folders and ZIP files and see how many files each holds; folder names are shown even when content filters hide the books in them |
| Scan | Libraries | yes | Administrators | scan the library's folders now, cancel a scan, schedule scans |
| Library settings | Libraries | yes | Administrators | change the library's settings |
| Import | Libraries | yes | Administrators | import a Calibre library or an INPX catalog into the library (its metadata is stored in Librarion; nothing is written back) |
| Tag books | Catalog | yes | Administrators, Curators | give the library's books plain tags and take them away, one book, a selection or a whole search at a time |
| Edit books | Catalog | yes | Administrators, Curators | change a book's title, authors, series and other fields, description, rating and cover; the changes are kept through later scans and imports |
| Merge books | Catalog | yes | Administrators, Curators | merge duplicate books into one, split them again, undo a merge, mark books as not duplicates |
| Imported tags | Catalog | yes | Administrators, Curators | map the tags and genres that came with imported books onto the tenant's tags, or ignore them |
| Default tags | Catalog | yes | Administrators, Curators | choose the tags the library and its folders give the books that come in |
| Manage tags | Catalog | yes | Administrators, Curators | create, rename, merge and delete the tenant's tags and fields (authors, series and the others), tenant-wide |
| Manage accounts and groups | Tenant | yes | Administrators | create accounts in the tenant, put them in groups, choose what each group may do |
| Manage libraries | Tenant | yes | Administrators | add, rename and delete the tenant's libraries |
| Tenant settings | Tenant | yes | Administrators | change the defaults of the tenant's libraries and accounts |
| Security log | Tenant | | Administrators | read the tenant's sign-ins and access changes |
| Own preferences | Own account | yes | Administrators, Users, Curators | keep one's own colors, density and page size on the server |
| Change own password | Own account | yes | Administrators, Users, Curators | change the password of one's own account |
| System information | System | | Administrators, Users, Curators | see the server's version and database |
| API documentation | System | | Administrators | read the API's description (OpenAPI, at `/openapi/v1.json`) |

Server administrators have every right, in every tenant, and these server rights besides (never given by a group):

| Right | Lets its holder |
|---|---|
| Manage tenants | create, change and delete tenants, and manage every tenant's accounts, groups and libraries |
| Every account | create, change and delete every account, and make server administrators |
| Server settings | change the defaults of every tenant |
| Server security log | read every tenant's security events and the server's own |

## Switching rights off

- For one tenant: [Administration → Tenants](../administration/tenants.md), **Edit**, rights switched off in this
  tenant (server administrators working there lose them too; they keep the server rights).
- For the whole server: `Librarion:Rights:Disabled`, a list of right ids (`library.settings`), prefixes
  (`tenant.*`) or `kind:write` (every right that changes something: a read-only server). Server administrators lose
  them too. See [configuration](../configuration.md#rights).
