namespace Librarion.Core;

/// <summary>
/// Where the server keeps files that are data, not cache (<c>Librarion:Data</c>; environment
/// <c>LIBRARION__DATA__DIRECTORY</c>): uploaded covers now, each kind in a folder of its own under
/// <see cref="Directory"/>. Unlike the cache it can't be made again: back it up with the database.
/// </summary>
public sealed class DataOptions
{
    public const string SectionName = "Librarion:Data";

    /// <summary>The data folder, absolute or relative to the application.</summary>
    public string Directory { get; set; } = "data";

    /// <summary>The full path of the data's folder <paramref name="name"/> (<c>covers</c>…).</summary>
    public string PathOf(string contentRoot, string name) => Path.GetFullPath(Path.Combine(Directory, name), contentRoot);
}
