using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace Librarion.Api.EReader;

/// <summary>
/// <c>ereader.css</c>, embedded in this assembly: the e-reader pages' only stylesheet (D-8.9: grayscale, a test reads
/// it). Its address carries a hash of its content, so it is cached for good and a new build is fetched at once.
/// </summary>
public static class EReaderStylesheet
{
    public const string ResourceName = "Librarion.EReader.ereader.css";

    public static string Text { get; } = Read();

    /// <summary>The first hex digits of the content's SHA-256: the <c>?v=</c> of its address and its ETag.</summary>
    public static string Version { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)))[..12];

    public static IResult Serve(HttpContext context)
    {
        var tag = new EntityTagHeaderValue($"\"{Version}\"");
        context.Response.Headers.ETag = tag.ToString();
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        if (context.Request.Headers.IfNoneMatch.ToString().Contains(tag.Tag.ToString(), StringComparison.Ordinal))
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        return TypedResults.Text(Text, "text/css", Encoding.UTF8);
    }

    private static string Read()
    {
        using var stream = typeof(EReaderStylesheet).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"{ResourceName} is not embedded in the API assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
