using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Librarion.Core.Access;

/// <param name="Before">Only events older than this id (the next page's cursor: the last id of the page before).</param>
/// <param name="TenantId">Only this tenant's; null with <paramref name="ServerWide"/> = every event, else the server's own.</param>
public sealed record SecurityEventQuery(int? TenantId, bool ServerWide = false, long? Before = null, SecurityEventKind? Kind = null, string? UserName = null, int Limit = 100);

public sealed record SecurityEventInfo(long Id, DateTime Time, SecurityEventKind Kind, int? TenantId, int? UserId, string? UserName, string? Address, string? Detail);

/// <summary>
/// The security log (agents/docs/features/security-log.md): what an administrator may need to know about access, kept
/// in the database for <see cref="SecurityOptions.EventDays"/>. Never a password, key or code: callers pass names and
/// what changed, nothing else.
/// </summary>
public sealed class SecurityLog(TenantDb tenantDb, TimeProvider time, ILogger<SecurityLog> logger)
{
    public async Task WriteAsync(SecurityEventKind kind, int? tenantId, int? userId, string? userName, string? address, string? detail = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        db.SecurityEvents.Add(new SecurityEvent
        {
            Time = time.GetUtcNow().UtcDateTime,
            Kind = kind,
            TenantId = tenantId,
            UserId = userId,
            UserName = Cut(userName),
            Address = address is { Length: > 64 } ? address[..64] : address,
            Detail = Cut(detail),
        });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Security: {Kind} {User} {Detail}", kind, userName, detail);
    }

    /// <summary>The newest events first, by id (keyset: <see cref="SecurityEventQuery.Before"/>).</summary>
    public async Task<IReadOnlyList<SecurityEventInfo>> ListAsync(SecurityEventQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = query.TenantId is { } tenantId ? await tenantDb.ForTenantAsync(tenantId, cancellationToken) : await tenantDb.ForServerAsync(cancellationToken);
        var events = db.SecurityEvents.AsNoTracking();
        if (query.TenantId is null && !query.ServerWide)
            events = events.Where(e => e.TenantId == null);
        if (query.Before is { } before)
            events = events.Where(e => e.Id < before);
        if (query.Kind is { } kind)
            events = events.Where(e => e.Kind == kind);
        if (!string.IsNullOrWhiteSpace(query.UserName))
        {
            var name = query.UserName.Trim();
            events = events.Where(e => e.UserName != null && e.UserName.StartsWith(name));
        }

        return await events.OrderByDescending(e => e.Id).Take(Math.Clamp(query.Limit, 1, 500))
            .Select(e => new SecurityEventInfo(e.Id, e.Time, e.Kind, e.TenantId, e.UserId, e.UserName, e.Address, e.Detail))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Deletes the events older than the retention, in batches; returns how many went.</summary>
    public async Task<int> PurgeAsync(int days, CancellationToken cancellationToken = default)
    {
        var limit = time.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, days));
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        var total = 0;
        while (true)
        {
            var ids = await db.SecurityEvents.Where(e => e.Time < limit).OrderBy(e => e.Id).Select(e => e.Id).Take(1000).ToListAsync(cancellationToken);
            if (ids.Count == 0)
                return total;
            total += await db.SecurityEvents.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }

    private static string? Cut(string? text) => text is { Length: > SecurityEvent.MaxTextLength } ? text[..SecurityEvent.MaxTextLength] : text;
}

/// <summary>Purges the security log at start and twice a day.</summary>
public sealed class SecurityLogCleanup(SecurityLog log, IOptionsMonitor<SecurityOptions> security, ILogger<SecurityLogCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(12));
        do
        {
            try
            {
                await log.PurgeAsync(security.CurrentValue.EventDays, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The security log could not be purged; trying again later");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
