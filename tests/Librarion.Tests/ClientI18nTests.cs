using System.Text.Json;
using System.Text.RegularExpressions;
using Librarion.Tests.Infrastructure;

namespace Librarion.Tests;

/// <summary>
/// Every text of the web client goes through a translation key (decision D12, agents/docs/development/frontend.md):
/// read from the templates' sources, so it holds without building the client.
/// </summary>
[TestClass]
public sealed partial class ClientI18nTests
{
    private static readonly string AppFolder = RepositoryFiles.PathOf("src/Librarion.Web/src/app");

    [TestMethod]
    public void TemplatesHaveNoLiteralText()
    {
        var found = new List<string>();
        foreach (var (file, template) in Templates())
        {
            foreach (Match node in TextNode().Matches(template))
            {
                var text = ControlFlow().Replace(Interpolation().Replace(node.Groups[1].Value, ""), "");
                if (Letter().IsMatch(text))
                    found.Add($"{file}: \"{text.Trim()}\"");
            }

            found.AddRange(LiteralAttribute().Matches(template).Select(m => $"{file}: {m.Value.Trim()}"));
        }

        Assert.IsEmpty(found, "Texts not going through a key ('key' | t):" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    [TestMethod]
    public void EveryKeyUsedInATemplateExists()
    {
        using var english = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.PathOf("src/Librarion.Web/src/i18n/en.json")));
        var keys = english.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var missing = Templates()
            .SelectMany(t => KeyLike().Matches(t.Template).Select(m => (t.File, Key: m.Groups[1].Value)))
            .Where(use => !keys.Contains(use.Key))
            .Select(use => $"{use.File}: {use.Key}")
            .Distinct()
            .ToList();

        Assert.IsNotEmpty(keys);
        Assert.IsEmpty(missing, "Keys missing from src/i18n/en.json:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    /// <summary>The test reads the templates it is meant to (inline and in files).</summary>
    [TestMethod]
    public void TheTemplatesAreFound()
    {
        var files = Templates().Select(t => t.File).ToList();

        CollectionAssert.Contains(files, Path.Combine("shell", "shell.html"));
        CollectionAssert.Contains(files, Path.Combine("home", "home-page.ts"));
    }

    private static IEnumerable<(string File, string Template)> Templates()
    {
        foreach (var path in Directory.EnumerateFiles(AppFolder, "*.html", SearchOption.AllDirectories))
            yield return (Path.GetRelativePath(AppFolder, path), File.ReadAllText(path));
        foreach (var path in Directory.EnumerateFiles(AppFolder, "*.ts", SearchOption.AllDirectories).Where(p => !p.EndsWith(".spec.ts", StringComparison.Ordinal)))
            foreach (Match match in InlineTemplate().Matches(File.ReadAllText(path)))
                yield return (Path.GetRelativePath(AppFolder, path), match.Groups[1].Value);
    }

    [GeneratedRegex(@"template:\s*`([^`]*)`")]
    private static partial Regex InlineTemplate();

    [GeneratedRegex(@">([^<>]+)<")]
    private static partial Regex TextNode();

    [GeneratedRegex(@"\{\{.*?\}\}", RegexOptions.Singleline)]
    private static partial Regex Interpolation();

    /// <summary><c>@if (…) {</c>, <c>} @else {</c>, <c>@for (…) {</c>, closing braces.</summary>
    [GeneratedRegex(@"@\w+[^{]*\{|\}")]
    private static partial Regex ControlFlow();

    [GeneratedRegex(@"\p{L}")]
    private static partial Regex Letter();

    /// <summary>Unbound attributes people read (<c>[title]="…"</c> is bound and starts with <c>[</c>).</summary>
    [GeneratedRegex(@"(?<![\[\w.-])(title|placeholder|aria-label|alt)=""[^""]*\p{L}[^""]*""")]
    private static partial Regex LiteralAttribute();

    /// <summary>Quoted dotted names in templates (<c>'shell.menu.open'</c>): keys, also inside conditions.</summary>
    [GeneratedRegex(@"'([a-z][a-zA-Z]*(?:\.[a-zA-Z]+)+)'")]
    private static partial Regex KeyLike();
}
