using System.Globalization;
using System.Text.RegularExpressions;
using Librarion.Tests.Infrastructure;

namespace Librarion.Tests;

/// <summary>
/// The client's design tokens (agents/docs/features/design.md): every text colour reads at WCAG AA (4.5:1) on the
/// surfaces it is used on, the focus ring and strong borders at 3:1, in both themes.
/// </summary>
[TestClass]
public sealed partial class ClientDesignTests
{
    private static readonly string Tokens = File.ReadAllText(RepositoryFiles.PathOf("src/Librarion.Web/src/styles/tokens.css"));

    /// <summary>Text colours and the backgrounds they appear on.</summary>
    private static readonly (string Text, string[] On, double Minimum)[] Pairs =
    [
        ("--text", ["--bg", "--surface", "--surface-2", "--surface-3"], 4.5),
        ("--text-muted", ["--bg", "--surface", "--surface-2"], 4.5),
        ("--accent", ["--bg", "--surface", "--surface-2"], 4.5),
        ("--danger", ["--bg", "--surface", "--surface-2"], 4.5),
        ("--success", ["--bg", "--surface"], 4.5),
        ("--warning", ["--bg", "--surface"], 4.5),
        ("--accent-text", ["--accent"], 4.5),
        ("--danger-text", ["--danger"], 4.5),
        ("--focus", ["--bg", "--surface", "--surface-2"], 3),
        ("--border-strong", ["--surface"], 3),
    ];

    [TestMethod]
    [DataRow("light")]
    [DataRow("dark")]
    public void EveryTextColourReadsOnItsSurfaces(string theme)
    {
        var colours = Theme(theme);
        var failures = new List<string>();
        foreach (var (text, backgrounds, minimum) in Pairs)
            foreach (var background in backgrounds)
            {
                var ratio = Contrast(colours[text], colours[background]);
                if (ratio < minimum)
                    failures.Add($"{text} on {background}: {ratio:0.00} (needs {minimum})");
            }

        Assert.IsEmpty(failures, $"{theme}: " + string.Join("; ", failures));
    }

    [TestMethod]
    public void BothThemesDefineTheSameColours()
    {
        CollectionAssert.AreEquivalent(Theme("light").Keys.ToList(), Theme("dark").Keys.ToList());
    }

    /// <summary>The hex colours (<c>#rrggbb</c>) of a theme's block.</summary>
    private static Dictionary<string, (double R, double G, double B)> Theme(string theme)
    {
        var block = Regex.Match(Tokens, $@":root\[data-theme=""{theme}""\]\s*\{{(?<body>[^}}]*)\}}").Groups["body"].Value;
        Assert.IsFalse(string.IsNullOrEmpty(block), $"no {theme} block");
        return HexColour().Matches(block).ToDictionary(m => m.Groups["name"].Value, m =>
        {
            var hex = m.Groups["hex"].Value;
            double Channel(int at) => int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return (Channel(0), Channel(2), Channel(4));
        });
    }

    private static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var (light, dark) = (Luminance(a), Luminance(b));
        if (light < dark)
            (light, dark) = (dark, light);
        return (light + 0.05) / (dark + 0.05);
    }

    private static double Luminance((double R, double G, double B) c)
    {
        static double Linear(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    [GeneratedRegex(@"(?<name>--[a-z0-9-]+):\s*#(?<hex>[0-9a-fA-F]{6})\s*;")]
    private static partial Regex HexColour();
}
