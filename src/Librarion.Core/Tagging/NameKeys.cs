using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Librarion.Core.Tagging;

/// <summary>
/// Match keys of tag names, for possible duplicate tags (stage 6 chunk 14, the user's answers 2026-11-09,
/// agents/docs/features/merging.md#duplicate-tags): names folded to their words (accents off, Cyrillic and Greek written
/// in Latin letters, punctuation and order ignored: "King, Stephen" = "Stephen King"), a <see cref="SoundKey"/> on top
/// (vowels and like consonants folded per word: "Steven" = "Stephen" = "Стивен") and an <see cref="InitialsKey"/>
/// (surname and first initial: "S. King" with "Stephen King"). Keys are XxHash64 values stored on each tag.
/// </summary>
public static class NameKeys
{
    /// <summary>The tag's keys from its name: none on a field's own row or a category (never suggested as duplicates).</summary>
    public static void Apply(Data.Tag tag)
    {
        var keyed = tag.FieldKey is null && !tag.IsCategory;
        tag.SoundKey = keyed ? SoundKey(tag.Name) : null;
        tag.InitialsKey = keyed ? InitialsKey(tag.Name) : null;
    }

    /// <summary>The name's words: decomposed, accents off, lower case, written in Latin letters, split on anything but letters and digits.</summary>
    public static List<string> Words(string name)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;
            var lower = char.ToLowerInvariant(c);
            if (Latin(lower) is { } latin)
                word.Append(latin);
            else if (char.IsLetterOrDigit(lower))
                word.Append(lower);
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
            words.Add(word.ToString());
        return words;
    }

    /// <summary>The words in order, one space apart: equal for names of the same words ("King, Stephen", "Stephen King").</summary>
    public static string WordsText(string name) => string.Join(' ', Words(name).Order(StringComparer.Ordinal));

    /// <summary>The sounds of the words, in order (null when the name has none): equal for names that sound alike.</summary>
    public static long? SoundKey(string name)
    {
        var words = Words(name);
        return words.Count == 0 ? null : Hash("s\n" + string.Join(' ', words.Select(Sound).Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// The surname's sound and the first given name's initial ("King, Stephen", "Stephen King", "S. King": the same);
    /// null for a name of one word. The surname is what comes before a comma, else the last word.
    /// </summary>
    public static long? InitialsKey(string name)
    {
        if (Split(name) is not { } split || split.Given.Count == 0 || split.Surname.Count == 0)
            return null;
        return Hash($"i\n{string.Join(' ', split.Surname.Select(Sound))}\n{split.Given[0][0]}");
    }

    /// <summary>A person's name whose given names are only initials ("S. King", "King, J. R."): what an initials group needs one of.</summary>
    public static bool IsInitials(string name) =>
        Split(name) is { } split && split.Surname.Count > 0 && split.Given.Count > 0 && split.Given.All(g => g.Length == 1);

    /// <summary>The surname's words and the given names' words; null for a name of fewer than two words.</summary>
    private static (List<string> Surname, List<string> Given)? Split(string name)
    {
        var comma = name.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0)
        {
            var (surname, given) = (Words(name[..comma]), Words(name[(comma + 1)..]));
            return surname.Count + given.Count < 2 ? null : (surname, given);
        }

        var words = Words(name);
        return words.Count < 2 ? null : ([words[^1]], words[..^1]);
    }

    /// <summary>
    /// A word's sound: spellings of one sound made one ("ph" "f" "w" → "v", "ck" "q" "c" → "k", "y" "j" → "i", an "h"
    /// after a letter dropped), every vowel "a", runs of one letter one. Numbers stay as they are.
    /// </summary>
    public static string Sound(string word)
    {
        if (word.All(char.IsAsciiDigit))
            return word;
        var text = word
            .Replace("shch", "S", StringComparison.Ordinal).Replace("sch", "S", StringComparison.Ordinal).Replace("sh", "S", StringComparison.Ordinal)
            .Replace("tch", "C", StringComparison.Ordinal).Replace("ch", "C", StringComparison.Ordinal).Replace("zh", "Z", StringComparison.Ordinal)
            .Replace("kh", "h", StringComparison.Ordinal).Replace("ph", "v", StringComparison.Ordinal).Replace("th", "t", StringComparison.Ordinal)
            .Replace("gh", "g", StringComparison.Ordinal).Replace("ck", "k", StringComparison.Ordinal).Replace("x", "ks", StringComparison.Ordinal);
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i] switch
            {
                'f' or 'w' => 'v',
                'q' or 'c' => 'k',
                'y' or 'j' => 'i',
                var other => other,
            };
            if (c == 'h' && i > 0)
                continue;
            if (c is 'a' or 'e' or 'i' or 'o' or 'u')
                c = 'a';
            if (result.Length == 0 || result[^1] != c)
                result.Append(c);
        }

        return result.ToString();
    }

    private static long Hash(string text) => (long)XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text));

    /// <summary>A Cyrillic or Greek letter in Latin letters (the usual spellings of library catalogs); null for any other.</summary>
    private static string? Latin(char c) => c switch
    {
        'а' => "a", 'б' => "b", 'в' => "v", 'г' => "g", 'д' => "d", 'е' => "e", 'ё' => "e", 'ж' => "zh", 'з' => "z", 'и' => "i", 'й' => "i",
        'к' => "k", 'л' => "l", 'м' => "m", 'н' => "n", 'о' => "o", 'п' => "p", 'р' => "r", 'с' => "s", 'т' => "t", 'у' => "u", 'ф' => "f",
        'х' => "kh", 'ц' => "ts", 'ч' => "ch", 'ш' => "sh", 'щ' => "shch", 'ъ' => "", 'ы' => "y", 'ь' => "", 'э' => "e", 'ю' => "iu", 'я' => "ia",
        'є' => "ie", 'і' => "i", 'ї' => "i", 'ґ' => "g", 'ў' => "u",
        'α' => "a", 'β' => "v", 'γ' => "g", 'δ' => "d", 'ε' => "e", 'ζ' => "z", 'η' => "i", 'θ' => "th", 'ι' => "i", 'κ' => "k", 'λ' => "l",
        'μ' => "m", 'ν' => "n", 'ξ' => "x", 'ο' => "o", 'π' => "p", 'ρ' => "r", 'σ' or 'ς' => "s", 'τ' => "t", 'υ' => "y", 'φ' => "f", 'χ' => "ch",
        'ψ' => "ps", 'ω' => "o",
        _ => null,
    };
}
