using Librarion.Core.Catalog;

namespace Librarion.Core.Data;

/// <summary>
/// Books sharing a blocking key (stage 6 chunk 11, D-6.6, agents/docs/features/merging.md#stored-duplicate-groups): the
/// same file, identifier or work. Built by <c>DuplicateIndex</c> and kept by the end of each scan or import and by
/// edits. A key shared by <c>DuplicateIndex.MaxGroup</c> books or more is <see cref="Noise"/>: kept without
/// members, never read again until a rebuild. A weak key (an FB2 id) makes one group per title and one per first
/// author among its books, all with the same <see cref="Key"/>.
/// </summary>
public sealed class DuplicateGroup
{
    public const int MaxKeyLength = 600;

    public long Id { get; set; }
    public int TenantId { get; set; }
    public DuplicateReason Level { get; set; }

    /// <summary>What the books share: <c>size:crc</c>, <c>scheme:value</c>, or the work key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>XxHash64 of <see cref="Level"/> and <see cref="Key"/>: the index the upkeep finds a key's rows by.</summary>
    public long KeyHash { get; set; }

    public bool Noise { get; set; }

    /// <summary>Its members (0 for noise).</summary>
    public int BookCount { get; set; }

    public List<DuplicateMember> Members { get; set; } = [];
}

public sealed class DuplicateMember
{
    public int TenantId { get; set; }
    public long GroupId { get; set; }
    public int BookId { get; set; }
}

/// <summary>
/// Books a user said are different (pair, <see cref="BookId"/> &lt; <see cref="OtherId"/>), or a book said to be unique
/// (<see cref="OtherId"/> = <see cref="BookId"/>): never suggested together again, also after re-imports and rebuilds.
/// </summary>
public sealed class NotDuplicate
{
    public int TenantId { get; set; }
    public int BookId { get; set; }
    public int OtherId { get; set; }

    /// <summary>UTC.</summary>
    public DateTime Created { get; set; }
}
