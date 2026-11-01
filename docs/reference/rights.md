# Rights

Groups give rights to their members ([groups](../administration/groups.md)). Library rights apply in the group's
libraries; the others tenant-wide. "Changes" marks a right that lets its holder change something.

| Right | Area | Changes | Built-in group | Lets its holder |
|---|---|---|---|---|
| Browse | Libraries | | Administrators, Users | see the library and its books |
| Download | Libraries | | Administrators, Users | download the books' files |
| Scan | Libraries | yes | Administrators | scan the library's folders now, cancel a scan, schedule scans |
| Library settings | Libraries | yes | Administrators | change the library's settings |
| Import | Libraries | yes | Administrators | import a Calibre library or an INPX catalog into the library (its metadata is stored in Librarion; nothing is written back) |
| Manage accounts and groups | Tenant | yes | Administrators | create accounts in the tenant, put them in groups, choose what each group may do |
| Manage libraries | Tenant | yes | Administrators | add, rename and delete the tenant's libraries |
| Tenant settings | Tenant | yes | Administrators | change the defaults of the tenant's libraries and accounts |
| Security log | Tenant | | Administrators | read the tenant's sign-ins and access changes |
| Own preferences | Own account | yes | Administrators, Users | keep one's own colors, density and page size on the server |
| Change own password | Own account | yes | Administrators, Users | change the password of one's own account |
| System information | System | | Administrators, Users | see the server's version and database |
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
