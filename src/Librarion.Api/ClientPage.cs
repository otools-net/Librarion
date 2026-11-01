using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Librarion.Api;

/// <summary>
/// The client's page for every address the client routes itself (<c>/authors/123</c>, an unknown one...). Its
/// <c>&lt;base href="./"&gt;</c> works at the root only: from <c>/a/b</c> the browser would ask <c>/a/main.js</c>.
/// Here it becomes the app's real root, the request's <c>PathBase</c> included (a virtual directory behind a proxy),
/// so scripts, styles and the router's links resolve from any depth.
/// </summary>
public sealed class ClientPage(IWebHostEnvironment environment)
{
    private const string RelativeBase = "<base href=\"./\">";
    private (DateTimeOffset Modified, string Html)? _cached;

    public IResult Serve(HttpContext context)
    {
        var html = Read();
        if (html is null)
            return TypedResults.NotFound();
        var root = context.Request.PathBase.Value?.TrimEnd('/') + "/";
        context.Response.Headers.CacheControl = "no-cache";
        return TypedResults.Content(html.Replace(RelativeBase, $"<base href=\"{System.Net.WebUtility.HtmlEncode(root)}\">", StringComparison.Ordinal),
            new MediaTypeHeaderValue("text/html") { Charset = "utf-8" }.ToString());
    }

    /// <summary>index.html as built, read again when the build replaced it (development's watch builds).</summary>
    private string? Read()
    {
        var file = environment.WebRootFileProvider.GetFileInfo("index.html");
        if (!file.Exists || file is NotFoundFileInfo)
            return null;
        if (_cached is { } cached && cached.Modified == file.LastModified)
            return cached.Html;
        using var reader = new StreamReader(file.CreateReadStream());
        var html = reader.ReadToEnd();
        _cached = (file.LastModified, html);
        return html;
    }
}
