using System.Globalization;
using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Librarion.Core.Covers.Generated;

public enum TextAlign
{
    Start,
    Center,
}

public enum TextAnchor
{
    Top,
    Middle,
    Bottom,
}

/// <param name="MaxSize">The size tried first.</param>
/// <param name="MinSize">The smallest size: what still doesn't fit in <paramref name="MaxLines"/> is cut with an ellipsis.</param>
public sealed record TextStyle(CoverFace Face, float MaxSize, float MinSize, int MaxLines, SKColor Color,
    TextAlign Align = TextAlign.Center, TextAnchor Anchor = TextAnchor.Middle, float LineHeight = 1.18f);

/// <summary>
/// Text fitted into a box on a cover: shaped by HarfBuzz (ligatures, Arabic joining, right-to-left), broken into lines
/// between words (a word wider than the box between its characters), shrunk in steps until it fits, cut with an
/// ellipsis at the smallest size. Lines are laid out in logical order; HarfBuzz orders each line's glyphs.
/// </summary>
public static class CoverLayout
{
    private const string Ellipsis = "…";

    /// <summary>Draws <paramref name="text"/> in <paramref name="box"/>; the height it took (0 for no text).</summary>
    public static float Draw(SKCanvas canvas, string? text, SKRect box, TextStyle style)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        var (typeface, covered) = CoverFonts.For(text.Trim(), style.Face);
        if (covered.Length == 0)
            return 0;
        using var shaper = new SKShaper(typeface);
        using var font = new SKFont(typeface) { Subpixel = true, Edging = SKFontEdging.Antialias };

        // A word is broken inside only at the smallest size, unless the text has no spaces at all (Chinese, Japanese):
        // there lines break between characters at any size.
        var unspaced = !covered.Contains(' ');
        List<string> lines = [];
        for (var size = style.MaxSize; ; size = Math.Max(style.MinSize, size * 0.9f))
        {
            font.Size = size;
            lines = Wrap(shaper, font, covered, box.Width, out var broken);
            var fits = lines.Count <= style.MaxLines && lines.Count * size * style.LineHeight <= box.Height && (!broken || unspaced);
            if (fits || size <= style.MinSize)
                break;
        }

        var lineHeight = font.Size * style.LineHeight;
        var maxLines = Math.Max(1, Math.Min(style.MaxLines, (int)(box.Height / lineHeight)));
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            lines[^1] = Cut(shaper, font, lines[^1], box.Width);
        }

        var height = lines.Count * lineHeight;
        var top = style.Anchor switch
        {
            TextAnchor.Top => box.Top,
            TextAnchor.Bottom => box.Bottom - height,
            _ => box.MidY - height / 2,
        };
        using var paint = new SKPaint { Color = style.Color, IsAntialias = true };
        var metrics = font.Metrics;
        // The ascent and descent of the font share the line's height around its baseline.
        var baselineOffset = (lineHeight - (metrics.Descent - metrics.Ascent)) / 2 - metrics.Ascent;
        for (var i = 0; i < lines.Count; i++)
        {
            var width = Width(shaper, font, lines[i]);
            var x = style.Align == TextAlign.Center ? box.MidX - width / 2 : Rtl(lines[i]) ? box.Right - width : box.Left;
            canvas.DrawShapedText(shaper, lines[i], x, top + i * lineHeight + baselineOffset, font, paint);
        }

        return height;
    }

    public static float Width(SKShaper shaper, SKFont font, string text) => shaper.Shape(text, font).Width;

    /// <param name="broken">Whether a word had to be broken between its characters.</param>
    private static List<string> Wrap(SKShaper shaper, SKFont font, string text, float width, out bool broken)
    {
        broken = false;
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (Width(shaper, font, candidate) <= width)
            {
                line = candidate;
                continue;
            }

            if (line.Length > 0)
                lines.Add(line);
            line = word;
            // A word wider than the box (a long compound, a file name without spaces) breaks between its characters.
            while (Width(shaper, font, line) > width && Elements(line) > 1)
            {
                var head = Fit(shaper, font, line, width, "");
                broken = true;
                lines.Add(head);
                line = line[head.Length..];
            }
        }

        if (line.Length > 0)
            lines.Add(line);
        return lines;
    }

    /// <summary>The line cut to fit with an ellipsis (when it fits already, it gets one only if text was dropped after it).</summary>
    private static string Cut(SKShaper shaper, SKFont font, string line, float width) =>
        Fit(shaper, font, line, width, Ellipsis).TrimEnd() + Ellipsis;

    /// <summary>The longest start of <paramref name="text"/> (whole characters) that fits with <paramref name="suffix"/> after it.</summary>
    private static string Fit(SKShaper shaper, SKFont font, string text, float width, string suffix)
    {
        var ends = new List<int>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
            ends.Add(elements.ElementIndex + ((string)elements.Current).Length);
        int low = 1, high = ends.Count;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Width(shaper, font, text[..ends[mid - 1]] + suffix) <= width)
                low = mid;
            else
                high = mid - 1;
        }

        return text[..ends[low - 1]];
    }

    private static int Elements(string text) => new StringInfo(text).LengthInTextElements;

    private static bool Rtl(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is >= 0x0590 and <= 0x08FF or >= 0xFB1D and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF)
                return true;
            if (Rune.IsLetter(rune))
                return false;
        }

        return false;
    }
}
