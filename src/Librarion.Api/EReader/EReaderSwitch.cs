using System.Collections.Concurrent;
using System.Text.Json;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Settings;

namespace Librarion.Api.EReader;

/// <summary>
/// The tenant's <see cref="SettingCatalog.EReaderEnabled"/> (D-8.5): off, every <c>/e</c> address is not found. Before
/// anyone signs in there is no tenant: the server's value decides. Kept while <see cref="AccessChanges"/> holds (a saved
/// setting moves it), as <see cref="ApiKeys.PathKeysAllowedAsync"/>.
/// </summary>
public sealed class EReaderSwitch(SettingsService settings, AccessChanges changes)
{
    private readonly ConcurrentDictionary<int, (long Version, bool On)> _known = new();

    public async Task<bool> IsOnAsync(int? tenantId, CancellationToken cancellationToken = default)
    {
        var version = changes.Version;
        var slot = tenantId ?? 0;
        if (_known.TryGetValue(slot, out var known) && known.Version == version)
            return known.On;
        var owner = tenantId is { } id ? new SettingOwner(SettingLevel.Tenant, id) : new SettingOwner(SettingLevel.Server, 0);
        var states = await settings.GetAsync(owner, tenantId, cancellationToken);
        var on = states?.FirstOrDefault(s => s.Key == SettingCatalog.EReaderEnabled)?.Value is not { ValueKind: JsonValueKind.String } value
                 || value.GetString() != "off";
        _known[slot] = (version, on);
        return on;
    }
}
