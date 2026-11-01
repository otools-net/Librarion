namespace Librarion.Core.Access;

/// <summary>
/// Counts the changes to accounts, groups, rights, tenants and libraries. The caches that read them (rights, sessions,
/// setup state) keep an entry only while the count is the one they read it at, so a change applies to the next
/// request (as SFE).
/// </summary>
public sealed class AccessChanges
{
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    public void Changed() => Interlocked.Increment(ref _version);
}
