using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Librarion.Api.Opds;

/// <summary>
/// Successful Basic password checks, remembered for <see cref="Lifetime"/> (D-7.4, agents/docs/features/opds-auth.md):
/// reader apps send the password with every request, and PBKDF2 is slow on purpose. An entry is the name and a keyed
/// hash of the password (the key made per process, never stored) with the account and its security stamp at the
/// time: the caller still checks the stamp against the account's current one, so a new password, a disabled account or
/// a sign-out everywhere ends it at once. Failures are never remembered. In memory: a restart forgets it.
/// </summary>
public sealed class BasicVerificationCache(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Entries kept at most; beyond, the expired ones go first, then all.</summary>
    public const int MaxEntries = 10_000;

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed record Entry(int UserId, string Stamp, DateTimeOffset Expires);

    /// <summary>The account a name and password were found to be, while the entry lives.</summary>
    public (int UserId, string Stamp)? Find(string userName, string password)
    {
        var key = Key(userName, password);
        if (!_entries.TryGetValue(key, out var entry))
            return null;
        if (entry.Expires > time.GetUtcNow())
            return (entry.UserId, entry.Stamp);
        _entries.TryRemove(key, out _);
        return null;
    }

    public void Remember(string userName, string password, int userId, string stamp)
    {
        if (_entries.Count >= MaxEntries)
            Trim();
        _entries[Key(userName, password)] = new Entry(userId, stamp, time.GetUtcNow() + Lifetime);
    }

    public void Forget(string userName, string password) => _entries.TryRemove(Key(userName, password), out _);

    private string Key(string userName, string password) =>
        userName.Trim().ToUpperInvariant() + "\n" + Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(password)));

    private void Trim()
    {
        var now = time.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
            if (entry.Expires <= now)
                _entries.TryRemove(key, out _);
        }

        if (_entries.Count >= MaxEntries)
            _entries.Clear();
    }
}
