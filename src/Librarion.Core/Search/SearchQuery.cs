using System.Text;

namespace Librarion.Core.Search;

public enum SearchTermKind
{
    /// <summary>Words in the title (or file name) or in a tag's name: an author, a series, a category…</summary>
    Text,
    /// <summary><c>title:</c> words in the title only.</summary>
    Title,
    /// <summary><c>author:"Stephen King"</c>: a value of a field (or one under it); <c>author:</c> alone, any value.</summary>
    Field,
    /// <summary><c>tag:Favourites</c> a plain tag, <c>tag:"category:Horror"</c> a field's value; with the tags under it.</summary>
    Tag,
    /// <summary><c>format:epub</c>: a file of the format.</summary>
    Format,
    /// <summary>
    /// <c>special:untagged</c> (no plain tag), <c>special:duplicates</c> (in a stored duplicate group; only where the
    /// caller may merge books): a property of the book, not a name (stage 6 chunk 15).
    /// </summary>
    Special,
}

/// <param name="Key">The field's key for <see cref="SearchTermKind.Field"/>; null otherwise.</param>
/// <param name="Value">As typed, quotes removed; empty for <c>field:</c> alone.</param>
/// <param name="Quoted">In quotes: a phrase (words in a row), a whole name.</param>
/// <param name="Prefix">Ends with <c>*</c>, or a bare word of free text (it may still be being typed).</param>
public sealed record SearchTerm(SearchTermKind Kind, bool Negated, string? Key, string Value, bool Quoted, bool Prefix);

/// <summary>
/// The search language (agents/docs/features/search.md, D-5.7): words and "quoted phrases"; <c>key:value</c> for
/// every field and for <c>tag:</c>, <c>format:</c>, <c>title:</c>, <c>special:</c>; <c>-</c> before a term leaves its books out;
/// <c>*</c> after a value matches names starting with it. A key that is no field is text ("Re:Zero" is a title).
/// </summary>
public sealed record SearchQuery(IReadOnlyList<SearchTerm> Terms)
{
    public const int MaxLength = 500;
    public const int MaxTerms = 20;

    private static readonly Dictionary<string, SearchTermKind> Keywords = new(StringComparer.Ordinal)
    {
        ["title"] = SearchTermKind.Title,
        ["tag"] = SearchTermKind.Tag,
        ["format"] = SearchTermKind.Format,
        ["special"] = SearchTermKind.Special,
    };

    public bool IsEmpty => Terms.Count == 0;

    /// <param name="isField">Whether a key (lower case) names one of the tenant's fields.</param>
    public static SearchQuery Parse(string? text, Func<string, bool> isField)
    {
        var terms = new List<SearchTerm>();
        if (string.IsNullOrWhiteSpace(text))
            return new SearchQuery(terms);
        var input = text.Length > MaxLength ? text[..MaxLength] : text;
        var i = 0;
        while (i < input.Length && terms.Count < MaxTerms)
        {
            if (char.IsWhiteSpace(input[i]))
            {
                i++;
                continue;
            }

            var negated = input[i] == '-' && i + 1 < input.Length && !char.IsWhiteSpace(input[i + 1]);
            if (negated)
                i++;

            // key: before a quote or a space?
            var keyEnd = i;
            while (keyEnd < input.Length && (char.IsAsciiLetterOrDigit(input[keyEnd]) || input[keyEnd] is '-' or '_'))
                keyEnd++;
            if (keyEnd > i && keyEnd < input.Length && input[keyEnd] == ':')
            {
                var key = input[i..keyEnd].ToLowerInvariant();
                SearchTermKind? kind = Keywords.TryGetValue(key, out var keyword) ? keyword : isField(key) ? SearchTermKind.Field : null;
                if (kind is { } known)
                {
                    i = keyEnd + 1;
                    var (value, quoted, prefix) = Value(input, ref i, bareIsPrefix: false);
                    if (value.Length > 0 || known == SearchTermKind.Field)
                        terms.Add(new SearchTerm(known, negated, known == SearchTermKind.Field ? key : null, value, quoted, prefix));
                    continue;
                }
            }

            var (words, isQuoted, isPrefix) = Value(input, ref i, bareIsPrefix: true);
            if (SearchText.Words(words).Count > 0)
                terms.Add(new SearchTerm(SearchTermKind.Text, negated, null, words, isQuoted, isPrefix));
        }

        return new SearchQuery(terms);
    }

    /// <summary>A quoted value (to its closing quote or the end) or a bare one (to the next space); a trailing <c>*</c> asks for a prefix.</summary>
    private static (string Value, bool Quoted, bool Prefix) Value(string input, ref int i, bool bareIsPrefix)
    {
        if (i < input.Length && input[i] == '"')
        {
            var close = input.IndexOf('"', i + 1);
            var value = close < 0 ? input[(i + 1)..] : input[(i + 1)..close];
            i = close < 0 ? input.Length : close + 1;
            var star = i < input.Length && input[i] == '*';
            if (star)
                i++;
            // An open quote is still being typed: its last word may be too.
            return (value.Trim(), true, star || close < 0);
        }

        var start = i;
        while (i < input.Length && !char.IsWhiteSpace(input[i]))
            i++;
        var bare = input[start..i];
        if (bare.EndsWith('*'))
            return (bare.TrimEnd('*'), false, true);
        return (bare, false, bareIsPrefix);
    }
}
