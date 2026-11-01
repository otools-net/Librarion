# Administration

**Administration** in the menu shows the sections your account may use (each needs a [right](../reference/rights.md)):

| Section | What | Who |
|---|---|---|
| [Accounts](accounts.md) | people who sign in | tenant administrators (their tenant), server administrators (all) |
| [Groups](groups.md) | what accounts may do, and in which libraries | tenant administrators, server administrators |
| [Libraries](libraries.md) | the tenant's libraries | tenant administrators, server administrators |
| [Tenants](tenants.md) | organisations sharing the server | server administrators |
| [Settings](settings.md) | defaults for the server, a tenant, a library | whoever holds the matching settings right |
| [Security log](security-log.md) | sign-ins and access changes | tenant administrators (their tenant), server administrators (all) |

## Server and tenant administrators

- A **server administrator** runs the whole server: every tenant, every account, the server's settings. It belongs to
  no tenant and picks the one it works in from the account menu in the top bar (shown when there are several tenants). The
  first one is made at the [first start](../first-start.md).
- A **tenant administrator** is an account in its tenant's **Administrators** group: it manages that tenant's
  accounts, groups, libraries and settings, and never sees other tenants or server administrators.
