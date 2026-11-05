# Tags

**Administration → Catalog → Tags** looks after the tenant's tags: the plain tags (Fantasy, To read) and the values of
each field (the authors, the series, the categories and the others). Its libraries share them: a change applies to
every library. It needs the **Manage tags** right ([rights](../reference/rights.md)), which Administrators and
Curators have.

## The list

- **Show** chooses what is listed: the plain tags, or one field's values. Each choice says how many there are ("Authors
  (10,000+)" past ten thousand).
- Every tag is listed, books or not, by name, 50 at a time: **More** brings the next ones. Each row says how many books
  have it, counting those of the tags under it once each (and, when that differs, how many are tagged with it
  itself), how many tags are under it, and marks the hidden ones, categories and built-in tags.
- **Find by name** finds tags whose name starts with what is typed, or has its words ("king" finds Stephen King). A
  search shows the first 50 found: type more to narrow it down.
- **New tag** (or **New value** for a field) makes one; **New field** makes a field, such as *mood*, with its key: what
  searches write before the colon (`mood:calm`). **Edit field** changes the shown field's name, color or other names.

## Editing a tag

Choose a tag in the list:

- **Name** and **Sorted as** (empty: the name; people are sorted by their last name, "King, Stephen").
- **Shorthand** and **Color**.
- **Category:** groups the tags under it when a book's tags are listed. **Hidden:** its books are left out of searches
  unless the search names it.
- **Other names**, one per line: searches and imports find the tag by them too.
- **Under:** the tags it belongs to; a book with this tag is found by those too (a book tagged *Epic* under *Fantasy*
  shows on *Fantasy*'s page and in `tag:Fantasy`). Type a name and choose it; a tag can't be put under itself or one of
  the tags under it. A plain tag goes under plain tags, a field's value under values of the same field.
- **Told apart by:** with two tags of the same name, one of its parents shown after it ("Freddy (FNAF)").

Wherever a tag is chosen, typing lists the tags whose name or other name starts with what you typed, whose shorthand
it is, or whose name has its words ("king" finds Stephen King), the most used first, each with its color, its books and
the other name it was found by. Choose one with a tap, or with the arrow keys and Enter; Escape closes the list.

**Save** applies it at once. Built-in tags (Meta Tags, Archived and the built-in fields) can be edited but not deleted;
the built-in fields keep their names.

## Merging

**Merge** (in the tag's editor) puts the tag into another one: type the name of the tag that stays and choose it. The
dialog says how many books move. After the merge:

- its books have the other tag (a book that had both keeps one);
- its name and other names become other names of the tag that stays, so books imported later under the old name land
  there too;
- the tags under it, its import mappings and the groups' content filters naming it move to the tag that stays.

**Merge field…** (shown for a field that isn't built in) merges a whole field into another: each value goes into the
value of the same name there, or moves there when it has none. The field's key keeps working: after merging *genre*
into *category*, `genre:horror` still finds the books.

A merge touching more than 10,000 links runs in the background: the panel under the top bar shows how it goes, and
**Administration → Background tasks → History** keeps it.

## Undoing a merge

**Merges** lists the merges, newest first, with what went where, how many links moved and when. **Undo** puts the
merged tag back as it was, with its books, names, parents and filters. It can't be undone after the tag it went into was
deleted, or while a later merge into that tag stands (undo that one first).

## Deleting

**Delete** (in the tag's editor) removes the tag from every book; the dialog says how many books lose it. It can't be
undone (merging can). It isn't offered for built-in tags, a field that still has values (merge it instead), or a tag a
group's content filter names (take it out of the filter first). A deleted tag that came with imported books comes back
when those books are imported again.
