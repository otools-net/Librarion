using System.Text.Json;
using System.Text.RegularExpressions;
using Librarion.Tests.Infrastructure;

namespace Librarion.Tests;

/// <summary>Priority 1: fully local (agents/docs/development/privacy.md).</summary>
[TestClass]
public sealed partial class PrivacyTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The server's flows reach nothing outside the process. Each stage adds its flows here (import, OPDS, readers…).
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task TheServerMakesNoOutboundRequests()
    {
        using var guard = new OutboundGuard();
        using var catalog = new TestCatalog();
        await using (var factory = ApiClients.CreateFactory(catalog))
        {
            using var client = ApiClients.Create(factory);
            foreach (var path in new[] { "/api/system/info", "/openapi/v1.json", "/", "/some/client/route" })
                using (await client.GetAsync(path, TestContext.CancellationToken)) { }
        }

        Assert.IsEmpty(guard.Seen, "Outbound: " + string.Join(", ", guard.Seen));
    }

    /// <summary>The guard above sees a real outbound request: without this, its silence would prove nothing.</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task TheOutboundGuardSeesARequest()
    {
        using var guard = new OutboundGuard();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // Port 9 (discard) on this machine: refused at once, nothing leaves the computer.
        try
        {
            using (await http.GetAsync("http://127.0.0.1:9/", TestContext.CancellationToken)) { }
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException)
        {
        }

        Assert.IsTrue(guard.Seen.Any(line => line.Contains("127.0.0.1:9", StringComparison.Ordinal)), string.Join(", ", guard.Seen));
    }

    /// <summary>
    /// No address of another site in the built client: everything it loads is bundled. Allowed: strings that are never
    /// fetched (XML namespaces, links in licence texts and in Angular's error messages).
    /// </summary>
    [TestMethod]
    public void TheWebClientHasNoExternalUrls()
    {
        var root = RepositoryFiles.WebRoot();
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(f => Path.GetExtension(f) is ".html" or ".js" or ".mjs" or ".css" or ".json" or ".svg" or ".webmanifest"))
        {
            foreach (Match match in ExternalUrl().Matches(File.ReadAllText(file)))
                if (!NeverFetchedExactly.Contains(match.Value)
                    && !NeverFetched.Any(prefix => match.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    found.Add($"{Path.GetRelativePath(root, file)}: {match.Value}");
        }

        Assert.IsEmpty(found, "External addresses in the client:" + Environment.NewLine + string.Join(Environment.NewLine, found.Distinct()));
    }

    private static readonly string[] NeverFetched =
    [
        "http://www.w3.org/",      // XML / SVG / MathML namespaces
        "https://www.w3.org/",
        "https://angular.dev/",    // links printed in Angular's error messages
        "https://g.co/ng/",        // the same, shortened
    ];

    private static readonly HashSet<string> NeverFetchedExactly = new(StringComparer.Ordinal)
    {
        "http://fake",             // Angular's router: a dummy base for new URL(path, base), never requested
    };

    [GeneratedRegex(@"https?://[A-Za-z0-9.\-]+(?::\d+)?[^\s""'`)<>]*")]
    private static partial Regex ExternalUrl();

    /// <summary>
    /// The page runs only scripts from files: the CSP (<c>script-src 'self'</c>) would block inline ones, and an inline
    /// handler (Angular's critical-CSS <c>onload</c>) is the usual way one sneaks in.
    /// </summary>
    [TestMethod]
    public void TheWebClientHasNoInlineScripts()
    {
        var html = File.ReadAllText(Path.Combine(RepositoryFiles.WebRoot(), "index.html"));

        Assert.IsEmpty(InlineScript().Matches(html).Select(m => m.Value).ToList(), "Inline scripts in index.html");
        Assert.IsEmpty(InlineHandler().Matches(html).Select(m => m.Value).ToList(), "Inline handlers in index.html");
    }

    [GeneratedRegex(@"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex InlineHandler();

    /// <summary>The Angular CLI never reports usage (it asks once otherwise, and CI would hang or send).</summary>
    [TestMethod]
    public void TheAngularCliSendsNoAnalytics()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.PathOf("src/Librarion.Web/angular.json")));

        Assert.IsFalse(config.RootElement.GetProperty("cli").GetProperty("analytics").GetBoolean());
    }
}
