using System.Reflection;

namespace Librarion.Api;

/// <summary>The running version (<c>Directory.Build.props</c>) and the commit the SDK appended to it (<c>0.1.0+sha</c>).</summary>
public static class AppVersion
{
    private static readonly string Informational =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string Version { get; } = Informational.Split('+')[0];

    public static string? Commit { get; } = Informational.Split('+') is [_, var commit, ..] ? commit : null;
}
