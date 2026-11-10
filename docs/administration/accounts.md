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

## API keys

An account's page also lists its [API keys](../user-guide/account.md#api-keys): what each is for, when it was last
used and from where. **Revoke** ends one at once (a lost phone, a key shared by mistake). Keys are made by the account
itself on its own page; a new password or disabling the account ends all of them. Making and revoking keys is in the
[security log](security-log.md).

A key may be made to work in the catalog's address (for reader apps that can't sign in): anyone who sees that address
can use it. The setting **API keys in the address** (group **Reader apps**, tenant or server [settings](settings.md))
switches that off for every key of the tenant at once; a server administrator's keys follow the server's setting. The
key in an address is never written to the server's log.

## Server administrators

Server administrators see **Server administrators** above the list: ticked, the list shows them instead of the
tenant's accounts, and **New account** makes a server administrator (it belongs to no tenant and is in no group).
Whether an account is a server administrator can't be changed after it is made. The last server administrator can't
be disabled or deleted.
