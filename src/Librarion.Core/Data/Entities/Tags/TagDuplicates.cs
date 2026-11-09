namespace Librarion.Core.Data;

/// <summary>Why tags may be one (stage 6 chunk 14): the same words, words that sound alike, or a name and its initials.</summary>
public enum TagDuplicateLevel : byte
{
    Words,
    Sound,
    Initials,
}

/// <summary>
/// Tags of one field (or plain tags) sharing a match key (agents/docs/features/merging.md#possible-duplicate-tags-built-stage-6-chunk-14):
/// built by <c>TagDuplicateIndex</c> and kept at the end of imports and on edits. <see cref="TagDuplicateLevel.Words"/> and
/// <see cref="TagDuplicateLevel.Sound"/> groups come from the sound key (the same words when every name folds to the
/// same), <see cref="TagDuplicateLevel.Initials"/> from the initials key with at least one name of initials only. A
/// key shared by <c>TagDuplicateIndex.MaxGroup</c> tags or more is <see cref="Noise"/>, kept without members.
/// </summary>
public sealed class TagDuplicateGroup
{
    public long Id { get; set; }
    public int TenantId { get; set; }

    /// <summary>The field's tag id; 0 for plain tags (no null: the list's index range stays one equality).</summary>
    public int Field { get; set; }

    public TagDuplicateLevel Level { get; set; }

    /// <summary>The sound or initials key the tags share.</summary>
    public long Key { get; set; }

    public bool Noise { get; set; }

    public int TagCount { get; set; }

    /// <summary>The members' books (their direct links, every library) when the group was written: what the list ranks by.</summary>
    public int BookCount { get; set; }

    public List<TagDuplicateMember> Members { get; set; } = [];
}

public sealed class TagDuplicateMember
{
    public int TenantId { get; set; }
    public long GroupId { get; set; }
    public int TagId { get; set; }
}

/// <summary>Two tags a curator said are not the same (<see cref="TagId"/> &lt; <see cref="OtherId"/>): never suggested together again.</summary>
public sealed class NotSameTag
{
    public int TenantId { get; set; }
    public int TagId { get; set; }
    public int OtherId { get; set; }

    /// <summary>UTC.</summary>
    public DateTime Created { get; set; }
}
