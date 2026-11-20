using System.Collections.Frozen;
using System.Text.Json;

namespace Librarion.Api.EReader;

/// <summary>
/// The client's texts (<c>src/Librarion.Web/src/i18n/en.json</c>, embedded in this assembly) for pages the server
/// renders itself: the e-reader mode (D-8.10). The same keys as the app, <c>{name}</c> placeholders as its
/// <c>I18n.t</c>. English is the only language shipped; the user's UI language picks one when there are more.
/// </summary>
public sealed class ServerTexts
{
    public const string ResourceName = "Librarion.i18n.en.json";

    private readonly FrozenDictionary<string, string> _english;

    public ServerTexts()
    {
        using var stream = typeof(ServerTexts).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"{ResourceName} is not embedded in the API assembly.");
        using var json = JsonDocument.Parse(stream);
        _english = json.RootElement.EnumerateObject().ToFrozenDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
    }

    /// <summary>The text of <paramref name="key"/>; the key itself when it has none (a test keeps every key used present).</summary>
    public string this[string key] => _english.GetValueOrDefault(key, key);

    /// <summary>The text of <paramref name="key"/> with its <c>{name}</c> placeholders filled.</summary>
    public string Format(string key, params (string Name, object? Value)[] values)
    {
        var text = this[key];
        foreach (var (name, value) in values)
            text = text.Replace("{" + name + "}", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return text;
    }

    public bool Has(string key) => _english.ContainsKey(key);

    /// <summary>
    /// A field's name: a built-in one by the app's name for it (<c>fields.name.&lt;key&gt;</c>, as the client), a
    /// custom one by its own.
    /// </summary>
    public string FieldName(string key, string name, bool builtIn) => builtIn && Has("fields.name." + key) ? this["fields.name." + key] : name;
}
