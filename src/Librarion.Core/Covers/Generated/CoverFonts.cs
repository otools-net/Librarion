using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Librarion.Core.Covers.Generated;

public enum CoverFace
{
    Sans,
    SansBold,
    SerifBold,
}

/// <summary>
/// The fonts bundled for generated covers (Noto, SIL OFL 1.1, in <c>fonts/</c> next to the program; nothing read
/// from the system, so a cover looks the same on every server): Latin, Greek and Cyrillic in the template's face; Arabic,
/// Hebrew, Devanagari, Bengali, Tamil, Thai and CJK (Chinese, Japanese kana, Korean Hangul: one font) in Noto's own
/// families. A text takes the font of the first letter of such a script; characters its font lacks are left out, never
/// the cover. A new script is a line in <see cref="Scripts"/> and its font in <c>Covers/Fonts/</c>.
/// </summary>
public static class CoverFonts
{
    /// <summary>Scripts with a font of their own: code point ranges and the font's file name (without extension).</summary>
    private static readonly (int First, int Last, string Font)[] Scripts =
    [
        (0x0590, 0x05FF, "NotoSansHebrew-Bold"),
        (0xFB1D, 0xFB4F, "NotoSansHebrew-Bold"),
        (0x0600, 0x06FF, "NotoSansArabic-Bold"),
        (0x0750, 0x077F, "NotoSansArabic-Bold"),
        (0x08A0, 0x08FF, "NotoSansArabic-Bold"),
        (0xFB50, 0xFDFF, "NotoSansArabic-Bold"),
        (0xFE70, 0xFEFF, "NotoSansArabic-Bold"),
        (0x0900, 0x097F, "NotoSansDevanagari-Bold"),
        (0x0980, 0x09FF, "NotoSansBengali-Bold"),
        (0x0B80, 0x0BFF, "NotoSansTamil-Bold"),
        (0x0E00, 0x0E7F, "NotoSansThai-Bold"),
        (0x1100, 0x11FF, "NotoSansCJKsc-Bold"),
        (0x2E80, 0x9FFF, "NotoSansCJKsc-Bold"),
        (0xA960, 0xA97F, "NotoSansCJKsc-Bold"),
        (0xAC00, 0xD7FF, "NotoSansCJKsc-Bold"),
        (0xF900, 0xFAFF, "NotoSansCJKsc-Bold"),
        (0xFF00, 0xFFEF, "NotoSansCJKsc-Bold"),
        (0x20000, 0x3134F, "NotoSansCJKsc-Bold"),
    ];

    /// <summary>The folder holding the fonts (<c>fonts/</c> next to the program).</summary>
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "fonts");

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SKTypeface> Loaded = new(StringComparer.Ordinal);

    /// <summary>The typeface for <paramref name="text"/> in <paramref name="face"/>, and the text without the characters it lacks.</summary>
    public static (SKTypeface Typeface, string Text) For(string text, CoverFace face)
    {
        var font = Script(text) ?? face switch
        {
            CoverFace.Sans => "NotoSans-Regular",
            CoverFace.SansBold => "NotoSans-Bold",
            _ => "NotoSerif-Bold",
        };
        var typeface = Loaded.GetOrAdd(font, Load);
        return (typeface, Covered(typeface, text));
    }

    /// <summary>The font of the first letter in a script of its own; null for Latin, Greek, Cyrillic and the rest.</summary>
    private static string? Script(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value < 0x0590)
                continue;
            foreach (var (first, last, font) in Scripts)
                if (value >= first && value <= last)
                    return font;
        }

        return null;
    }

    /// <summary>The text without what the typeface can't draw (spaces kept, so words stay apart).</summary>
    private static string Covered(SKTypeface typeface, string text)
    {
        var kept = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Format
                || typeface.GetGlyph(rune.Value) != 0)
                kept.Append(rune.ToString());
        }

        return string.Join(' ', kept.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static SKTypeface Load(string font)
    {
        var path = new[] { ".ttf", ".otf" }.Select(extension => Path.Combine(Directory, font + extension)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"The bundled font {font} is missing from {Directory}.");
        return SKTypeface.FromFile(path) ?? throw new InvalidOperationException($"The bundled font {path} doesn't load.");
    }
}
