using System.Globalization;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Librarion.Core.Access;

/// <summary>
/// Where accounts and passwords are checked (D14, as SFE). The local accounts are one (<see cref="LocalUserDirectory"/>);
/// a directory such as LDAP or OIDC is another, registered next to it. Whatever answers, rights, groups and preferences
/// hang on the account's row in <see cref="LibrarionDbContext.Users"/>.
/// </summary>
public interface IUserDirectory
{
    /// <summary>Name in <see cref="UserAccount.Source"/>.</summary>
    string Source { get; }

    Task<DirectoryResult> ValidateAsync(string userName, string password, CancellationToken cancellationToken);
}

/// <param name="ExternalId">The directory's own id of the account (for local accounts, the row's id).</param>
public sealed record DirectoryUser(string Source, string ExternalId, string UserName, string? DisplayName);

public enum DirectoryStatus
{
    Ok,

    /// <summary>The account is not in this directory: ask the next one.</summary>
    Unknown,

    /// <summary>The account is there but the password is wrong.</summary>
    Invalid,

    /// <summary>Too many wrong passwords: refused until <see cref="DirectoryResult.LockedUntil"/>.</summary>
    LockedOut,
}

public sealed record DirectoryResult(DirectoryStatus Status, DirectoryUser? User = null, DateTime? LockedUntil = null);

/// <summary>
/// The accounts with a password in the database. Locks an account for a while after repeated failures from every
/// address together, and stores a stronger hash when the password was hashed with fewer iterations than today's.
/// </summary>
public sealed class LocalUserDirectory(TenantDb tenantDb, AccessChanges changes, IOptionsMonitor<SecurityOptions> security, TimeProvider time) : IUserDirectory
{
    private static readonly string DummyHash = PasswordHashing.Hash(Guid.NewGuid().ToString());

    public string Source => UserAccount.LocalSource;

    public async Task<DirectoryResult> ValidateAsync(string userName, string password, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        var normalized = userName.Trim().ToUpperInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized && u.Source == UserAccount.LocalSource, cancellationToken);
        if (user is null)
        {
            // As long as a real check, so that the time taken doesn't tell which accounts exist.
            PasswordHashing.Verify(password, DummyHash, out _);
            return new DirectoryResult(DirectoryStatus.Unknown);
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (user.LockedUntil > now)
            return new DirectoryResult(DirectoryStatus.LockedOut, LockedUntil: user.LockedUntil);

        if (!PasswordHashing.Verify(password, user.PasswordHash, out var outdated))
        {
            var options = security.CurrentValue;
            user.FailedSignIns++;
            // From every address together: the Api counts each address's own (SignInThrottle), far fewer.
            if (options.AccountLockoutFailures > 0 && user.FailedSignIns >= options.AccountLockoutFailures)
            {
                user.FailedSignIns = 0;
                user.LockedUntil = now.AddMinutes(Math.Max(1, options.LockoutMinutes));
            }

            await db.SaveChangesAsync(cancellationToken);
            changes.Changed();
            return user.LockedUntil > now
                ? new DirectoryResult(DirectoryStatus.LockedOut, LockedUntil: user.LockedUntil)
                : new DirectoryResult(DirectoryStatus.Invalid);
        }

        if (user.FailedSignIns != 0 || user.LockedUntil is not null || outdated)
        {
            user.FailedSignIns = 0;
            user.LockedUntil = null;
            if (outdated)
                user.PasswordHash = PasswordHashing.Hash(password);
            await db.SaveChangesAsync(cancellationToken);
        }

        return new DirectoryResult(DirectoryStatus.Ok,
            new DirectoryUser(Source, user.Id.ToString(CultureInfo.InvariantCulture), user.UserName, user.DisplayName));
    }
}
