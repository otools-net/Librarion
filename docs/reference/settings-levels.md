# Settings levels

Settings are kept at four levels, from the widest to the narrowest:

1. **Server:** defaults of every tenant ([Administration → Settings](../administration/settings.md), server
   administrators).
2. **Tenant:** defaults of its libraries and accounts.
3. **Library:** settings of one library.
4. **My preferences:** each person's own ([My account](../user-guide/account.md)).

A level that doesn't set a value takes it from the level above, down to the built-in default. A level can **lock** a
setting: the levels below can't change it (a person's preferences can't lock). Some settings stop at a level (a
server-only setting is never offered lower).

## The settings

| Setting | Values | Default | Lowest level |
|---|---|---|---|
| Colors | like the device, light, dark | like the device | my preferences |
| Density | compact, comfortable | compact | my preferences |
| Language | like the browser, English | like the browser | my preferences |
| Books per page | 20 to 200 | 50 | my preferences |

More settings come with the features that use them.
