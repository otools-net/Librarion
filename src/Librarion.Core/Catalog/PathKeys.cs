using System.Text;

namespace Librarion.Core.Catalog;

/// <summary>
/// The 64-bit keys of <see cref="Data.BookFile.PathKey"/> and <see cref="Data.Archive.PathKey"/>: paths are too long to
/// index, so their hash is (FNV-1a over the UTF-8 bytes, stable across runs and machines). A key finds candidates; the
/// path itself is compared after.
/// </summary>
public static class PathKeys
{
    public static long Of(string path)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in path.EnumerateRunes())
        {
            var length = rune.EncodeToUtf8(buffer);
            for (var i = 0; i < length; i++)
            {
                hash ^= buffer[i];
                hash *= prime;
            }
        }

        return unchecked((long)hash);
    }

    /// <summary>The same hash over several parts (a blocking key: normalized title and author), separated by a zero.</summary>
    public static long Of(params ReadOnlySpan<string> parts)
    {
        var text = new StringBuilder();
        foreach (var part in parts)
            text.Append(part).Append('\0');
        return Of(text.ToString());
    }
}
