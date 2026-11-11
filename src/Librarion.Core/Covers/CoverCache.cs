namespace Librarion.Core.Covers;

/// <summary>How covers are made (<c>Librarion:Covers</c>); they are cached in the <c>covers</c> folder of the cache (<see cref="CacheOptions"/>).</summary>
public sealed class CoverOptions
{
    public const string SectionName = "Librarion:Covers";

    /// <summary>Covers made at once (each decodes or draws a picture); more requests wait.</summary>
    public int MaxParallel { get; set; } = Math.Max(2, Environment.ProcessorCount / 2);
}

/// <summary>
/// Covers on disk, sharded as SFE's thumbnail cache (<c>ab/cd/&lt;key&gt;-&lt;size&gt;.jpg</c>): a key is a hash of what
/// the picture is made from, so a changed book gets a new key and the old file is simply never asked for again.
/// Written to a temp name and moved, so a reader never sees half a file.
/// </summary>
public sealed class CoverCache(string directory)
{
    public string Directory { get; } = directory;

    public string PathOf(string key, CoverSize size) =>
        System.IO.Path.Combine(Directory, key[..2], key[2..4], $"{key}-{size.ToString().ToLowerInvariant()}{CoverImages.Extension}");

    /// <summary>
    /// A cover's entity tag: its key, size and format (<c>.jpg</c> since stage 7, so a browser's cached WebP of the same
    /// key isn't taken for it).
    /// </summary>
    public static string ETag(string key, CoverSize size) => $"\"{key}-{size}{CoverImages.Extension}\"";

    public bool Has(string key, CoverSize size) => File.Exists(PathOf(key, size));

    public void Write(string key, CoverSize size, byte[] bytes)
    {
        var path = PathOf(key, size);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, bytes);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
            // Another request wrote the same picture first: theirs is as good.
            File.Delete(temp);
        }
    }

    /// <summary>A key from what a cover is made of: 16 hex digits.</summary>
    public static string Key(string source) =>
        System.IO.Hashing.XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(source)).ToString("x16");
}
