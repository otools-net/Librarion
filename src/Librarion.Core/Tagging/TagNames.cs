using System.Globalization;
using System.Text;

namespace Librarion.Core.Tagging;

/// <summary>How tag names compare (agents/docs/features/tags.md#imported-tags): trimmed, NFKC, case-folded, single spaces.</summary>
public static class TagNames
{
    public static string Normalize(string name)
    {
        var text = name.Normalize(NormalizationForm.FormKC);
        var result = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = result.Length > 0;
                continue;
            }

            if (space)
                result.Append(' ');
            space = false;
            result.Append(char.ToLowerInvariant(c));
        }

        return result.ToString();
    }

    /// <summary>"Stephen King" → "King, Stephen": the last word first. Names with a comma are kept as they are.</summary>
    public static string PersonSortName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Contains(',', StringComparison.Ordinal))
            return trimmed;
        var space = trimmed.LastIndexOf(' ');
        return space <= 0 ? trimmed : string.Create(CultureInfo.InvariantCulture, $"{trimmed[(space + 1)..]}, {trimmed[..space]}");
    }
}
