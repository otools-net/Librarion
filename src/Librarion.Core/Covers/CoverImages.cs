using SkiaSharp;

namespace Librarion.Core.Covers;

/// <summary>The sizes a cover is served in: list rows, grid cards, the book page (agents/docs/features/book-display.md).</summary>
public enum CoverSize
{
    S,
    M,
    L,
}

/// <summary>
/// Covers decoded, resized and encoded as JPEG (SkiaSharp, D17), baseline, transparency flattened on white: every
/// reader app decodes it, WebP they often don't (the user's answer, 2026-11-10: JPEG everywhere). A source is looked at
/// before it is decoded: too many bytes, too many pixels or absurd sides are refused (decompression bombs), never
/// allocated.
/// </summary>
public static class CoverImages
{
    /// <summary>A cover's width at each size; the height follows its aspect ratio (generated ones are 2:3).</summary>
    public static int Width(CoverSize size) => size switch
    {
        CoverSize.S => 80,
        CoverSize.M => 240,
        _ => 600,
    };

    /// <summary>The largest picture decoded (a 7000 × 7000 scan); past it, a cover is refused.</summary>
    public const long MaxPixels = 50_000_000;

    public const int MaxSide = 16_384;

    /// <summary>A cover's longest side over its shortest past this is not a cover (a strip, a banner).</summary>
    private const double MaxAspect = 4;

    /// <summary>
    /// The thumbnails' quality: measured 2026-11-10, a drawn cover 600 px wide 26 KB (WebP 80: 12), a photo-like one
    /// 47 KB (35), 240 px 8 and 13 KB; higher grew fast for little (85: 32 and 65 KB).
    /// </summary>
    private const int Quality = 75;

    /// <summary>An uploaded cover is kept once, at most 1,200 px wide: a little better.</summary>
    public const int UploadQuality = 85;

    /// <summary>What every cover is served as.</summary>
    public const string ContentType = "image/jpeg";

    /// <summary>The covers' files' extension.</summary>
    public const string Extension = ".jpg";

    public static readonly CoverSize[] Sizes = [CoverSize.S, CoverSize.M, CoverSize.L];

    /// <summary>The cover at every size, JPEG; null when it is not an image Librarion decodes or breaks a limit.</summary>
    public static IReadOnlyDictionary<CoverSize, byte[]>? Thumbnails(byte[] source)
    {
        if (source.Length is 0 or > CoverExtractor.MaxCoverBytes)
            return null;
        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data);
        if (codec is null)
            return null;
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > MaxSide || info.Height > MaxSide || (long)info.Width * info.Height > MaxPixels
            || Math.Max(info.Width, info.Height) > MaxAspect * Math.Min(info.Width, info.Height))
            return null;

        // Big pictures are decoded already scaled down (JPEG decodes at 1/2, 1/4, 1/8 for almost nothing).
        var largest = Width(CoverSize.L);
        var scale = Math.Min(1f, (float)largest / info.Width);
        var decoded = codec.GetScaledDimensions(Math.Max(scale, 0.125f));
        if (decoded.Width < largest || decoded.Height <= 0)
            decoded = info.Size;
        using var bitmap = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            return null;

        var thumbnails = new Dictionary<CoverSize, byte[]>();
        foreach (var size in Sizes)
            thumbnails[size] = Resize(bitmap, Width(size));
        return thumbnails;
    }

    /// <summary>The formats a cover is uploaded in.</summary>
    private static readonly SKEncodedImageFormat[] Uploadable = [SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Png, SKEncodedImageFormat.Webp];

    /// <summary>
    /// An uploaded cover decoded and made again (stage 6 chunk 10): JPEG, PNG or WebP within the limits of
    /// <see cref="Thumbnails"/>, at most <paramref name="maxWidth"/> wide, as JPEG; null for anything else. Nothing of
    /// what was sent is kept as it was.
    /// </summary>
    public static byte[]? Remake(byte[] source, int maxWidth, long maxBytes)
    {
        if (source.Length == 0 || source.Length > maxBytes)
            return null;
        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data);
        if (codec is null || !Uploadable.Contains(codec.EncodedFormat))
            return null;
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > MaxSide || info.Height > MaxSide || (long)info.Width * info.Height > MaxPixels
            || Math.Max(info.Width, info.Height) > MaxAspect * Math.Min(info.Width, info.Height))
            return null;
        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            return null;
        return Resize(bitmap, maxWidth, UploadQuality);
    }

    /// <summary>A picture at <paramref name="width"/> (never enlarged), as JPEG.</summary>
    public static byte[] Resize(SKBitmap bitmap, int width, int quality = Quality)
    {
        if (bitmap.Width <= width)
            return Encode(bitmap, quality);
        var height = Math.Max(1, (int)Math.Round((double)bitmap.Height * width / bitmap.Width));
        using var resized = bitmap.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return Encode(resized ?? bitmap, quality);
    }

    /// <summary>The picture as a baseline JPEG; what is transparent is white (JPEG has none: it would turn black).</summary>
    public static byte[] Encode(SKBitmap bitmap, int quality = Quality)
    {
        using var flat = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(flat))
        {
            canvas.Clear(SKColors.White);
            using var source = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        }

        using var pixels = flat.PeekPixels();
        using var encoded = pixels.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore))
                            ?? throw new InvalidOperationException("SkiaSharp could not encode the cover as JPEG.");
        return encoded.ToArray();
    }
}
