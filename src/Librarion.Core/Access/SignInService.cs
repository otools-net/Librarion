using System.Globalization;
using Librarion.Core.Data;

namespace Librarion.Core.Access;

public enum SignInStatus
{
    Ok,
    Invalid,
    LockedOut,
    Disabled,
}

public sealed record SignInResult(SignInStatus Status, SessionUser? User = null, DateTime? LockedUntil = null);

/// <summary>
/// Checks a name and password against the directories in their order, the local accounts first: the first one that
/// knows the account decides. Only the local directory exists so far; a directory account's local row (made on its
/// first sign-in, as SFE) comes with the first directory.
/// </summary>
public sealed class SignInService(IEnumerable<IUserDirectory> directories, AccessService accounts)
{
    public async Task<SignInResult> SignInAsync(string? userName, string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            return new SignInResult(SignInStatus.Invalid);

        foreach (var directory in directories.OrderBy(d => d.Source == UserAccount.LocalSource ? 0 : 1))
        {
            var result = await directory.ValidateAsync(userName, password, cancellationToken);
            switch (result.Status)
            {
                case DirectoryStatus.Unknown:
                    continue;
                case DirectoryStatus.Invalid:
                    return new SignInResult(SignInStatus.Invalid);
                case DirectoryStatus.LockedOut:
                    return new SignInResult(SignInStatus.LockedOut, LockedUntil: result.LockedUntil);
            }

            if (result.User!.Source != UserAccount.LocalSource)
                return new SignInResult(SignInStatus.Invalid);
            var user = await accounts.GetSessionUserAsync(int.Parse(result.User.ExternalId, CultureInfo.InvariantCulture), cancellationToken);
            if (user is null)
                return new SignInResult(SignInStatus.Invalid);
            if (user.Disabled)
                return new SignInResult(SignInStatus.Disabled);

            await accounts.RecordSignInAsync(user.Id, cancellationToken);
            return new SignInResult(SignInStatus.Ok, user);
        }

        return new SignInResult(SignInStatus.Invalid);
    }
}
