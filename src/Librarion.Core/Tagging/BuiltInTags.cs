using Librarion.Core.Data;

namespace Librarion.Core.Tagging;

/// <summary>
/// The tags every tenant starts with (agents/docs/features/tags.md, metadata-tags.md): the category Meta Tags, the
/// hidden tag Archived, and the built-in fields. Edited, never deleted.
/// </summary>
public static class BuiltInTags
{
    /// <summary>The built-in fields and their keys.</summary>
    public static readonly IReadOnlyDictionary<BuiltInTag, string> Fields = new Dictionary<BuiltInTag, string>
    {
        [BuiltInTag.Author] = "author",
        [BuiltInTag.Translator] = "translator",
        [BuiltInTag.Editor] = "editor",
        [BuiltInTag.Illustrator] = "illustrator",
        [BuiltInTag.Series] = "series",
        [BuiltInTag.Category] = "category",
        [BuiltInTag.Publisher] = "publisher",
        [BuiltInTag.Language] = "language",
        [BuiltInTag.Year] = "year",
    };

    /// <summary>New rows for a new tenant, with their closure rows (the tenant id is set by the caller's graph).</summary>
    public static (List<Tag> Tags, List<TagClosure> Closure) Create(int tenantId = 0)
    {
        var tags = new List<Tag>
        {
            Make(tenantId, "Meta Tags", BuiltInTag.MetaTags, isCategory: true),
            Make(tenantId, "Archived", BuiltInTag.Archived, isHidden: true),
        };
        foreach (var (builtIn, key) in Fields)
        {
            var field = Make(tenantId, key, builtIn, isCategory: true);
            field.FieldKey = key;
            tags.Add(field);
        }

        var closure = tags.Select(t => new TagClosure { TenantId = tenantId, Ancestor = t, Descendant = t, Depth = 0 }).ToList();
        return (tags, closure);
    }

    private static Tag Make(int tenantId, string name, BuiltInTag builtIn, bool isCategory = false, bool isHidden = false) => new()
    {
        TenantId = tenantId,
        Name = name,
        NormalizedName = TagNames.Normalize(name),
        SortName = name,
        IsCategory = isCategory,
        IsHidden = isHidden,
        BuiltIn = builtIn,
    };
}
