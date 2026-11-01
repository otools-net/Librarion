using System.Collections.Concurrent;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Librarion.Core.Access;

/// <summary>Who asks, in which tenant (null: a server administrator before any tenant exists).</summary>
public sealed record AccessSubject(int? TenantId, int UserId);

/// <summary>What a subject may do in a tenant.</summary>
/// <param name="Rights">Its server and tenant rights, and the library rights it has in at least one library.</param>
/// <param name="Libraries">The library rights it has in each library it reaches.</param>
public sealed record EffectiveAccess(IReadOnlySet<string> Rights, IReadOnlyDictionary<int, IReadOnlySet<string>> Libraries, bool IsServerAdmin)
{
    public static readonly EffectiveAccess None = new(new HashSet<string>(), new Dictionary<int, IReadOnlySet<string>>(), false);

    /// <summary>Has the right (a library right: in some library), what <c>RequireRight</c> checks.</summary>
    public bool Has(string right) => Rights.Contains(right);

    /// <summary>Has the right in a library (a tenant or server right: anywhere it has it).</summary>
    public bool HasIn(string right, int libraryId) =>
        RightCatalog.Find(right) is { Scope: RightScope.Library }
            ? Libraries.TryGetValue(libraryId, out var rights) && rights.Contains(right)
            : Rights.Contains(right);

    public IEnumerable<int> LibrariesWith(string right) => Libraries.Where(l => l.Value.Contains(right)).Select(l => l.Key);
}

/// <summary>
/// The rights of an account in a tenant (as SFE's): the union of its groups' rights, library rights only in the
/// groups' libraries, less those the tenant disables and those the server disables (<see cref="RightOptions.Disabled"/>,
/// applied at each read). Server administrators have every right everywhere but the disabled ones. Cached until
/// accounts, groups, tenants or libraries change (<see cref="AccessChanges"/>).
/// </summary>
public sealed class RightsEvaluator(TenantDb tenantDb, AccessChanges changes, AccessService accounts, IOptionsMonitor<RightOptions> rightOptions)
{
    private readonly ConcurrentDictionary<AccessSubject, (long Version, EffectiveAccess Access)> _cache = new();

    public async Task<EffectiveAccess> GetAsync(AccessSubject subject, CancellationToken cancellationToken = default)
    {
        var version = changes.Version;
        if (!_cache.TryGetValue(subject, out var cached) || cached.Version != version)
        {
            cached = (version, await ComputeAsync(subject, cancellationToken));
            _cache[subject] = cached;
        }

        return Filter(cached.Access);
    }

    /// <summary>The server's switches apply at once, also to cached entries (they come from the configuration).</summary>
    private EffectiveAccess Filter(EffectiveAccess access)
    {
        var disabled = rightOptions.CurrentValue.Disabled;
        if (disabled.Count == 0)
            return access;
        return new EffectiveAccess(
            access.Rights.Where(r => !RightCatalog.MatchesAny(disabled, r)).ToHashSet(),
            access.Libraries.ToDictionary(l => l.Key, IReadOnlySet<string> (l) => l.Value.Where(r => !RightCatalog.MatchesAny(disabled, r)).ToHashSet()),
            access.IsServerAdmin);
    }

    private async Task<EffectiveAccess> ComputeAsync(AccessSubject subject, CancellationToken cancellationToken)
    {
        var user = await accounts.GetSessionUserAsync(subject.UserId, cancellationToken);
        if (user is null || user.Disabled)
            return EffectiveAccess.None;

        if (subject.TenantId is not { } tenantId)
        {
            // A server administrator before any tenant exists: everything but the rights of libraries it can't have yet.
            return user.IsServerAdmin
                ? new EffectiveAccess(RightCatalog.All.Where(r => r.Scope != RightScope.Library).Select(r => r.Id).ToHashSet(), new Dictionary<int, IReadOnlySet<string>>(), true)
                : EffectiveAccess.None;
        }

        if (!user.IsServerAdmin && user.TenantId != tenantId)
            return EffectiveAccess.None;

        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().Include(t => t.DisabledRights).FirstOrDefaultAsync(cancellationToken);
        if (tenant is null || tenant.Disabled && !user.IsServerAdmin)
            return EffectiveAccess.None;
        var tenantDisabled = tenant.DisabledRights.Select(r => r.Right).ToHashSet(StringComparer.Ordinal);
        var libraryIds = await db.Libraries.AsNoTracking().Select(l => l.Id).ToListAsync(cancellationToken);

        if (user.IsServerAdmin)
        {
            var inLibrary = RightCatalog.All.Where(r => r.Scope == RightScope.Library && !tenantDisabled.Contains(r.Id)).Select(r => r.Id).ToHashSet();
            var all = RightCatalog.All
                .Where(r => r.Scope == RightScope.Server || r.Scope == RightScope.Tenant && !tenantDisabled.Contains(r.Id))
                .Select(r => r.Id)
                .Concat(libraryIds.Count > 0 ? inLibrary : [])
                .ToHashSet();
            return new EffectiveAccess(all, libraryIds.ToDictionary(id => id, IReadOnlySet<string> (_) => inLibrary), true);
        }

        var groups = await db.Groups.AsNoTracking()
            .Where(g => g.Members.Any(m => m.UserId == user.Id))
            .Select(g => new { g.AllLibraries, Rights = g.Rights.Select(r => r.Right).ToList(), Libraries = g.Libraries.Select(l => l.LibraryId).ToList() })
            .ToListAsync(cancellationToken);

        var rights = new HashSet<string>(StringComparer.Ordinal);
        var perLibrary = libraryIds.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal));
        foreach (var group in groups)
        {
            foreach (var right in group.Rights.Where(r => RightCatalog.IsGrantable(r) && !tenantDisabled.Contains(r)))
            {
                if (RightCatalog.Find(right)!.Scope != RightScope.Library)
                {
                    rights.Add(right);
                    continue;
                }

                foreach (var libraryId in group.AllLibraries ? libraryIds : group.Libraries.Where(perLibrary.ContainsKey))
                {
                    perLibrary[libraryId].Add(right);
                    rights.Add(right);
                }
            }
        }

        return new EffectiveAccess(rights, perLibrary.Where(l => l.Value.Count > 0).ToDictionary(l => l.Key, IReadOnlySet<string> (l) => l.Value), false);
    }
}
