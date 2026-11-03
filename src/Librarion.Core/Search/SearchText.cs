using System.Globalization;
using System.Text;

namespace Librarion.Core.Search;

/// <summary>
/// How titles are indexed and searched words compared (agents/docs/features/search.md): NFKD, accents and case
/// dropped, anything but letters and digits a space. Done here, not by the database, so both providers index and
/// match the same words (PostgreSQL's <c>simple</c> parser keeps accents and reads "a.b" as a host name).
/// </summary>
public static class SearchText
{
    /// <summary>The words of <paramref name="text"/>, folded, joined by single spaces.</summary>
    public static string Fold(string? text) => string.Join(' ', Words(text));

    /// <summary>A book's indexed text: its title and, when it differs, its file's name ("123456" finds lib.rus.ec's books).</summary>
    public static string ForBook(string? title, string? fileTitle)
    {
        var folded = Fold(title);
        var file = Fold(fileTitle);
        return folded.Length == 0 ? file : file.Length == 0 || file == folded ? folded : $"{folded} {file}";
    }

    /// <summary>The words lower-cased only (NFKC, accents kept), split where <see cref="Words"/> splits: names as spelled.</summary>
    public static IReadOnlyList<string> Spelled(string? text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in (text ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || char.IsSurrogate(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                word.Append(c);
                continue;
            }

            if (word.Length > 0)
                words.Add(word.ToString());
            word.Clear();
        }

        if (word.Length > 0)
            words.Add(word.ToString());
        return words;
    }

    public static IReadOnlyList<string> Words(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(text))
            return words;
        var word = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormKD))
        {
            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark:
                    continue;
                case UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                    or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
                    or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber:
                    word.Append(char.ToLowerInvariant(c));
                    continue;
                default:
                    if (char.IsSurrogate(c))
                    {
                        word.Append(c);
                        continue;
                    }

                    if (word.Length > 0)
                        words.Add(word.ToString());
                    word.Clear();
                    continue;
            }
        }

        if (word.Length > 0)
            words.Add(word.ToString());
        return words;
    }
}
