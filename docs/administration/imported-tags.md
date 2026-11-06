# Imported tags

Books bring their own tags and genres: an FB2 file's keywords and genres, an EPUB's subjects, a Calibre library's
tags, an INPX collection's genres. With many books from many sources, the same idea arrives under many names
(`fiction`, `Fiction`, `Fiction - General`) and some names are junk. **Administration → Catalog → Imported tags**
shows them, the most used first, so a few hundred clicks put most of a library in order.

It needs the **Imported tags** right ([rights](../reference/rights.md)), which Administrators and Curators have.

## The list

- **Library**: the list shows the tags of one library, by how many of its books have them. It appears when you may
  curate more than one.
- **Show**: plain tags, or one field's values (categories, languages, publishers, years, a Calibre library's own
  columns). Authors, translators and series are not listed here: they are put right by merging them under
  [Tags](tags.md).
- **From**: only the names that came from one source (FB2, EPUB, Calibre, INPX…), or from any.
- **Show** (status): **To review** (the default: some name of the tag hasn't been looked at), **Reviewed**, or **All**.
- Under the choices, a line says how many tags with imported names the library has, how many links imports gave them,
  and what share the first 500 and the reviewed ones hold. It shows how far the curation has come.
- Each row: the tag, its books in this library, the sources its names came from, and the names an import wrote when
  they differ from the tag's (`as “Fiction - General”`).

Long lists load 50 rows at a time with **More**. On a very large library, a page can stop before it is full: **More**
goes on from there.

## Keep, map, ignore

Tick the rows to change, then use the bar above the list (100 tags at a time at most). Changes apply to the whole
tenant, because a tag and its names are shared by every library. For that reason they need the Imported tags right in
every library of the tenant.

- **Keep**: the tag is right as it is. It moves to Reviewed. **Review again** (on the Reviewed list) puts it back.
- **Map to…**: choose the tag they really are, or type a new name to make it. Each chosen tag is merged into it:
  its books get that tag, and its names give that tag in later imports. It needs the **Manage tags** right too. Each
  merge can be undone under **Tags → Merges**.
- **Ignore**: imports skip the tags' names from now on, and the links imports gave them are taken off in every
  library. The tags themselves stay, with any tag a person gave a book by hand. Undo it under **Ignored**.

A change that touches more than 10,000 links (or more than 10 tags at once) runs in the background: the panel under
the top bar shows how it goes.

Re-reading or re-importing books follows these choices: a mapped name gives the tag it was mapped to, an ignored name
gives nothing, and a kept tag stays as it is.

## Ignored

**Ignored** lists what was ignored, newest first: the tag, the names ignored, and how many links were taken off.
**Undo** gives the names back to the tag and its links back to the books that are still there. The tag is then to
review again.
