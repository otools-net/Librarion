using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Data;

/// <summary>
/// Contexts that say whose rows they may see. Code working for a tenant takes <see cref="ForTenantAsync"/>: its
/// query filters hide every other tenant's rows, so a forgotten <c>Where</c> can't leak them. Server-wide code
/// (sign-in, tenants, server administrators) takes <see cref="ForServerAsync"/> and says so by its name.
/// </summary>
public sealed class TenantDb(IDbContextFactory<LibrarionDbContext> factory)
{
    public async Task<LibrarionDbContext> ForTenantAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken);
        db.TenantScope = tenantId;
        return db;
    }

    public Task<LibrarionDbContext> ForServerAsync(CancellationToken cancellationToken = default) => factory.CreateDbContextAsync(cancellationToken);
}
