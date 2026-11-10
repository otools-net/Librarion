# Signing in and your account

## Signing in

Enter your name and password. **Keep me signed in on this device** keeps the session for 30 days after its last use;
leave it off on a shared computer, and the session ends when the browser closes. After signing in you return to the
page you asked for.

Too many wrong passwords from one place make that place wait 15 minutes before trying the same name again (you can
still sign in from elsewhere). Many more lock the account itself until an administrator unlocks it. A server
administrator can unlock their own account from the sign-in page: see [first start](../first-start.md#locked-out).

## My account

Your initial at the top right opens the account menu; **My account** there opens your section of the
[administration](../administration/README.md), with these pages:

- **Password:** enter the current one and the new one twice. Your other devices are signed out.
- **Preferences:** colors (light, dark or like the device), density, language and books per page, kept for your
  account on every device. A setting an administrator has locked can't be changed here. **Save** keeps the changes,
  **Discard** forgets them.

- **API keys:** keys for reader apps and scripts ([below](#api-keys)).

## API keys

A key signs a reader app or a script in instead of your password. Give each device or script its own: you can then
revoke one without changing your password.

1. **New key**, give it a name (the device or script it is for).
2. **Used for:** **Reader apps (OPDS)**, **Reading through the API** (scripts that only look), **Changing through the
   API** (scripts that may change what your account may change). A key never does more than your account may.
3. **Libraries:** when you may open several, tick some to limit the key to them; none ticked means all of them.
4. **Ends on:** an optional last day; empty, the key works until you revoke it.
5. **Can be used in the address** (reader apps only): for apps that can't sign in, the key becomes part of the
   catalog's address. Anyone who sees that address can use the key, so tick it only when needed.
6. **Make the key**: the key is shown **this once**. **Copy** it and paste it where it goes; lost, revoke it and make
   another.

Under the key, **How to use it** says what to give the app or script:

- **A reader app, with a key that can be used in the address:** the **catalog address with the key** (it ends in
  `/opds/k/` and the key). **Copy address** and add it as a catalog in the app: nothing else is asked, no user name, no
  password.
- **A reader app, otherwise:** add a catalog with the address shown on the page (it ends in `/opds`), your user name,
  and the key as the password.
- **A script:** send the key in a header, `Authorization: Bearer <key>` or `X-Api-Key: <key>`.

An administrator can switch keys in the address off for the whole tenant (the setting **API keys in the address**);
**Can be used in the address** is then not offered, and keys made with it no longer work that way.

The list shows what each key is for, when it was last used and from where. **Revoke** ends a key at once. A new
password, or your account being disabled, ends **every** key: make new ones afterwards. Keys are managed only here,
signed in to the app, never with a key. Administrators can see and revoke your keys
([accounts](../administration/accounts.md#api-keys)).

**Switch to light colors** / **Switch to dark colors** in the account menu changes colors in this browser only.
