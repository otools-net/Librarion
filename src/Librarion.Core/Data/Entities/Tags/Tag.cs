namespace Librarion.Core.Data;

/// <summary>
/// A tag of a tenant's vocabulary (SFE / TagStudio model, agents/docs/features/tags.md). A <b>field</b> is a category
/// tag with a <see cref="FieldKey"/> (<c>author</c>, <c>series</c>); its values are tags with <see cref="FieldId"/>
/// set (<c>author:Stephen King</c>, agents/docs/features/metadata-tags.md); plain tags have neither.
/// </summary>
public sealed class Tag
{
    public const int MaxNameLength = 300;
    public const int MaxFieldKeyLength = 50;

    public int Id { get; set; }
    public int TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Trimmed, case-folded, NFKC, single spaces (<see cref="Tagging.TagNames.Normalize"/>): what lookups compare.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>What lists sort by ("King, Stephen"); the name when there is no other.</summary>
    public string SortName { get; set; } = string.Empty;

    public string? Shorthand { get; set; }
    public string? Color { get; set; }

    /// <summary>Groups its children when a book's tags are listed.</summary>
    public bool IsCategory { get; set; }

    /// <summary>Books with it are left out of search and feeds unless the search names it or an ancestor.</summary>
    public bool IsHidden { get; set; }

    /// <summary>Shown after the name to tell two tags of one name apart: "Freddy (FNAF)".</summary>
    public int? DisambiguationParentId { get; set; }

    /// <summary>Set on a field's own row: lower case <c>[a-z0-9-]</c>, unique in the tenant.</summary>
    public string? FieldKey { get; set; }

    /// <summary>The field this tag is a value of; null for plain tags and fields.</summary>
    public int? FieldId { get; set; }
    public Tag? Field { get; set; }

    /// <summary>Set for the tags every tenant has, which are edited but never deleted.</summary>
    public BuiltInTag? BuiltIn { get; set; }

    public List<TagAlias> Aliases { get; set; } = [];
}

public enum BuiltInTag
{
    MetaTags,
    Archived,
    Author,
    Translator,
    Editor,
    Illustrator,
    Series,
    Category,
    Publisher,
    Language,
    Year,
}

/// <summary>Another name of a tag ("Steven King" for Stephen King): found by search and by the importer.</summary>
public sealed class TagAlias
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int TagId { get; set; }
    public Tag? Tag { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
}

/// <summary>Child "is a" parent; many parents allowed, no cycles. A book tagged with the child is found by the parent.</summary>
public sealed class TagParent
{
    public int TenantId { get; set; }
    public int ChildId { get; set; }
    public Tag? Child { get; set; }
    public int ParentId { get; set; }
    public Tag? Parent { get; set; }
}

/// <summary>
/// Every (ancestor, descendant) pair of the parent graph, itself included at depth 0, and a field over its values at
/// depth 1: "has this tag or a descendant" is one join (agents/docs/features/tags.md#scale).
/// </summary>
public sealed class TagClosure
{
    public int TenantId { get; set; }
    public int AncestorId { get; set; }
    public Tag? Ancestor { get; set; }
    public int DescendantId { get; set; }
    public Tag? Descendant { get; set; }
    public int Depth { get; set; }
}

/// <summary>A tag on a book, with where it came from; for field values also the order (authors) and number (series).</summary>
public sealed class BookTag
{
    public int TenantId { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public int TagId { get; set; }
    public Tag? Tag { get; set; }
    public TagSource Source { get; set; }

    /// <summary>Order among the book's values of the field (the first author is the one on cards).</summary>
    public int? Ordinal { get; set; }

    /// <summary>The book's number in a series: 2, 2.5.</summary>
    public double? Number { get; set; }
}

public enum TagSource : byte
{
    Imported,
    Manual,
    Rule,
}

/// <summary>Books of a library with the tag or a descendant, kept by imports and tag changes (never counted per request).</summary>
public sealed class TagCount
{
    public int TenantId { get; set; }
    public int LibraryId { get; set; }
    public int TagId { get; set; }
    public int BookCount { get; set; }
}

/// <summary>A content filter of a group (D5): its members see only books with an allowed tag, never one with a denied tag.</summary>
public sealed class GroupTagFilter
{
    public int GroupId { get; set; }
    public UserGroup? Group { get; set; }
    public int TagId { get; set; }
    public Tag? Tag { get; set; }
    public bool Deny { get; set; }
}
