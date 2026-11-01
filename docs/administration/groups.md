# Groups

An account may do what its groups allow, all of them together. **Administration → Groups** lists the tenant's groups
with how many rights and members each has.

Every tenant has two built-in groups, which can be changed but not deleted:

- **Administrators:** every right of the tenant and its libraries.
- **Users:** browse the libraries, keep their own preferences, change their own password, see the system information.

## Making or changing a group

**New group**, or choose one in the list:

- **Name.**
- **Rights**, by area (libraries, tenant, own account, system); those marked **Changes things** let the account change
  something rather than only look. A right marked **Off on this server** is switched off by the server's configuration
  and can't be given. Every right is described in the [rights reference](../reference/rights.md).
- **Where its library rights apply:** **Every library** of the tenant (those added later too), or the libraries ticked.
  Tenant rights apply tenant-wide either way.
- **Members:** the accounts in the group.

**Save** applies the change at once, to sessions already open too.
