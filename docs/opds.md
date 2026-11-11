# Reader apps (OPDS)

Librarion serves your libraries as an **OPDS catalog**, the format e-book reader apps use to browse a library, search
it and download books straight onto the device: KOReader, Moon+ Reader, Thorium Reader, PocketBook's and many others.
You need the **Reader apps (OPDS)** right in the library, and **Download** to download (Users, Curators and
Administrators have both).

## The address

- The catalog is at your Librarion address followed by `/opds`, for example `https://books.example.org/opds` (or
  `http://192.168.1.20:5080/opds` at home). **My account → API keys** shows it.
- With one library you get its catalog at once; with several, a list of them, each its own catalog. There is no
  catalog across libraries: search works inside one library.

## Signing in

Reader apps don't use the web app's sign-in. Choose one of these:

- **Your user name and password**, in the app's catalog settings. Simple, but the password is stored in the app.
- **Your user name and an API key as the password** (recommended): make a key for the device in
  **My account → API keys** with **Reader apps (OPDS)** ticked ([API keys](user-guide/account.md#api-keys)). If the
  device is lost, revoke its key; your password stays private.
- **The key in the address**, for apps that can't sign in at all: tick **Can be used in the address** when making
  the key and use the address shown under it (it ends in `/opds/k/` and the key). Anyone who has that address can
  open your catalog, so keep it like a password. An administrator may have switched this off.

Five wrong passwords in a row from the same address make that address wait 15 minutes, as on the sign-in page; an app
with an old key or password stored can lock itself out that way, so fix its settings when it starts failing. A
changed password or a disabled account ends every key at once.

## What the catalog shows

- **Recently added** (newest first) and **All books by title**.
- **My shelves**, once you have one ([Shelves](user-guide/shelves.md)): each shelf's books of that library, in your
  shelf's order.
- **Authors**, **Series**, **Categories**, **Languages**, **Publishers**, **Years** and your libraries' other fields,
  then **Tags**: with many names, first by letter (**All** lists every one from the start, names in other alphabets
  too), then each name's books. A series lists its books by their number; a tag with tags under it lists them too.
- **Search** from the app's search box, in the same search language as the web app: words, `author:"Terry
  Pratchett"`, `-` to leave something out ([Searching](user-guide/searching.md)).
- Each book with its cover, title (the file name when the book has none), authors, series and number, language,
  categories and description, and a download link per format. **FB2** books are also offered as **FB2.ZIP**, much
  smaller to download (zipped as they are sent).
- Lists come 50 books at a time; the app asks for the next ones as you scroll or turn the page.
- Only the books you may see are there: the same libraries and content filters as in the web app.

## Setting up an app

The menus below are those of recent versions; older ones may name them differently. In each, the catalog address is
the one from **My account → API keys**.

### KOReader

1. Open the file browser's top menu → **Search** (magnifier) → **OPDS catalog**.
2. Tap **+** (add a catalog): **Catalog name** (anything), **Catalog URL** the address, **Username** your user name,
   **Password** your password or an API key. With a key in the address, leave the name and password empty.
3. Open the catalog; long-press a book's format to download it. KOReader keeps the download folder you choose.

### Moon+ Reader

1. Menu → **Net Library** (the globe) → **Add** (a new catalog; on some versions under **OPDS** or **Network**).
2. Enter a title and the address; Moon+ asks for the user name and password when the catalog answers that it needs
   them (use your password or an API key).
3. Browse, then tap a book and **Download**: it opens in the reader.

### Thorium Reader

1. **Catalogs** (top bar) → **Add OPDS feed**: a title and the address.
2. Thorium asks for a user name and password when the catalog needs them; if your version doesn't, use the address
   with the key in it (make the key with **Can be used in the address**).
3. Open a book and **Add to bookshelf** to download it.

## Good to know

- A reader app may ask for up to 600 pages a minute; a well-behaved app stays far below that.
- Covers are JPEG, which every reader app shows.
- Anonymous browsing (without signing in) is not available.
