using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace Librarion.Core.Catalog;

/// <summary>
/// Where a page ends: the last row's sort value and id, opaque to clients (base64url of a small JSON). The next page
/// starts after it by index, never by offset (agents/docs/development/scale.md).
/// </summary>
public sealed record KeysetCursor(string? Text, long? Number, int Id, double? Real = null)
{
    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this));

    /// <summary>Null for no cursor or one that doesn't read (a client's garbage starts at the beginning).</summary>
    public static KeysetCursor? Decode(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        try
        {
            return JsonSerializer.Deserialize<KeysetCursor>(Base64Url.DecodeFromChars(text));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    public override string ToString() => Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(this));
}

/// <summary>A page of a list and the cursor of the next one (null at the end).</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? Next);
