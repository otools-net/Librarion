namespace Librarion.Core.Covers;

/// <summary>
/// Uploaded covers on disk (stage 6 chunk 10, the user's answers 2026-11-06): in the data folder
/// (<see cref="DataOptions"/>), <c>covers/&lt;tenant&gt;/ab/&lt;hash&gt;.jpg</c>, each made again from what was sent
/// (<see cref="CoverImages.Remake"/>) and named by its content, so the same picture uploaded twice is one file. A book
/// keeps the path relative to the folder (<see cref="Data.Book.CoverFile"/>).
/// </summary>
public sealed class CoverUploads(string directory)
{
    /// <summary>The largest upload accepted.</summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>The widest cover kept: more than the book page's (600 px), room for a reader app's large cover.</summary>
    public const int MaxWidth = 1200;

    public string Directory { get; } = directory;

    /// <summary>The picture checked and made again, written; its path relative to the folder, or null when it isn't a cover Librarion takes.</summary>
    public string? Store(int tenantId, byte[] source)
    {
        if (CoverImages.Remake(source, MaxWidth, MaxBytes) is not { } jpeg)
            return null;
        var hash = System.IO.Hashing.XxHash64.HashToUInt64(jpeg).ToString("x16");
        var relative = $"{tenantId}/{hash[..2]}/{hash}{CoverImages.Extension}";
        var path = FullPath(relative);
        if (File.Exists(path))
            return relative;
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, jpeg);
        try
        {
            File.Move(temp, path, overwrite: false);
        }
        catch (IOException)
        {
            // The same picture written meanwhile: it is the same file.
            File.Delete(temp);
        }

        return relative;
    }

    public string FullPath(string relative) => Path.GetFullPath(Path.Combine(Directory, relative));

    /// <summary>The hash a stored cover is named by (its cache key's part).</summary>
    public static string HashOf(string relative) => Path.GetFileNameWithoutExtension(relative);

    public void Delete(string relative)
    {
        var path = FullPath(relative);
        if (File.Exists(path))
            File.Delete(path);
    }
}
