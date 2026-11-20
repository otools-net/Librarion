using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Net.Http.Headers;

namespace Librarion.Api.Endpoints;

/// <summary>
/// A download's <c>Content-Disposition</c> (D-8.8): the name in UTF-8 (<c>filename*</c>, RFC 5987) for browsers that
/// read it, and an ASCII <c>filename</c> for those that don't (old e-ink browsers mangle UTF-8 names). ASP.NET's own
/// fallback turns every other letter into <c>_</c> ("Война и мир.fb2" → "_____ _ ___.fb2"): here accents are folded
/// ("Café" → "Cafe") and a name with no Latin letter or digit left becomes <c>book-&lt;file id&gt;</c> with its
/// extensions.
/// </summary>
public static partial class DownloadNames
{
    /// <summary>The header's value for an attachment named <paramref name="name"/>.</summary>
    public static string Disposition(string name, int fileId)
    {
        var disposition = new ContentDispositionHeaderValue("attachment") { FileName = Quote(Ascii(name, fileId)), FileNameStar = name };
        return disposition.ToString();
    }

    /// <summary>The ASCII name: accents folded, other characters a single <c>_</c>, never only signs.</summary>
    public static string Ascii(string name, int fileId)
    {
        var folded = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            folded.Append(IsPlain(c) ? c : '_');
        }

        var ascii = Underscores().Replace(folded.ToString(), "_");
        var extension = Extensions().Match(ascii).Value;
        var stem = ascii[..^extension.Length].Trim(' ', '_', '.');
        return stem.Any(char.IsAsciiLetterOrDigit) ? stem + extension : $"book-{fileId.ToString(CultureInfo.InvariantCulture)}{extension}";
    }

    /// <summary>Safe in a quoted header value on any browser: letters, digits, space and a few signs (no quote, no backslash, no %).</summary>
    private static bool IsPlain(char c) => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '-' or '_' or '(' or ')' or '[' or ']' or ',' or '+' or '!';

    private static string Quote(string value) => "\"" + value + "\"";

    /// <summary>A run of replaced characters, with the spaces among them: one <c>_</c>.</summary>
    [GeneratedRegex("[_ ]*_[_ ]*")]
    private static partial Regex Underscores();

    /// <summary>The last one or two extensions (".fb2", ".fb2.zip").</summary>
    [GeneratedRegex(@"(\.[A-Za-z0-9]{1,5}){1,2}$")]
    private static partial Regex Extensions();
}
