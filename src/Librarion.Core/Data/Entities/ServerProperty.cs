namespace Librarion.Core.Data;

/// <summary>A value of the whole install, by name: the installed version first (<see cref="ServerProperties.Version"/>).</summary>
public sealed class ServerProperty
{
    public const int MaxKeyLength = 100;

    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }
}

/// <summary>Names of the <see cref="ServerProperty"/> rows.</summary>
public static class ServerProperties
{
    /// <summary>The version that last brought the database up (written at each start).</summary>
    public const string Version = "Version";
}
