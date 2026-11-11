namespace Librarion.Core.Data;

/// <summary>
/// An account's API key (agents/docs/features/opds-auth.md, D-7.3): <c>lib_&lt;Id&gt;_&lt;secret&gt;</c>, kept as the id and
/// the SHA-256 of the secret only, shown once. Its scopes and libraries narrow the account's rights, never widen them;
/// it ends when revoked, expired, or when the account's security stamp is no longer <see cref="Stamp"/>.
/// </summary>
public sealed class ApiKey
{
    public const int MaxScopesLength = 100;

    public int Id { get; set; }

    public int UserId { get; set; }

    /// <summary>The account's tenant; null for a server administrator's key.</summary>
    public int? TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 of the secret, hex.</summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>The account's security stamp when the key was made: a new password or a disabled account ends the key.</summary>
    public string Stamp { get; set; } = string.Empty;

    /// <summary>Space-separated: <c>opds</c>, <c>api:read</c>, <c>api:write</c> (<see cref="Access.ApiKeyScopes"/>).</summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>May be put in an address (<c>/opds/k/&lt;key&gt;/…</c>) by clients that send no credentials.</summary>
    public bool PathAllowed { get; set; }

    public DateTime Created { get; set; }
    public DateTime? Expires { get; set; }
    public DateTime? Revoked { get; set; }
    public DateTime? LastUsed { get; set; }
    public string? LastAddress { get; set; }

    /// <summary>The libraries it may reach; none = every library the account may.</summary>
    public List<ApiKeyLibrary> Libraries { get; set; } = [];
}

/// <summary>A library an <see cref="ApiKey"/> is limited to.</summary>
public sealed class ApiKeyLibrary
{
    public int ApiKeyId { get; set; }
    public ApiKey? ApiKey { get; set; }
    public int LibraryId { get; set; }

    /// <summary>The library's tenant (the filter's column).</summary>
    public int TenantId { get; set; }
}
