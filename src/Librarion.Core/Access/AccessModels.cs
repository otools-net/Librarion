using Librarion.Core.Data;

namespace Librarion.Core.Access;

/// <param name="DisabledRights">Rights nobody gets in this tenant (<see cref="TenantDisabledRight"/>).</param>
public sealed record TenantInfo(int Id, string Slug, string Name, bool Disabled, DateTime Created, IReadOnlyList<string> DisabledRights);

public sealed record LibraryInfo(int Id, int TenantId, string Name, DateTime Created);

public sealed record MembershipInfo(int GroupId, string Group);

/// <param name="TenantId">Its tenant; null for a server administrator.</param>
/// <param name="Source"><c>local</c>, or the directory the account comes from (its password is checked there).</param>
/// <param name="LockedOut">Too many wrong passwords: locked until a while has passed or an administrator unlocks it.</param>
public sealed record UserInfo(
    int Id, int? TenantId, string UserName, string? DisplayName, string Source, bool IsServerAdmin, bool Disabled, bool LockedOut,
    DateTime Created, DateTime? LastSignIn, IReadOnlyList<MembershipInfo> Groups);

public sealed record GroupInfo(
    int Id, int TenantId, string Name, BuiltInGroup? BuiltIn, string? ExternalName, bool AllLibraries,
    IReadOnlyList<string> Rights, IReadOnlyList<int> LibraryIds, IReadOnlyList<int> MemberIds);

/// <param name="Password">Required for a new local account; null keeps the current one.</param>
/// <param name="GroupIds">The account's groups (of its tenant), replacing those it had; null keeps them.</param>
/// <param name="Unlock">Clears a lock left by wrong passwords.</param>
public sealed record UserInput(
    string? UserName, string? DisplayName = null, string? Password = null, bool IsServerAdmin = false, bool Disabled = false,
    IReadOnlyList<int>? GroupIds = null, bool Unlock = false);

/// <param name="Rights">The group's rights, replacing its current ones; null keeps them.</param>
/// <param name="LibraryIds">The libraries its library rights apply in, when not <paramref name="AllLibraries"/>; null keeps them.</param>
/// <param name="MemberIds">The group's members, replacing its current ones; null keeps them.</param>
public sealed record GroupInput(
    string? Name, bool AllLibraries = false, IReadOnlyList<string>? Rights = null, IReadOnlyList<int>? LibraryIds = null,
    IReadOnlyList<int>? MemberIds = null, string? ExternalName = null);

/// <param name="Slug">Short name; made from the name when empty. Fixed once created.</param>
/// <param name="LibraryName">Name of its first library, at creation; empty = "Library".</param>
/// <param name="DisabledRights">Rights nobody gets in this tenant; null keeps the current ones.</param>
public sealed record TenantInput(string? Name, string? Slug = null, bool Disabled = false, string? LibraryName = null, IReadOnlyList<string>? DisabledRights = null);

/// <summary>What a request needs to know about the signed-in account (cached, see <see cref="AccessService.GetSessionUserAsync"/>).</summary>
public sealed record SessionUser(int Id, int? TenantId, string UserName, string? DisplayName, string Source, bool IsServerAdmin, bool Disabled, string SecurityStamp)
{
    public bool IsLocal => Source == UserAccount.LocalSource;
}

public enum AccessStatus
{
    Ok,
    Invalid,
    NotFound,

    /// <summary>The change would break something (the last administrator, a built-in group...): see the problem.</summary>
    Conflict,
}

public sealed record AccessResult<T>(AccessStatus Status, T? Value = default, Dictionary<string, string[]>? Errors = null, string? Problem = null)
{
    public static AccessResult<T> Ok(T value) => new(AccessStatus.Ok, value);
    public static AccessResult<T> Invalid(string key, string message) => new(AccessStatus.Invalid, Errors: new() { [key] = [message] });
    public static AccessResult<T> Missing() => new(AccessStatus.NotFound);
    public static AccessResult<T> Conflict(string problem) => new(AccessStatus.Conflict, Problem: problem);

    /// <summary>The same failure, for another kind of value.</summary>
    public AccessResult<TOther> As<TOther>() => new(Status, default, Errors, Problem);
}
