namespace Librarion.Core.Data;

/// <summary>
/// Accounts of a tenant with the same rights. Tenant rights apply in the whole tenant, library rights in the group's
/// libraries: all of them (<see cref="AllLibraries"/>) or those listed (<see cref="Libraries"/>).
/// </summary>
public sealed class UserGroup
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Set for the built-in groups, which cannot be deleted; null for groups made by hand.</summary>
    public BuiltInGroup? BuiltIn { get; set; }

    /// <summary>Name of a directory group whose members belong to this one (synchronised at sign-in); null = by hand.</summary>
    public string? ExternalName { get; set; }

    /// <summary>Its library rights apply in every library of the tenant, those created later too.</summary>
    public bool AllLibraries { get; set; }

    public List<GroupMember> Members { get; set; } = [];
    public List<GroupRight> Rights { get; set; } = [];
    public List<GroupLibrary> Libraries { get; set; } = [];
}

public enum BuiltInGroup
{
    Administrators,
    Users,
}

public sealed class GroupMember
{
    public int GroupId { get; set; }
    public UserGroup? Group { get; set; }
    public int UserId { get; set; }
    public UserAccount? User { get; set; }
}

/// <summary>A right granted to a group, by its id in <see cref="Access.RightCatalog"/>.</summary>
public sealed class GroupRight
{
    public int GroupId { get; set; }
    public UserGroup? Group { get; set; }
    public string Right { get; set; } = string.Empty;
}

/// <summary>A library where the group's library rights apply (unless the group has <see cref="UserGroup.AllLibraries"/>).</summary>
public sealed class GroupLibrary
{
    public int GroupId { get; set; }
    public UserGroup? Group { get; set; }
    public int LibraryId { get; set; }
    public Library? Library { get; set; }
}

/// <summary>A right nobody gets in this tenant, whatever its groups grant (set by server administrators).</summary>
public sealed class TenantDisabledRight
{
    public int TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public string Right { get; set; } = string.Empty;
}
