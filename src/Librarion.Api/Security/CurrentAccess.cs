using Librarion.Core.Access;

namespace Librarion.Api.Security;

/// <param name="TenantId">The tenant the request works in; null for nobody signed in, or a server administrator before any tenant exists.</param>
/// <param name="User">The signed-in account; null for a visitor without one.</param>
public sealed record AccessState(int? TenantId, SessionUser? User, EffectiveAccess Access)
{
    public bool Has(string right) => Access.Has(right);
}

/// <summary>
/// Who makes the request and in which tenant, worked out once per request: an account of a tenant works in it; a
/// server administrator in the tenant of its session (or the first one). Visitors without an account have no rights
/// (no guests before OPDS decides on them).
/// </summary>
public sealed class CurrentAccess(IHttpContextAccessor accessor, AccessService accounts, RightsEvaluator rights)
{
    private Task<AccessState>? _state;

    public Task<AccessState> GetAsync() => _state ??= ResolveAsync();

    /// <summary>Forgets the state read so far, after the request signed in or out.</summary>
    public void Reset() => _state = null;

    private async Task<AccessState> ResolveAsync()
    {
        var context = accessor.HttpContext ?? throw new InvalidOperationException("No request.");
        var cancellationToken = context.RequestAborted;
        var user = context.User.UserId() is { } userId ? await accounts.GetSessionUserAsync(userId, cancellationToken) : null;
        if (user is null || user.Disabled)
            return new AccessState(null, null, EffectiveAccess.None);

        var tenantId = user.TenantId;
        if (user.IsServerAdmin)
        {
            var tenants = await accounts.ListTenantsAsync(cancellationToken);
            tenantId = context.User.TenantId() is { } chosen && tenants.Any(t => t.Id == chosen) ? chosen : tenants.FirstOrDefault()?.Id;
        }

        return new AccessState(tenantId, user, await rights.GetAsync(new AccessSubject(tenantId, user.Id), cancellationToken));
    }
}
