using SkiaSharp;

namespace Librarion.Core.Covers.Generated;

/// <summary>
/// Covers for books without one (agents/docs/features/book-display.md#generated-covers): a template and a palette are
/// assigned at import from the book's identity (the same book always looks the same, templates spread evenly), and the
/// image is drawn on first view.
/// </summary>
public static class GeneratedCovers
{
    /// <summary>The templates by stored index: new ones go at the end, none is ever removed or moved.</summary>
    public static IReadOnlyList<CoverTemplate> Templates { get; } =
        [new ClassicTemplate(), new TypographicTemplate(), new GeometricTemplate(), new BandTemplate(), new MinimalTemplate()];

    /// <summary>The palettes by stored index (a book's variant), each readable with every template.</summary>
    public static IReadOnlyList<CoverPalette> Palettes { get; } =
    [
        new(new SKColor(0x1F, 0x3A, 0x5F), new SKColor(0xD9, 0xA4, 0x41), new SKColor(0xF4, 0xEE, 0xE0), new SKColor(0xB8, 0xC4, 0xD6)),
        new(new SKColor(0x6B, 0x1E, 0x2B), new SKColor(0xE8, 0xC3, 0x7E), new SKColor(0xFA, 0xF1, 0xE4), new SKColor(0xE0, 0xB8, 0xBE)),
        new(new SKColor(0x23, 0x4D, 0x3C), new SKColor(0xC9, 0xB0, 0x6B), new SKColor(0xF2, 0xF0, 0xE6), new SKColor(0xB5, 0xCF, 0xC2)),
        new(new SKColor(0xF3, 0xEC, 0xDD), new SKColor(0xB2, 0x3A, 0x2E), new SKColor(0x22, 0x1E, 0x1A), new SKColor(0x5E, 0x55, 0x4B)),
        new(new SKColor(0xE9, 0xEE, 0xF2), new SKColor(0x2F, 0x6F, 0xA8), new SKColor(0x15, 0x22, 0x30), new SKColor(0x4A, 0x5A, 0x6B)),
        new(new SKColor(0x2B, 0x2B, 0x2E), new SKColor(0xE0, 0x6C, 0x3C), new SKColor(0xF5, 0xF2, 0xEC), new SKColor(0xB9, 0xB4, 0xAC)),
        new(new SKColor(0xF6, 0xE7, 0xC8), new SKColor(0x3E, 0x7C, 0x6E), new SKColor(0x2A, 0x23, 0x1C), new SKColor(0x6A, 0x5B, 0x47)),
        new(new SKColor(0x3D, 0x2C, 0x5C), new SKColor(0xE4, 0xB8, 0x5A), new SKColor(0xF3, 0xEE, 0xF8), new SKColor(0xC4, 0xB8, 0xD8)),
    ];

    /// <summary>
    /// The template and palette of a book, from a 64-bit value of its identity (its <c>WorkKey</c>), among the templates
    /// not in <paramref name="off"/> (a tenant's <c>CoverTemplatesOff</c>; all of them when none would be left). With
    /// every template on, the same as it always was: a book keeps its look.
    /// </summary>
    public static (short Template, short Variant) Assign(long identity, int off = 0)
    {
        var on = On(off);
        var mixed = Mix((ulong)identity);
        return ((short)on[(int)(mixed % (ulong)on.Count)], (short)(mixed / (ulong)on.Count % (ulong)Palettes.Count));
    }

    /// <summary>The templates' indexes not in <paramref name="off"/> (bit <c>i</c> = template <c>i</c>); every one when that leaves none.</summary>
    public static IReadOnlyList<int> On(int off)
    {
        var on = Enumerable.Range(0, Templates.Count).Where(i => (off & (1 << i)) == 0).ToList();
        return on.Count > 0 ? on : Enumerable.Range(0, Templates.Count).ToList();
    }

    /// <summary>The cover drawn at <paramref name="size"/> (2:3), as WebP. An unknown index falls back to the first.</summary>
    public static byte[] Render(int template, int variant, CoverText text, CoverSize size)
    {
        var width = CoverImages.Width(size);
        var height = (int)Math.Round(width * CoverTemplate.Height / CoverTemplate.Width);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Scale(width / CoverTemplate.Width);
            var design = template >= 0 && template < Templates.Count ? Templates[template] : Templates[0];
            var palette = variant >= 0 && variant < Palettes.Count ? Palettes[variant] : Palettes[0];
            design.Draw(canvas, text, palette, Mix((ulong)System.IO.Hashing.XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(text.Title))));
        }

        return CoverImages.Encode(bitmap);
    }

    /// <summary>SplitMix64's finaliser: neighbouring identities land far apart.</summary>
    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
