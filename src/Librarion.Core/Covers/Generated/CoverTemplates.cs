using SkiaSharp;

namespace Librarion.Core.Covers.Generated;

/// <summary>What a generated cover says: the title (or the file name), the authors, the series with its number.</summary>
public sealed record CoverText(string Title, string? Authors, string? Series);

/// <summary>A palette: the background, an accent for shapes and rules, the title's colour and a quieter one for the rest.</summary>
public sealed record CoverPalette(SKColor Background, SKColor Accent, SKColor Ink, SKColor Muted);

/// <summary>
/// One design of generated cover, drawn on a 600 × 900 canvas (scaled to each size). Templates are code: a new one
/// is a class added to <see cref="GeneratedCovers.Templates"/>, at the end (the index is stored on each book).
/// </summary>
public abstract class CoverTemplate
{
    public const float Width = 600;
    public const float Height = 900;

    public abstract string Name { get; }

    public abstract void Draw(SKCanvas canvas, CoverText text, CoverPalette palette, ulong seed);

    protected static void Fill(SKCanvas canvas, SKColor color, SKRect rect)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawRect(rect, paint);
    }

    protected static void Line(SKCanvas canvas, SKColor color, float width, float x0, float y0, float x1, float y1)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true, StrokeWidth = width, Style = SKPaintStyle.Stroke };
        canvas.DrawLine(x0, y0, x1, y1, paint);
    }
}

/// <summary>A cloth binding: a framed panel, the title in serif in the middle, the authors below, the series above.</summary>
public sealed class ClassicTemplate : CoverTemplate
{
    public override string Name => "classic";

    public override void Draw(SKCanvas canvas, CoverText text, CoverPalette palette, ulong seed)
    {
        canvas.Clear(palette.Background);
        using (var frame = new SKPaint { Color = palette.Accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6 })
        {
            canvas.DrawRect(new SKRect(36, 36, Width - 36, Height - 36), frame);
            frame.StrokeWidth = 2;
            canvas.DrawRect(new SKRect(50, 50, Width - 50, Height - 50), frame);
        }

        CoverLayout.Draw(canvas, text.Series, new SKRect(80, 80, Width - 80, 150), new TextStyle(CoverFace.Sans, 24, 16, 2, palette.Muted));
        CoverLayout.Draw(canvas, text.Title, new SKRect(80, 170, Width - 80, 600), new TextStyle(CoverFace.SerifBold, 66, 26, 7, palette.Ink));
        Line(canvas, palette.Accent, 2, Width / 2 - 60, 630, Width / 2 + 60, 630);
        CoverLayout.Draw(canvas, text.Authors, new SKRect(80, 660, Width - 80, 820), new TextStyle(CoverFace.Sans, 34, 18, 3, palette.Ink));
    }
}

/// <summary>Type alone: a large title from the top left, a rule, the authors at the foot.</summary>
public sealed class TypographicTemplate : CoverTemplate
{
    public override string Name => "typographic";

    public override void Draw(SKCanvas canvas, CoverText text, CoverPalette palette, ulong seed)
    {
        canvas.Clear(palette.Background);
        var seriesHeight = CoverLayout.Draw(canvas, text.Series, new SKRect(56, 64, Width - 56, 124),
            new TextStyle(CoverFace.SansBold, 24, 16, 2, palette.Accent, TextAlign.Start, TextAnchor.Top));
        CoverLayout.Draw(canvas, text.Title, new SKRect(56, 80 + seriesHeight, Width - 56, 690),
            new TextStyle(CoverFace.SansBold, 92, 30, 8, palette.Ink, TextAlign.Start, TextAnchor.Top, 1.08f));
        Line(canvas, palette.Accent, 4, 56, 720, Width - 56, 720);
        CoverLayout.Draw(canvas, text.Authors, new SKRect(56, 740, Width - 56, 850),
            new TextStyle(CoverFace.Sans, 34, 18, 2, palette.Muted, TextAlign.Start, TextAnchor.Top));
    }
}

/// <summary>Shapes from the palette, placed by the book's hash, over the top; a plain panel with the text below.</summary>
public sealed class GeometricTemplate : CoverTemplate
{
    public override string Name => "geometric";

    public override void Draw(SKCanvas canvas, CoverText text, CoverPalette palette, ulong seed)
    {
        canvas.Clear(palette.Background);
        var random = new Random(unchecked((int)seed ^ (int)(seed >> 32)));
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, Width, 470));
            for (var i = 0; i < 7; i++)
            {
                paint.Color = (i % 2 == 0 ? palette.Accent : palette.Muted).WithAlpha((byte)random.Next(120, 230));
                var x = random.Next(-60, (int)Width + 60);
                var y = random.Next(-60, 500);
                var r = random.Next(50, 190);
                if (random.Next(3) == 0)
                    canvas.DrawRect(new SKRect(x - r, y - r / 2f, x + r, y + r / 2f), paint);
                else if (random.Next(2) == 0)
                    canvas.DrawCircle(x, y, r, paint);
                else
                {
                    using var builder = new SKPathBuilder();
                    builder.MoveTo(x, y - r);
                    builder.LineTo(x + r, y + r);
                    builder.LineTo(x - r, y + r);
                    builder.Close();
                    using var path = builder.Detach();
                    canvas.DrawPath(path, paint);
                }
            }

            canvas.Restore();
        }

        Fill(canvas, palette.Ink, new SKRect(0, 470, Width, Height));
        CoverLayout.Draw(canvas, text.Title, new SKRect(48, 500, Width - 48, 740), new TextStyle(CoverFace.SansBold, 58, 24, 5, palette.Background));
        CoverLayout.Draw(canvas, text.Authors, new SKRect(48, 750, Width - 48, 830), new TextStyle(CoverFace.Sans, 30, 18, 2, palette.Background.WithAlpha(210)));
        CoverLayout.Draw(canvas, text.Series, new SKRect(48, 835, Width - 48, 880), new TextStyle(CoverFace.Sans, 22, 15, 1, palette.Accent));
    }
}

/// <summary>A band across the top carrying the series, the title in serif below, a thin band at the foot.</summary>
public sealed class BandTemplate : CoverTemplate
{
    public override string Name => "band";

    public override void Draw(SKCanvas canvas, CoverText text, CoverPalette palette, ulong seed)
    {
        canvas.Clear(palette.Background);
        Fill(canvas, palette.Accent, new SKRect(0, 0, Width, 170));
        Fill(canvas, palette.Accent, new SKRect(0, Height - 28, Width, Height));
        CoverLayout.Draw(canvas, text.Series, new SKRect(40, 30, Width - 40, 140), new TextStyle(CoverFace.SansBold, 34, 18, 2, palette.Background));
        CoverLayout.Draw(canvas, text.Title, new SKRect(60, 210, Width - 60, 650), new TextStyle(CoverFace.SerifBold, 64, 26, 7, palette.Ink));
        CoverLayout.Draw(canvas, text.Authors, new SKRect(60, 680, Width - 60, 840), new TextStyle(CoverFace.Sans, 32, 18, 3, palette.Muted));
    }
}

/// <summary>Only the title and the authors, centred, with room around them.</summary>
public sealed class MinimalTemplate : CoverTemplate
{
    public override string Name => "minimal";

    public override void Draw(SKCanvas canvas, CoverText text, CoverPalette palette, ulong seed)
    {
        canvas.Clear(palette.Background);
        var height = CoverLayout.Draw(canvas, text.Title, new SKRect(70, 200, Width - 70, 600), new TextStyle(CoverFace.SansBold, 60, 24, 7, palette.Ink));
        Fill(canvas, palette.Accent, new SKRect(Width / 2 - 24, 400 + height / 2 + 30, Width / 2 + 24, 400 + height / 2 + 36));
        CoverLayout.Draw(canvas, text.Authors, new SKRect(70, 660, Width - 70, 800), new TextStyle(CoverFace.Sans, 30, 18, 3, palette.Muted));
    }
}
