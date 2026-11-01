# First start

A new server has no accounts. Until its first administrator is made, every page opens the **setup page**.

## The setup code

The setup page asks for a code that proves you run the server:

- If the configuration sets `Librarion:Setup:Code` (at least 12 characters), that is the code.
- Otherwise the server makes one at its first start, shows it in its console in a box, and keeps it in
  `data/setup-code.txt` next to the program. Type it as shown; dashes, spaces and case don't matter.

## Setting up

On the setup page, enter:

1. the setup code;
2. the name and password of the **server administrator** (the password twice);
3. the name of your organisation (a *tenant*: a family, a club, a company; "Home" if left empty) and of its first
   library ("Library" if left empty).

**Set up and sign in** creates them and signs you in. The setup page is then closed for good: other accounts are made
in the [administration](administration/accounts.md).

## Locked out?

- **A server administrator** who forgot the password, or is locked out after too many wrong passwords: on the sign-in
  page, choose **Recover the account**, enter the name and ask for a code. The server shows it in its console (and in
  `recovery-code.txt` next to `setup-code.txt`); it works once, for 15 minutes. Enter it with a new password to sign
  in.
- **Other accounts:** an administrator sets a new password or unlocks them in [Accounts](administration/accounts.md).
- **No way in at all** (every server administrator lost): start the server with `Librarion:Setup:Recover` set to
  `true`. The setup page opens again and, with the setup code, resets the password of a server administrator (or
  adds one). Turn the setting off afterwards.
