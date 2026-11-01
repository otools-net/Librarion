using Librarion.Core.Data;

namespace Librarion.Core.Access;

/// <summary>
/// Every right of the server. Every route names one or more (the Api's <c>RequireRight</c>); a test fails on a route
/// that names none, and on a right the client has no text for (<c>right.&lt;id&gt;</c> in <c>en.json</c>).
/// </summary>
public static class Rights
{
    public const string Browse = "library.browse";
    public const string Download = "library.download";
    public const string Scan = "library.scan";
    public const string LibrarySettings = "library.settings";
    public const string ManageUsers = "tenant.users";
    public const string ManageLibraries = "tenant.libraries";
    public const string TenantSettings = "tenant.settings";
    public const string TenantSecurityLog = "tenant.securityLog";
    public const string OwnPreferences = "account.preferences";
    public const string OwnPassword = "account.password";
    public const string SystemInfo = "system.info";
    public const string ApiDocs = "system.apiDocs";
    public const string ManageTenants = "server.tenants";
    public const string ManageAllUsers = "server.users";
    public const string ServerSettings = "server.settings";
    public const string ServerSecurityLog = "server.securityLog";
}

/// <summary>
/// Library rights apply in the libraries of the groups granting them; tenant rights in the whole tenant; server rights
/// belong to server administrators only.
/// </summary>
public enum RightScope
{
    Library,
    Tenant,
    Server,
}

/// <summary>What a right does: only looks (<see cref="Read"/>), or changes something through one of its routes.</summary>
public enum RightKind
{
    Read,
    Write,
}

/// <param name="DefaultGroups">The built-in groups granted it when a tenant is created; changed by hand afterwards.</param>
/// <param name="Area">Heading the right is listed under on the group page (its text: <c>rightArea.&lt;area&gt;</c>).</param>
/// <param name="Kind">Write when any route it opens changes something (books, tags, settings, accounts, own data).</param>
public sealed record RightDefinition(string Id, RightScope Scope, RightKind Kind, IReadOnlyList<BuiltInGroup> DefaultGroups, string Area);

public static class RightCatalog
{
    private static readonly BuiltInGroup[] Members = [BuiltInGroup.Administrators, BuiltInGroup.Users];
    private static readonly BuiltInGroup[] Administrators = [BuiltInGroup.Administrators];

    private const RightKind R = RightKind.Read;
    private const RightKind W = RightKind.Write;

    public static IReadOnlyList<RightDefinition> All { get; } =
    [
        new(Rights.Browse, RightScope.Library, R, Members, "library"),
        new(Rights.Download, RightScope.Library, R, Members, "library"),
        new(Rights.Scan, RightScope.Library, W, Administrators, "library"),
        new(Rights.LibrarySettings, RightScope.Library, W, Administrators, "library"),
        new(Rights.ManageUsers, RightScope.Tenant, W, Administrators, "tenant"),
        new(Rights.ManageLibraries, RightScope.Tenant, W, Administrators, "tenant"),
        new(Rights.TenantSettings, RightScope.Tenant, W, Administrators, "tenant"),
        new(Rights.TenantSecurityLog, RightScope.Tenant, R, Administrators, "tenant"),
        new(Rights.OwnPreferences, RightScope.Tenant, W, Members, "account"),
        new(Rights.OwnPassword, RightScope.Tenant, W, Members, "account"),
        new(Rights.SystemInfo, RightScope.Tenant, R, Members, "system"),
        new(Rights.ApiDocs, RightScope.Tenant, R, Administrators, "system"),
        new(Rights.ManageTenants, RightScope.Server, W, [], "server"),
        new(Rights.ManageAllUsers, RightScope.Server, W, [], "server"),
        new(Rights.ServerSettings, RightScope.Server, W, [], "server"),
        new(Rights.ServerSecurityLog, RightScope.Server, R, [], "server"),
    ];

    /// <summary>Selector naming every right that changes something (<see cref="RightKind.Write"/>).</summary>
    public const string WriteSelector = "kind:write";

    private static readonly Dictionary<string, RightDefinition> ById = All.ToDictionary(r => r.Id, StringComparer.Ordinal);

    public static RightDefinition? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>A right a tenant's group may be granted (library or tenant scope).</summary>
    public static bool IsGrantable(string id) => Find(id) is { Scope: not RightScope.Server };

    /// <summary>
    /// <paramref name="pattern"/> names <paramref name="right"/>: equal (ignoring case), a prefix ending with <c>*</c>
    /// (<c>tenant.*</c>; <c>*</c> alone names every right), or <see cref="WriteSelector"/>.
    /// </summary>
    public static bool Matches(string pattern, string right)
    {
        var trimmed = pattern.Trim();
        if (string.Equals(trimmed, WriteSelector, StringComparison.OrdinalIgnoreCase))
            return Find(right) is { Kind: RightKind.Write };
        return trimmed.EndsWith('*')
            ? right.StartsWith(trimmed[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(trimmed, right, StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesAny(IEnumerable<string> patterns, string right) => patterns.Any(p => Matches(p, right));
}
