# On an e-ink reader

The browsers of e-ink readers (Kindle, Kobo, PocketBook, Tolino, Boox) are slow and show only grays, so Librarion
gives them **the e-reader version**: plain gray pages, without script, made to find a book and download it onto the
device. The device's own reader then opens the book. Nothing is read in the browser itself.

## Opening it

- The e-reader version is at your Librarion address followed by `/e/`, for example `https://books.example.org/e/` (or
  `http://192.168.1.20:5080/e/` at home). It is short on purpose: typing on an e-ink keyboard is slow.
- **Kindle, Kobo, PocketBook, Tolino and Boox browsers are sent there by themselves** when they open the app's
  address; a link to a book opens the same book there.
- **Full site**, at the bottom of every e-reader page, keeps this browser on the app instead (for a year). On the app's
  sign-in page, **On an e-ink reader? Use the e-reader version** brings it back.
- Any other browser can open it too, at `/e/` or from that link.
- An administrator may have switched it off (the setting **E-reader mode**, [settings](../reference/settings-levels.md)):
  then its pages are not found and no browser is sent there.

## Signing in

- The same account and password as the app; signing in on either opens both in that browser.
- **Keep me signed in on this device** is ticked to begin with, so you type the password once: the session then lasts
  30 days after its last use. Untick it on a device you share.
- Wrong passwords count as on the app ([signing in](account.md#signing-in)): too many from one place make it wait.
- **Sign out**, at the top of every page.
- **A browser that forgets the sign-in** (some devices clear cookies when they sleep): make an API key for the device
  with **Reader apps and e-readers** and **Can be used in the address** ticked, open the **e-reader address with the
  key** shown under it (it ends in `/e/k/`, the key and `/`) and bookmark it. The pages then open without signing in
  and every link keeps the key. Anyone who has that address can open your library, so treat it as a password; revoke
  the key if the device is lost ([API keys](account.md#api-keys)).

## What it shows

- **The libraries** you may browse; with one, its page straight away.
- **A library's page:** the search box, the books added last (**All recently added** for more), then **Browse**: **All
  books by title**, every field that has values (**Authors**, **Series**, **Categories**, **Languages**…) and **Tags**.
- **Lists** come 20 books a page, each with a small cover, its title, authors and series. **Next page** goes on,
  **First page** goes back to the start; the browser's Back returns to the page before.
- **Search** in the same search language as the app: words, `author:"Terry Pratchett"`, `series:Discworld`, `-` to
  leave something out ([Searching](searching.md)). Results come by title. A blank search, or one that finds nothing,
  shows how to search.
- **Authors, series and the other fields:** a long list opens on its **First letters** (**All, from the start** for
  every name, other alphabets included); each name shows how many books it has. A series lists its books by number; a
  tag with tags under it lists them.
- **A book's page:** cover, title, authors, series and the other fields (each a link to its books), tags, then its
  **files** with format and size, a **Download** for each (an FB2 also **Download FB2, zipped**: much smaller), and the
  description last.
- Only the books you may see are there: the same libraries and content filters as in the app. Downloading needs the
  **Download** right; without it, the page says so.

## The device's formats

The formats the device opens come first, on a book's page and in lists (bold there); the others follow, dimmed, under
**Other formats**, and can still be downloaded (for another device, or to copy later).

- The device is recognised by its browser: **Kindle** (AZW3, AZW, MOBI, PDF, TXT), **Kobo** (KEPUB, EPUB, PDF, CBZ…),
  **PocketBook** (FB2, EPUB, PDF, DJVU…), **Tolino** (EPUB, PDF, TXT), **Boox** (EPUB, FB2, PDF, DJVU…).
- **Change**, next to **Formats ordered for …** on a book's page, picks another device or **Another device: no order**
  for this browser; **Automatic** goes back to recognising it.
- A book in none of the device's formats can still be downloaded; converting it for the device comes in a later
  version.

## Each device

The menus below are those of recent firmware; older ones may name them differently. Wi-Fi must be on, and the device
must reach your Librarion address (at home, the server's address on your network).

### Kindle

1. Home → the menu (**⋮**) → **Experimental Browser** (on some models **Browser**, or under **Settings → Device
   options → Advanced options**).
2. Type the address followed by `/e/`; sign in. Bookmark the page (the menu → **Add bookmark**).
3. Open a book and tap **Download** on an **AZW3**, **MOBI**, **PDF** or **TXT** file: the Kindle asks to download and
   the book appears in the library. The Kindle's browser downloads nothing else (no EPUB or FB2): convert those first,
   or use another way to the device.

### Kobo

1. **More** (☰) → **Beta features** → **Web browser** → **Start**.
2. Type the address followed by `/e/`; sign in.
3. Tap **Download** on a **KEPUB** or **EPUB** (or PDF, CBZ): Kobo saves it and adds it to **My books**. A KEPUB opens
   in Kobo's own reader (page and time left), an EPUB in the basic one.

### PocketBook

1. **Applications** → **Browser**.
2. Type the address followed by `/e/`; sign in. Add it to the browser's bookmarks.
3. Tap **Download** on an **FB2**, **EPUB**, PDF or DJVU file: PocketBook saves it in its **Download** folder, and the
   **Library** shows it. **FB2, zipped** works too.

### Tolino and Boox

- **Tolino:** the **Browser** in the main menu; EPUB and PDF download into the library.
- **Boox:** the **Browser** app (Android); downloads land in the **Download** folder; open them from **Library** or
  **Storage**. Boox can also install a reader app with an OPDS catalog ([Reader apps](../opds.md)).

## When something goes wrong

- **Not found:** the address is wrong, the book is hidden for you, or the e-reader version is switched off.
- **The form has expired:** the page stayed open too long; go back, reload it and send the form again.
- **Key not accepted:** the key in the address was revoked, has ended, isn't allowed in the address, or keys in the
  address are switched off; make a new key, or sign in without one.
- **Not set up yet:** the server has no administrator; finish the [first start](../first-start.md) on a computer or a
  phone.
- **Covers or pages look washed out:** the e-reader version is gray on purpose; everything still works.
