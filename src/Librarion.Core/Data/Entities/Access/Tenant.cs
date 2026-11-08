namespace Librarion.Core.Data;

/// <summary>
/// An organisation (a family, a club, a company): its libraries, its accounts and groups, its tag vocabulary and its
/// settings. Every row it owns carries its <c>TenantId</c>, and a context made for it sees only those
/// (<see cref="TenantDb"/>).
/// </summary>
public sealed class Tenant
{
    public int Id { get; set; }

    /// <summary>Short name, unique: used in addresses and folder names. Fixed once created.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Nobody of the tenant signs in; server administrators still reach it.</summary>
    public bool Disabled { get; set; }

    public DateTime Created { get; set; }

    /// <summary>
    /// The generated cover templates turned off (bit <c>i</c> = template <c>i</c>, agents/docs/features/book-display.md):
    /// new books get one of the others. At least one stays on.
    /// </summary>
    public int CoverTemplatesOff { get; set; }

    /// <summary>
    /// UTC, when the duplicate groups were last built whole (agents/docs/features/merging.md#stored-duplicate-groups);
    /// null until then: the end of the next scan or import builds them.
    /// </summary>
    public DateTime? DuplicatesBuilt { get; set; }

    public List<Library> Libraries { get; set; } = [];
    public List<UserGroup> Groups { get; set; } = [];
    public List<TenantDisabledRight> DisabledRights { get; set; } = [];
}
