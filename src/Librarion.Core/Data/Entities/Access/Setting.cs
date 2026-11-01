namespace Librarion.Core.Data;

/// <summary>
/// A setting at one level (<see cref="Settings.SettingCatalog"/>): one row per key, so a key added later needs no
/// column. A row exists only where the value differs from the one inherited, or where it is locked.
/// </summary>
public sealed class Setting
{
    public SettingLevel Level { get; set; }

    /// <summary>The tenant's, library's or user's id; 0 at the server level.</summary>
    public int OwnerId { get; set; }

    /// <summary>The tenant the row belongs to; null for server rows and server administrators' own.</summary>
    public int? TenantId { get; set; }

    public string Key { get; set; } = string.Empty;

    /// <summary>The value as JSON; null when the row only locks the inherited value.</summary>
    public string? Value { get; set; }

    /// <summary>The levels below can't change it.</summary>
    public bool Locked { get; set; }
}

/// <summary>From the widest to the narrowest: a level inherits every key it doesn't set from the one above.</summary>
public enum SettingLevel
{
    Server,
    Tenant,
    Library,
    User,
}
