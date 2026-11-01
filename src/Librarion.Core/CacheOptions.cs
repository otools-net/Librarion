namespace Librarion.Core;

/// <summary>
/// Where the server keeps its cache files (<c>Librarion:Cache</c>; environment <c>LIBRARION__CACHE__DIRECTORY</c>):
/// covers now, extracted books later, each in a folder of its own under <see cref="Directory"/>. Everything in it can
/// be deleted: it is made again when needed.
/// </summary>
public sealed class CacheOptions
{
    public const string SectionName = "Librarion:Cache";

    /// <summary>The cache folder, absolute or relative to the application.</summary>
    public string Directory { get; set; } = "data/cache";

    /// <summary>The full path of the cache's folder <paramref name="name"/> (<c>covers</c>…).</summary>
    public string PathOf(string contentRoot, string name) => Path.GetFullPath(Path.Combine(Directory, name), contentRoot);
}
