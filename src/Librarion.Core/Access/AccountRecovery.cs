using System.Collections.Concurrent;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Librarion.Core.Access;

/// <param name="Code">Shown only where the server runs (its console and <see cref="AccountRecovery.FilePath"/>), never to the browser.</param>
public sealed record RecoveryCode(int UserId, string UserName, string Code, DateTime Expires);

/// <summary>
/// The way back in for a server administrator locked out or who forgot the password, without editing a file or
/// restarting (ported from SFE): the sign-in page asks for a one-time code, which the server shows only where it runs
/// (its console, and <see cref="FilePath"/> for a service without one); with it, a new password unlocks the account and
/// signs it in. A code lives <see cref="Lifetime"/>, works once, and stays the same until it expires (others asking
/// can't replace the one its administrator is reading). Other accounts are unlocked by an administrator.
/// </summary>
public sealed class AccountRecovery(TenantDb tenantDb, AccessService accounts, string filePath, TimeProvider time, ILogger<AccountRecovery> logger)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<int, RecoveryCode> _codes = new();
    private readonly Lock _fileLock = new();

    public string FilePath { get; } = filePath;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// The pending code of <paramref name="userName"/>, made when it has none. Null when the name is not an enabled
    /// local server administrator (the caller answers the same either way).
    /// </summary>
    /// <returns>The code, and whether it was just made.</returns>
    public async Task<(RecoveryCode Code, bool New)?> RequestAsync(string? userName, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(userName, cancellationToken) is not { } user)
            return null;

        if (_codes.TryGetValue(user.Id, out var pending) && pending.Expires > Now)
            return (pending, false);

        var code = new RecoveryCode(user.Id, user.UserName, GeneratedSetupCode.Generate(), Now + Lifetime);
        _codes[user.Id] = code;
        WriteFile();
        return (code, true);
    }

    /// <summary>Gives the account a new password with its pending code: unlocks it and ends its other sessions.</summary>
    public async Task<AccessResult<SessionUser>> CompleteAsync(string? userName, string? code, string? password, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userName, cancellationToken);
        if (user is null || !_codes.TryGetValue(user.Id, out var pending) || pending.Expires <= Now
            || !PasswordHashing.SameSecret(GeneratedSetupCode.Normalize(code), GeneratedSetupCode.Normalize(pending.Code)))
            return AccessResult<SessionUser>.Invalid("code", "recovery.wrongCode");

        var result = await accounts.UpdateUserAsync(user.Id,
            new UserInput(user.UserName, user.DisplayName, password ?? string.Empty, IsServerAdmin: true, Disabled: false, Unlock: true),
            null, cancellationToken);
        if (result.Status != AccessStatus.Ok)
            return result.As<SessionUser>();

        _codes.TryRemove(user.Id, out _);
        WriteFile();
        return AccessResult<SessionUser>.Ok((await accounts.GetSessionUserAsync(user.Id, cancellationToken))!);
    }

    private async Task<UserAccount?> FindAsync(string? userName, CancellationToken cancellationToken)
    {
        var name = userName?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(name))
            return null;
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        return await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == name && u.Source == UserAccount.LocalSource && u.IsServerAdmin && !u.Disabled, cancellationToken);
    }

    /// <summary>The pending codes, one per line; no file when there is none.</summary>
    private void WriteFile()
    {
        lock (_fileLock)
        {
            var lines = _codes.Values.Where(c => c.Expires > Now).OrderBy(c => c.UserName, StringComparer.Ordinal)
                .Select(c => $"{c.UserName}: {c.Code} (until {c.Expires:HH:mm} UTC)").ToList();
            try
            {
                if (lines.Count == 0)
                {
                    File.Delete(FilePath);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllLines(FilePath, ["Codes to recover an administrator's account on the sign-in page, each usable once:", .. lines]);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Cannot write the recovery codes to {Path}: they are only in the console", FilePath);
            }
        }
    }
}
