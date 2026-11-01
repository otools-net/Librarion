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
/// The fonts bundled for generated covers (Noto, SIL OFL 1.1, embedded in the assembly: nothing read from the
/// system, so a cover looks the same on every server): Latin, Greek and Cyrillic in the template's face, Arabic and
/// Hebrew in Noto's own families. Text in a script none of them covers (CJK for now) loses those characters, never the
/// cover.
/// </summary>
public static class CoverFonts
{
    private static readonly Lazy<Dictionary<string, SKTypeface>> Faces = new(Load);

    /// <summary>The typeface for <paramref name="text"/> in <paramref name="face"/>, and the text without the characters it lacks.</summary>
    public static (SKTypeface Typeface, string Text) For(string text, CoverFace face)
    {
        var typeface = Faces.Value[Script(text) switch
        {
            "arabic" => "NotoSansArabic-Bold",
            "hebrew" => "NotoSansHebrew-Bold",
            _ => face switch
            {
                CoverFace.Sans => "NotoSans-Regular",
                CoverFace.SansBold => "NotoSans-Bold",
                _ => "NotoSerif-Bold",
            },
        }];
        return (typeface, Covered(typeface, text));
    }

    /// <summary>The script that decides the font: the first Arabic or Hebrew letter wins over Latin around it.</summary>
    private static string Script(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x08A0 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF)
                return "arabic";
            if (value is >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F)
                return "hebrew";
        }

        return "latin";
    }

    /// <summary>The text without what the typeface can't draw (spaces kept, so words stay apart).</summary>
    private static string Covered(SKTypeface typeface, string text)
    {
        var kept = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format
                || typeface.GetGlyph(rune.Value) != 0)
                kept.Append(rune.ToString());
        }

        return string.Join(' ', kept.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static Dictionary<string, SKTypeface> Load()
    {
        var assembly = typeof(CoverFonts).Assembly;
        var faces = new Dictionary<string, SKTypeface>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Librarion.Fonts.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var data = SKData.Create(stream);
            faces[Path.GetFileNameWithoutExtension(name["Librarion.Fonts.".Length..])] = SKTypeface.FromData(data)
                ?? throw new InvalidOperationException($"The bundled font {name} doesn't load.");
        }

        return faces;
    }
}
