# Security log

**Administration → Security log** lists, newest first, what an administrator may need to know about access:

- sign-ins, refused sign-ins, lockouts and sign-outs;
- the server's setup, recovery codes asked for and used, password changes;
- accounts, groups, tenants, libraries and settings created, changed or deleted.

Each event shows when, which account (by name) and from which address. Passwords and codes are never recorded.

Filter by kind of event or by name. Tenant administrators see their tenant's events; server administrators can tick
**Every tenant** to see all events, otherwise they see the server's own (setup, server administrators). Events are
kept for a year (`Librarion:Security:EventDays`, [configuration](../configuration.md#security)).
