using System.Text.Json;
using Librarion.Core.Access;
using Librarion.Core.Data;

namespace Librarion.Core.Settings;

public enum SettingKind
{
    Choice,
    Integer,
}

/// <param name="Default">The value when no level sets one, as JSON.</param>
/// <param name="LowestLevel">The narrowest level that may set it (a server-only key: <see cref="SettingLevel.Server"/>).</param>
/// <param name="Choices">The values a <see cref="SettingKind.Choice"/> takes.</param>
/// <param name="Section">Heading on the settings page (its text: <c>settingSection.&lt;section&gt;</c>).</param>
public sealed record SettingDefinition(
    string Key, SettingKind Kind, JsonElement Default, SettingLevel LowestLevel, string Section,
    IReadOnlyList<string>? Choices = null, int Min = 0, int Max = 0)
{
    /// <summary>Null when <paramref name="value"/> fits; else a problem code.</summary>
    public string? Problem(JsonElement value) => Kind switch
    {
        SettingKind.Choice when value.ValueKind == JsonValueKind.String && Choices!.Contains(value.GetString()!) => null,
        SettingKind.Integer when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= Min && number <= Max => null,
        _ => "setting.invalid",
    };
}

/// <summary>A key of the catalog as the client sees it (the administration's search): no default, no value.</summary>
public sealed record SettingInfo(
    string Key, string Section, SettingKind Kind, IReadOnlyList<string>? Choices, int Min, int Max, SettingLevel LowestLevel);

/// <summary>
/// Every setting the levels can hold (agents/docs/features/settings.md). The right editing a level comes from the level
/// (<see cref="RightFor"/>); the user level is one's own preferences.
/// </summary>
public static class SettingCatalog
{
    public const string Theme = "ui.theme";
    public const string Density = "ui.density";
    public const string Language = "ui.language";
    public const string PageSize = "ui.pageSize";

    /// <summary>Days a missing book waits before it is offered for removal (agents/docs/features/missing-books.md); never removed without a user.</summary>
    public const string KeepMissingDays = "library.keepMissingDays";

    /// <summary>A scanned file with the same bytes as one of the library becomes another file of that book (D19, the user's Q4: on by default).</summary>
    public const string MergeSameFiles = "import.mergeSameFiles";

    /// <summary>
    /// API keys may be used in the catalog's address (<c>/opds/k/&lt;key&gt;</c>, D-7.5): each key still opts in; off, no
    /// key of the tenant works so (the user's Q9).
    /// </summary>
    public const string OpdsPathKeys = "opds.pathKeys";

    /// <summary>
    /// Shelves an account may hold in a tenant (agents/docs/features/shelves.md; the user's answer, 2026-11-10): set by
    /// the server or the tenant, never by the account; at most 1,000, as a list of shelves is never paged.
    /// </summary>
    public const string MaxShelves = "shelves.maxPerAccount";

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new(Theme, SettingKind.Choice, Json("\"system\""), SettingLevel.User, "interface", Choices: ["system", "light", "dark"]),
        new(Density, SettingKind.Choice, Json("\"compact\""), SettingLevel.User, "interface", Choices: ["compact", "comfortable"]),
        new(Language, SettingKind.Choice, Json("\"browser\""), SettingLevel.User, "interface", Choices: ["browser", "en"]),
        new(PageSize, SettingKind.Integer, Json("50"), SettingLevel.User, "interface", Min: 20, Max: 200),
        new(KeepMissingDays, SettingKind.Integer, Json("30"), SettingLevel.Library, "libraries", Min: 0, Max: 3650),
        new(MergeSameFiles, SettingKind.Choice, Json("\"on\""), SettingLevel.Tenant, "libraries", Choices: ["on", "off"]),
        new(OpdsPathKeys, SettingKind.Choice, Json("\"on\""), SettingLevel.Tenant, "readerApps", Choices: ["on", "off"]),
        new(MaxShelves, SettingKind.Integer, Json("100"), SettingLevel.Tenant, "shelves", Min: 1, Max: 1000),
    ];

    private static readonly Dictionary<string, SettingDefinition> ByKey = All.ToDictionary(s => s.Key, StringComparer.Ordinal);

    public static SettingDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>The right that edits the settings of a level.</summary>
    public static string RightFor(SettingLevel level) => level switch
    {
        SettingLevel.Server => Rights.ServerSettings,
        SettingLevel.Tenant => Rights.TenantSettings,
        SettingLevel.Library => Rights.LibrarySettings,
        _ => Rights.OwnPreferences,
    };

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
