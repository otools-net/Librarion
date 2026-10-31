using System.Text.Json;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Settings;

/// <param name="Value">The value in effect at this level.</param>
/// <param name="Inherited">The value the level above gives (what removing this level's own value brings back).</param>
/// <param name="Source">The level whose row gives <paramref name="Value"/>; null = the catalog's default.</param>
/// <param name="Locked">This level locks it for the levels below.</param>
/// <param name="LockedAbove">A level above locks it: this level can't change it.</param>
/// <param name="Editable">This level may set it (not locked above, and the key goes down to this level).</param>
public sealed record SettingState(
    string Key, string Section, SettingKind Kind, IReadOnlyList<string>? Choices, int Min, int Max,
    JsonElement Value, JsonElement Inherited, SettingLevel? Source, bool Locked, bool LockedAbove, bool Editable);

/// <param name="Values">New values by key; null removes this level's own value (the inherited one applies).</param>
/// <param name="Lock">Keys this level locks for the levels below.</param>
/// <param name="Unlock">Keys this level stops locking.</param>
public sealed record SettingChanges(Dictionary<string, JsonElement?>? Values = null, IReadOnlyList<string>? Lock = null, IReadOnlyList<string>? Unlock = null);

/// <summary>Who owns a level's settings: the server (owner 0), a tenant, a library or a user.</summary>
public sealed record SettingOwner(SettingLevel Level, int OwnerId);

/// <summary>
/// Settings by level, server → tenant → library → user (as SFE's `settings-levels.md`, all in the database): each level
/// inherits what it doesn't set, a level may lock a key for those below, and a level keeps a row only where it differs
/// from what it inherits (or locks).
/// </summary>
public sealed class SettingsService(TenantDb tenantDb, AccessChanges changes)
{
    /// <summary>The states of every key at a level; null when the owner doesn't exist (or not in <paramref name="tenantScope"/>).</summary>
    public async Task<IReadOnlyList<SettingState>?> GetAsync(SettingOwner owner, int? tenantScope, CancellationToken cancellationToken = default)
    {
        if (await ChainAsync(owner, tenantScope, cancellationToken) is not { } chain)
            return null;
        var rows = await RowsAsync(chain.Owners, cancellationToken);
        return SettingCatalog.All.Select(definition => State(definition, chain.Owners, rows)).ToList();
    }

    /// <summary>Applies the changes all or nothing; the errors by key when some don't fit.</summary>
    public async Task<AccessResult<IReadOnlyList<SettingState>>> SaveAsync(SettingOwner owner, int? tenantScope, SettingChanges input, CancellationToken cancellationToken = default)
    {
        if (await ChainAsync(owner, tenantScope, cancellationToken) is not { } chain)
            return AccessResult<IReadOnlyList<SettingState>>.Missing();
        var rows = await RowsAsync(chain.Owners, cancellationToken);
        var errors = new Dictionary<string, string[]>();
        var values = input.Values ?? [];
        var locks = input.Lock ?? [];
        var unlocks = input.Unlock ?? [];

        foreach (var key in values.Keys.Concat(locks).Concat(unlocks).Distinct())
        {
            if (SettingCatalog.Find(key) is not { } definition)
                errors[key] = ["setting.unknown"];
            else if (!State(definition, chain.Owners, rows).Editable)
                errors[key] = ["setting.notEditable"];
            else if (values.TryGetValue(key, out var value) && value is { } given && definition.Problem(given) is { } problem)
                errors[key] = [problem];
            else if (owner.Level == SettingLevel.User && (locks.Contains(key) || unlocks.Contains(key)))
                errors[key] = ["setting.notLockable"];
        }
        if (errors.Count > 0)
            return new AccessResult<IReadOnlyList<SettingState>>(AccessStatus.Invalid, Errors: errors);

        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        foreach (var key in values.Keys.Concat(locks).Concat(unlocks).Distinct())
        {
            var definition = SettingCatalog.Find(key)!;
            var state = State(definition, chain.Owners, rows);
            var row = await db.Settings.FirstOrDefaultAsync(s => s.Level == owner.Level && s.OwnerId == owner.OwnerId && s.Key == key, cancellationToken);
            var value = values.TryGetValue(key, out var newValue) ? newValue : row?.Value is { } stored ? JsonDocument.Parse(stored).RootElement : null;
            var locked = locks.Contains(key) || (row?.Locked == true && !unlocks.Contains(key));
            var own = value is { } v && !JsonElement.DeepEquals(v, state.Inherited) ? v.GetRawText() : null;

            if (own is null && !locked)
            {
                if (row is not null)
                    db.Settings.Remove(row);
                continue;
            }

            if (row is null)
            {
                row = new Setting { Level = owner.Level, OwnerId = owner.OwnerId, TenantId = chain.TenantId, Key = key };
                db.Settings.Add(row);
            }
            row.Value = own;
            row.Locked = locked;
        }

        await db.SaveChangesAsync(cancellationToken);
        changes.Changed();
        return AccessResult<IReadOnlyList<SettingState>>.Ok((await GetAsync(owner, tenantScope, cancellationToken))!);
    }

    /// <summary>The values in effect for a user (its preferences over its tenant's and the server's).</summary>
    public async Task<IReadOnlyDictionary<string, JsonElement>> EffectiveForUserAsync(int userId, CancellationToken cancellationToken = default) =>
        (await GetAsync(new SettingOwner(SettingLevel.User, userId), null, cancellationToken) ?? []).ToDictionary(s => s.Key, s => s.Value);

    private static SettingState State(SettingDefinition definition, IReadOnlyList<SettingOwner> owners, IReadOnlyList<Setting> rows)
    {
        var value = definition.Default;
        var inherited = definition.Default;
        SettingLevel? source = null;
        var lockedAbove = false;
        var locked = false;
        for (var i = 0; i < owners.Count; i++)
        {
            var row = rows.FirstOrDefault(r => r.Level == owners[i].Level && r.OwnerId == owners[i].OwnerId && r.Key == definition.Key);
            var last = i == owners.Count - 1;
            if (last)
                inherited = value;
            if (row is null)
                continue;
            // Below a lock, the levels' own values don't count.
            if (row.Value is { } json && !lockedAbove)
            {
                value = JsonDocument.Parse(json).RootElement.Clone();
                source = row.Level;
            }
            if (last)
                locked = row.Locked;
            else if (row.Locked)
                lockedAbove = true;
        }

        var level = owners[^1].Level;
        return new SettingState(definition.Key, definition.Section, definition.Kind, definition.Choices, definition.Min, definition.Max,
            value, inherited, source, locked, lockedAbove, !lockedAbove && level <= definition.LowestLevel);
    }

    private async Task<List<Setting>> RowsAsync(IReadOnlyList<SettingOwner> owners, CancellationToken cancellationToken)
    {
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        var rows = new List<Setting>();
        foreach (var owner in owners)
            rows.AddRange(await db.Settings.AsNoTracking().Where(s => s.Level == owner.Level && s.OwnerId == owner.OwnerId).ToListAsync(cancellationToken));
        return rows;
    }

    /// <summary>The levels from the server down to <paramref name="owner"/>, and the tenant its rows belong to.</summary>
    private async Task<(IReadOnlyList<SettingOwner> Owners, int? TenantId)?> ChainAsync(SettingOwner owner, int? tenantScope, CancellationToken cancellationToken)
    {
        var server = new SettingOwner(SettingLevel.Server, 0);
        if (owner.Level == SettingLevel.Server)
            return tenantScope is null ? ([server], null) : null;

        await using var db = tenantScope is { } scope ? await tenantDb.ForTenantAsync(scope, cancellationToken) : await tenantDb.ForServerAsync(cancellationToken);
        switch (owner.Level)
        {
            case SettingLevel.Tenant:
                return await db.Tenants.AnyAsync(t => t.Id == owner.OwnerId, cancellationToken) ? ([server, owner], owner.OwnerId) : null;
            case SettingLevel.Library:
                var library = await db.Libraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == owner.OwnerId, cancellationToken);
                return library is null ? null : ([server, new SettingOwner(SettingLevel.Tenant, library.TenantId), owner], library.TenantId);
            default:
                var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == owner.OwnerId, cancellationToken);
                if (user is null)
                    return null;
                return user.TenantId is { } tenantId ? ([server, new SettingOwner(SettingLevel.Tenant, tenantId), owner], tenantId) : ([server, owner], null);
        }
    }
}
