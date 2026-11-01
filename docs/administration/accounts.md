# Accounts

**Administration → Accounts** lists the tenant's accounts, 100 at a time (**Show more** for the next ones). The search
box finds accounts by the start of their name or display name.

## Making an account

**New account**, then:

- **Name:** what the person types to sign in. Names are unique on the whole server, whatever the case, and can't
  contain a colon.
- **Display name** (optional): shown instead of the name.
- **Password:** at least 8 characters.
- **Groups:** what the account may do. **Users** reads the libraries; **Administrators** manages the tenant. See
  [groups](groups.md).
- **Disabled:** the account can't sign in.

**Save** creates it. The person changes the password later in [My account](../user-guide/account.md).

## Changing an account

Choose it in the list. Leave **Password** empty to keep the current one. A new password, or disabling the account,
signs it out everywhere. **Unlock** clears a lock left by too many wrong passwords. **Delete** removes the account.

The tenant always keeps at least one administrator: a change that would leave its Administrators group without an
enabled member is refused.

## Server administrators

Server administrators see **Server administrators** above the list: ticked, the list shows them instead of the
tenant's accounts, and **New account** makes a server administrator (it belongs to no tenant and is in no group).
Whether an account is a server administrator can't be changed after it is made. The last server administrator can't
be disabled or deleted.
