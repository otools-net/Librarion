namespace Librarion.Core.Data;

/// <summary>
/// An account. It belongs to one tenant (<see cref="TenantId"/>), or to none when it is a server administrator. Names
/// are unique on the whole server whatever the case, since OPDS readers sign in with a name alone. Local accounts have
/// a password here; accounts of a directory (<see cref="Source"/> other than <c>local</c>) are checked there.
/// </summary>
public sealed class UserAccount
{
    public const string LocalSource = "local";

    public int Id { get; set; }

    /// <summary>Its tenant; null for a server administrator.</summary>
    public int? TenantId { get; set; }

    public Tenant? Tenant { get; set; }

    /// <summary>The name signed in with, as written.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary><see cref="UserName"/> in upper case: unique, and what sign-ins look up.</summary>
    public string NormalizedUserName { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>Where the password is checked: <see cref="LocalSource"/> or a directory's name.</summary>
    public string Source { get; set; } = LocalSource;

    /// <summary>The directory's own id of the account; null for local accounts.</summary>
    public string? ExternalId { get; set; }

    /// <summary>PBKDF2 hash (<see cref="Access.PasswordHashing"/>); null for directory accounts.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Every right in every tenant, and the server's own. Has no <see cref="TenantId"/>.</summary>
    public bool IsServerAdmin { get; set; }

    public bool Disabled { get; set; }

    /// <summary>Changes with the password or the account's status: sessions made before stop working.</summary>
    public string SecurityStamp { get; set; } = string.Empty;

    public int FailedSignIns { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime Created { get; set; }
    public DateTime? LastSignIn { get; set; }

    public List<GroupMember> Groups { get; set; } = [];
}
