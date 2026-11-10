using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Access;

/// <summary>What an API key may be used for (D-7.2); they only narrow the account's rights.</summary>
public static class ApiKeyScopes
{
    /// <summary>The OPDS feeds (<c>/opds</c>).</summary>
    public const string Opds = "opds";

    /// <summary>The REST API's reading routes (GET).</summary>
    public const string ApiRead = "api:read";

    /// <summary>Every route of the REST API (includes <see cref="ApiRead"/>).</summary>
    public const string ApiWrite = "api:write";

    public static IReadOnlyList<string> All { get; } = [Opds, ApiRead, ApiWrite];
}

/// <param name="Expires">When it stops working (UTC); null = until revoked (the user's answer, 2026-11-10).</param>
/// <param name="LibraryIds">The libraries it is limited to; empty = every library the account may reach.</param>
public sealed record ApiKeyInput(string? Name, IReadOnlyList<string>? Scopes, IReadOnlyList<int>? LibraryIds = null, bool PathAllowed = false, DateTime? Expires = null);

/// <summary>A key as listed: never its secret.</summary>
public sealed record ApiKeyInfo(int Id, string Name, IReadOnlyList<string> Scopes, IReadOnlyList<int> LibraryIds, bool PathAllowed,
    DateTime Created, DateTime? Expires, DateTime? Revoked, DateTime? LastUsed, string? LastAddress);

/// <summary>A key just made, with the one time its whole text is shown.</summary>
public sealed record NewApiKey(ApiKeyInfo Info, string Key);

/// <summary>A key that is valid now: whose it is and what it may do.</summary>
public sealed record ApiKeyGrant(int KeyId, int UserId, IReadOnlySet<string> Scopes, IReadOnlySet<int> LibraryIds, bool PathAllowed)
{
    /// <summary>The scope lets the key make this request (the feeds, or the API's reading or every route).</summary>
    public bool Allows(bool opds, bool reading) =>
        opds ? Scopes.Contains(ApiKeyScopes.Opds)
        : Scopes.Contains(ApiKeyScopes.ApiWrite) || (reading && Scopes.Contains(ApiKeyScopes.ApiRead));

    /// <summary>
    /// The account's access, narrowed to the key's libraries (none named: unchanged): a library right is then held only
    /// where one of them holds it.
    /// </summary>
    public EffectiveAccess Narrow(EffectiveAccess access)
    {
        if (LibraryIds.Count == 0)
            return access;
        var libraries = access.Libraries.Where(l => LibraryIds.Contains(l.Key)).ToDictionary(l => l.Key, l => l.Value);
        var rights = access.Rights.Where(r => RightCatalog.Find(r) is not { Scope: RightScope.Library } || libraries.Values.Any(l => l.Contains(r))).ToHashSet();
        return access with { Rights = rights, Libraries = libraries };
    }
}

/// <summary>
/// API keys (agents/docs/features/opds-auth.md, D-7.3): <c>lib_&lt;id&gt;_&lt;secret&gt;</c> with 256 bits of secret,
/// stored as the id and the SHA-256 of the secret (high entropy: no slow hash needed), compared in fixed time. A key
/// found once is kept in memory while <see cref="AccessChanges"/> doesn't move: every key change moves it, so a revoked
/// key fails at the next request. Expiry and the account's state are checked at each use.
/// </summary>
public sealed class ApiKeys(TenantDb tenantDb, AccessChanges changes, AccessService accounts, Settings.SettingsService settings, TimeProvider time)
{
    public const string Prefix = "lib_";

    /// <summary>Keys an account may hold, revoked ones included (a short list on the account page, never paged).</summary>
    public const int MaxPerUser = 50;

    /// <summary>How often a key's last use is written at most.</summary>
    public static readonly TimeSpan LastUseInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<int, (long Version, ApiKey? Key)> _keys = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastWritten = new();
    private readonly ConcurrentDictionary<int, (long Version, bool Allowed)> _pathKeys = new();

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Makes a key for <paramref name="owner"/>; its whole text is in the answer and nowhere else.</summary>
    public async Task<AccessResult<NewApiKey>> CreateAsync(SessionUser owner, ApiKeyInput input, CancellationToken cancellationToken = default)
    {
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > LibrarionDbContext.MaxNameLength)
            return AccessResult<NewApiKey>.Invalid("name", "apiKey.nameInvalid");
        var scopes = (input.Scopes ?? []).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (scopes.Count == 0 || scopes.Any(s => !ApiKeyScopes.All.Contains(s)))
            return AccessResult<NewApiKey>.Invalid("scopes", "apiKey.scopesInvalid");
        if (input.Expires is { } expires && expires.ToUniversalTime() <= Now)
            return AccessResult<NewApiKey>.Invalid("expires", "apiKey.expiresPast");

        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        if (await db.ApiKeys.CountAsync(k => k.UserId == owner.Id, cancellationToken) >= MaxPerUser)
            return AccessResult<NewApiKey>.Conflict("apiKey.tooMany");

        var libraryIds = (input.LibraryIds ?? []).Distinct().ToList();
        var libraries = await db.Libraries.AsNoTracking().Where(l => libraryIds.Contains(l.Id)).Select(l => new { l.Id, l.TenantId }).ToListAsync(cancellationToken);
        // An account's own tenant only (a server administrator: any tenant's).
        if (libraries.Count != libraryIds.Count || (!owner.IsServerAdmin && libraries.Any(l => l.TenantId != owner.TenantId)))
            return AccessResult<NewApiKey>.Invalid("libraryIds", "apiKey.libraryUnknown");

        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey
        {
            UserId = owner.Id,
            TenantId = owner.TenantId,
            Name = name,
            SecretHash = Hash(secret),
            Stamp = owner.SecurityStamp,
            Scopes = string.Join(' ', ApiKeyScopes.All.Where(scopes.Contains)),
            PathAllowed = input.PathAllowed,
            Created = Now,
            Expires = input.Expires?.ToUniversalTime(),
            Libraries = libraries.Select(l => new ApiKeyLibrary { LibraryId = l.Id, TenantId = l.TenantId }).ToList(),
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        changes.Changed();
        return AccessResult<NewApiKey>.Ok(new NewApiKey(ToInfo(key), Prefix + key.Id.ToString(CultureInfo.InvariantCulture) + "_" + secret));
    }

    /// <summary>An account's keys, newest first.</summary>
    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        var keys = await db.ApiKeys.AsNoTracking().Include(k => k.Libraries).Where(k => k.UserId == userId)
            .OrderByDescending(k => k.Id).Take(MaxPerUser).ToListAsync(cancellationToken);
        return keys.Select(ToInfo).ToList();
    }

    /// <summary>Ends a key of the account at once (kept in the list as revoked).</summary>
    public async Task<AccessResult<ApiKeyInfo>> RevokeAsync(int userId, int keyId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        var key = await db.ApiKeys.Include(k => k.Libraries).FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == userId, cancellationToken);
        if (key is null)
            return AccessResult<ApiKeyInfo>.Missing();
        key.Revoked ??= Now;
        await db.SaveChangesAsync(cancellationToken);
        changes.Changed();
        return AccessResult<ApiKeyInfo>.Ok(ToInfo(key));
    }

    /// <summary>
    /// The key a request names, when it is valid now: well formed, its secret right, not revoked or expired, its
    /// account enabled and with the stamp the key was made with. Null otherwise (the caller doesn't tell why).
    /// </summary>
    /// <param name="inPath">The key came in the catalog's address (<c>/opds/k/&lt;key&gt;</c>): it must allow that, be for
    /// reader apps, and its tenant (the server's settings for a key without one) must not have switched path keys off.</param>
    public async Task<ApiKeyGrant?> VerifyAsync(string? text, CancellationToken cancellationToken = default, bool inPath = false)
    {
        var grant = await GrantAsync(text, cancellationToken);
        if (grant is null || !inPath)
            return grant;
        return grant.PathAllowed && grant.Scopes.Contains(ApiKeyScopes.Opds) && _keys.TryGetValue(grant.KeyId, out var cached)
               && await PathKeysAllowedAsync(cached.Key!.TenantId, cancellationToken)
            ? grant
            : null;
    }

    /// <summary>The tenant's <see cref="Settings.SettingCatalog.OpdsPathKeys"/>, kept while <see cref="AccessChanges"/> holds (a saved setting moves it).</summary>
    public async Task<bool> PathKeysAllowedAsync(int? tenantId, CancellationToken cancellationToken = default)
    {
        var version = changes.Version;
        var slot = tenantId ?? 0;
        if (_pathKeys.TryGetValue(slot, out var known) && known.Version == version)
            return known.Allowed;
        var owner = tenantId is { } id ? new Settings.SettingOwner(SettingLevel.Tenant, id) : new Settings.SettingOwner(SettingLevel.Server, 0);
        var states = await settings.GetAsync(owner, tenantId, cancellationToken);
        var allowed = states?.FirstOrDefault(s => s.Key == Settings.SettingCatalog.OpdsPathKeys)?.Value is not { ValueKind: System.Text.Json.JsonValueKind.String } value
                      || value.GetString() != "off";
        _pathKeys[slot] = (version, allowed);
        return allowed;
    }

    private async Task<ApiKeyGrant?> GrantAsync(string? text, CancellationToken cancellationToken)
    {
        if (Parse(text) is not var (id, secret))
            return null;

        var version = changes.Version;
        if (!_keys.TryGetValue(id, out var cached) || cached.Version != version)
        {
            await using var db = await tenantDb.ForServerAsync(cancellationToken);
            cached = (version, await db.ApiKeys.AsNoTracking().Include(k => k.Libraries).FirstOrDefaultAsync(k => k.Id == id, cancellationToken));
            _keys[id] = cached;
        }

        if (cached.Key is not { Revoked: null } key || !PasswordHashing.SameSecret(Hash(secret), key.SecretHash)
            || key.Expires is { } expires && expires <= Now)
            return null;
        if (await accounts.GetSessionUserAsync(key.UserId, cancellationToken) is not { Disabled: false } user || user.SecurityStamp != key.Stamp)
            return null;

        return new ApiKeyGrant(key.Id, key.UserId, key.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal),
            key.Libraries.Select(l => l.LibraryId).ToHashSet(), key.PathAllowed);
    }

    /// <summary>Notes a key's use, written at most once a <see cref="LastUseInterval"/> per key (not on every request).</summary>
    public async Task UsedAsync(int keyId, string? address, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        if (_lastWritten.TryGetValue(keyId, out var last) && now - last < LastUseInterval)
            return;
        _lastWritten[keyId] = now;
        var cut = address is { Length: > 64 } ? address[..64] : address;
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        await db.ApiKeys.Where(k => k.Id == keyId).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsed, now.UtcDateTime).SetProperty(k => k.LastAddress, cut), cancellationToken);
    }

    /// <summary>The id and secret of a key's text; null when it isn't one.</summary>
    public static (int Id, string Secret)? Parse(string? text)
    {
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var rest = text.AsSpan(Prefix.Length);
        var underscore = rest.IndexOf('_');
        if (underscore <= 0 || !int.TryParse(rest[..underscore], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return null;
        var secret = rest[(underscore + 1)..].ToString();
        return secret.Length is >= 20 and <= 100 ? (id, secret) : null;
    }

    /// <summary>A text that looks like a key (the secret given at a Basic sign-in is then checked as one).</summary>
    public static bool LooksLikeKey(string? text) => Parse(text) is not null;

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ApiKeyInfo ToInfo(ApiKey key) =>
        new(key.Id, key.Name, key.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries), key.Libraries.Select(l => l.LibraryId).Order().ToList(),
            key.PathAllowed, key.Created, key.Expires, key.Revoked, key.LastUsed, key.LastAddress);
}
