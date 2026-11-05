using System.Text.Json;
using System.Text.RegularExpressions;
using Librarion.Api.Endpoints;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Settings;
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
        CollectionAssert.Contains(files, Path.Combine("browse", "browse-page.ts"));
    }

    /// <summary>
    /// What the server names and the client shows by a key built at run time: rights, right areas, settings and their
    /// choices and hints, setting levels, built-in groups, security event kinds, built-in fields, and the error codes the access services send.
    /// </summary>
    [TestMethod]
    public void EveryNameTheServerSendsHasItsText()
    {
        using var english = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.PathOf("src/Librarion.Web/src/i18n/en.json")));
        var keys = english.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

        var wanted = new List<string>();
        foreach (var right in RightCatalog.All)
            wanted.AddRange([$"right.{right.Id}", $"right.{right.Id}.hint", $"rightArea.{right.Area}"]);
        foreach (var setting in SettingCatalog.All)
        {
            wanted.AddRange([$"setting.{setting.Key}", $"setting.{setting.Key}.hint", $"settingSection.{setting.Section}"]);
            wanted.AddRange((setting.Choices ?? []).Select(choice => $"setting.{setting.Key}.{choice}"));
        }
        wanted.AddRange(Enum.GetNames<SettingLevel>().Select(level => $"settingLevel.{Camel(level)}"));
        wanted.AddRange(Enum.GetNames<BuiltInGroup>().Select(group => $"group.{Camel(group)}"));
        wanted.AddRange(Enum.GetNames<SecurityEventKind>().Select(kind => $"securityEvent.{Camel(kind)}"));
        wanted.AddRange(Enum.GetNames<BackgroundTaskKind>().Select(kind => $"task.{Camel(kind)}"));
        wanted.AddRange(Librarion.Core.Tagging.BuiltInTags.Fields.Values.SelectMany(key => new[] { $"fields.name.{key}", $"fields.one.{key}" }));
        wanted.AddRange(new[] { SessionEndpoints.SignInFailedType, SessionEndpoints.LockedOutType, RequestGuard.SetupRequiredType }.Select(type => $"error.{type}"));

        var named = RightCatalog.All.Select(r => r.Id).Concat(SettingCatalog.All.Select(s => s.Key)).ToHashSet(StringComparer.Ordinal);
        foreach (var source in new[] { "Access/AccessService.cs", "Access/SetupService.cs", "Access/AccountRecovery.cs", "Settings/SettingsService.cs", "Settings/SettingCatalog.cs",
                     "Tagging/TagService.cs", "Merging/TagMerger.cs", "Catalog/BookEditor.cs", "Tagging/BookTagger.cs" })
        {
            var text = File.ReadAllText(RepositoryFiles.PathOf("src/Librarion.Core/" + source));
            wanted.AddRange(ErrorCode().Matches(text).Select(m => m.Groups[1].Value).Where(code => !named.Contains(code)).Select(code => $"error.{code}"));
        }

        foreach (var endpoints in new[] { "TaskEndpoints.cs", "BookEndpoints.cs" })
            wanted.AddRange(ErrorCode().Matches(File.ReadAllText(RepositoryFiles.PathOf($"src/Librarion.Api/Endpoints/{endpoints}"))).Select(m => $"error.{m.Groups[1].Value}"));

        var kinds = File.ReadAllText(Path.Combine(AppFolder, "admin", "security-section.ts"));
        var missing = wanted.Distinct().Where(key => !keys.Contains(key)).ToList();
        missing.AddRange(Enum.GetNames<SecurityEventKind>().Select(Camel).Where(kind => !kinds.Contains($"'{kind}'", StringComparison.Ordinal))
            .Select(kind => $"SECURITY_EVENT_KINDS lacks {kind}"));

        Assert.IsGreaterThan(80, wanted.Count, "The names were found");
        Assert.IsEmpty(missing, "Missing from src/i18n/en.json:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
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

    /// <summary><c>@let x = …;</c>, <c>@if (…) {</c>, <c>} @else {</c>, <c>@for (…) {</c>, closing braces.</summary>
    [GeneratedRegex(@"@let\s[^;]*;|@\w+[^{]*\{|\}")]
    private static partial Regex ControlFlow();

    [GeneratedRegex(@"\p{L}")]
    private static partial Regex Letter();

    /// <summary>Unbound attributes people read (<c>[title]="…"</c> is bound and starts with <c>[</c>).</summary>
    [GeneratedRegex(@"(?<![\[\w.-])(title|placeholder|aria-label|alt)=""[^""]*\p{L}[^""]*""")]
    private static partial Regex LiteralAttribute();

    /// <summary>Quoted dotted names in templates (<c>'shell.menu.open'</c>): keys, also inside conditions.</summary>
    [GeneratedRegex(@"'([a-z][a-zA-Z]*(?:\.[a-zA-Z]+)+)'")]
    private static partial Regex KeyLike();

    /// <summary>String literals shaped like <c>"userName.taken"</c> in the access services: the codes they answer with.</summary>
    [GeneratedRegex(@"""([a-z][a-zA-Z]*\.[a-z][a-zA-Z]*)""")]
    private static partial Regex ErrorCode();
}
