using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Settings;

namespace Librarion.Api.Endpoints;

/// <summary>
/// Settings by level (agents/docs/features/settings.md): the server's (<see cref="Rights.ServerSettings"/>), a tenant's
/// (<see cref="Rights.TenantSettings"/>; another tenant's: <see cref="Rights.ManageTenants"/>), a library's
/// (<see cref="Rights.LibrarySettings"/> in it), and one's own preferences (<see cref="Rights.OwnPreferences"/>).
/// </summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder api)
    {
        var settings = api.MapGroup("/settings").WithTags("Settings")
            .RequireRight(Rights.ServerSettings, Rights.TenantSettings, Rights.LibrarySettings, Rights.ManageTenants);

        settings.MapGet("/", async (string level, int? owner, HttpContext context, CurrentAccess current, SettingsService service) =>
                Enum.TryParse<SettingLevel>(level, ignoreCase: true, out var parsed) && Target(await current.GetAsync(), parsed, owner) is { } target
                    && await service.GetAsync(target.Owner, target.Scope, context.RequestAborted) is { } states
                    ? Results.Ok(states)
                    : Results.NotFound())
            .WithName("GetSettings")
            .WithSummary("The settings of a level (`server`, `tenant`, `library`) with their values, inherited values and locks.")
            .WithDescription("`owner`: the tenant (the request's own by default) or the library.");

        settings.MapPatch("/", async (string level, int? owner, SettingChanges changes, HttpContext context, CurrentAccess current, SettingsService service, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                if (!Enum.TryParse<SettingLevel>(level, ignoreCase: true, out var parsed) || Target(state, parsed, owner) is not { } target)
                    return Results.NotFound();
                var result = await service.SaveAsync(target.Owner, target.Scope, changes, context.RequestAborted);
                if (result.Status == AccessStatus.Ok)
                    await log.LogAsync(context, state, SecurityEventKind.SettingsChanged, parsed == SettingLevel.Server ? null : state.TenantId, Describe(parsed, target.Owner, changes));
                return ApiResults.From(result);
            })
            .WithName("ChangeSettings")
            .WithSummary("Changes the keys sent at a level, all or nothing: `values` by key (null = inherit), `lock`, `unlock`.");

        var preferences = api.MapGroup("/me/preferences").WithTags("Settings").RequireRight(Rights.OwnPreferences);

        preferences.MapGet("/", async (HttpContext context, CurrentAccess current, SettingsService service) =>
                await service.GetAsync(new SettingOwner(SettingLevel.User, (await current.GetAsync()).User!.Id), null, context.RequestAborted) is { } states
                    ? Results.Ok(states)
                    : Results.NotFound())
            .WithName("GetPreferences")
            .WithSummary("One's own preferences, over the tenant's and the server's settings.");

        preferences.MapPatch("/", async (SettingChanges changes, HttpContext context, CurrentAccess current, SettingsService service) =>
                ApiResults.From(await service.SaveAsync(new SettingOwner(SettingLevel.User, (await current.GetAsync()).User!.Id), null, changes, context.RequestAborted)))
            .WithName("ChangePreferences")
            .WithSummary("Changes one's own preferences (`values` by key; null = inherit).");
    }

    /// <summary>The owner of the level asked for and the tenant scope to check it in; null when not allowed.</summary>
    private static (SettingOwner Owner, int? Scope)? Target(AccessState state, SettingLevel level, int? owner)
    {
        switch (level)
        {
            case SettingLevel.Server:
                return state.Has(Rights.ServerSettings) ? (new SettingOwner(level, 0), null) : null;
            case SettingLevel.Tenant:
                return ApiResults.TargetTenant(state, owner, Rights.TenantSettings, Rights.ManageTenants) is { } tenantId
                    ? (new SettingOwner(level, tenantId), state.Has(Rights.ManageTenants) ? null : tenantId)
                    : null;
            case SettingLevel.Library when owner is { } libraryId:
                if (state.Has(Rights.ManageTenants))
                    return (new SettingOwner(level, libraryId), null);
                return state.TenantId is { } own && state.Access.HasIn(Rights.LibrarySettings, libraryId) ? (new SettingOwner(level, libraryId), own) : null;
            default:
                return null;
        }
    }

    private static string Describe(SettingLevel level, SettingOwner owner, SettingChanges changes) =>
        $"{level} {owner.OwnerId}: {string.Join(", ", (changes.Values?.Keys.AsEnumerable() ?? []).Concat(changes.Lock ?? []).Concat(changes.Unlock ?? []).Distinct())}";
}
