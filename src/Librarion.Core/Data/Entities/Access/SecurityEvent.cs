namespace Librarion.Core.Data;

/// <summary>
/// Something an administrator may need to know about access: sign-ins, lockouts, changes of accounts and rights. Never
/// holds a secret. Kept <c>Librarion:Security:EventDays</c> days (<see cref="Access.SecurityLog"/>).
/// </summary>
public sealed class SecurityEvent
{
    public const int MaxTextLength = 200;

    public long Id { get; set; }
    public DateTime Time { get; set; }
    public SecurityEventKind Kind { get; set; }

    /// <summary>The tenant it concerns; null for the server's own (server administrators, tenants).</summary>
    public int? TenantId { get; set; }

    /// <summary>Who did it (the signed-in account), when known.</summary>
    public int? UserId { get; set; }

    /// <summary>The name of who did it, or the name typed at a failed sign-in.</summary>
    public string? UserName { get; set; }

    /// <summary>The address of the request.</summary>
    public string? Address { get; set; }

    /// <summary>What was concerned, in words (the account changed, the group's new rights...).</summary>
    public string? Detail { get; set; }
}

public enum SecurityEventKind
{
    SignedIn,
    SignInFailed,
    LockedOut,
    SignedOut,
    PasswordChanged,
    SetupCompleted,
    RecoveryRequested,
    RecoveryCompleted,
    UserCreated,
    UserChanged,
    UserDeleted,
    GroupCreated,
    GroupChanged,
    GroupDeleted,
    TenantCreated,
    TenantChanged,
    TenantDeleted,
    LibraryCreated,
    LibraryChanged,
    LibraryDeleted,
    SettingsChanged,
    ApiKeyCreated,
    ApiKeyRevoked,
}
