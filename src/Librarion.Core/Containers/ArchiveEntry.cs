namespace Librarion.Core.Containers;

public enum ArchiveEntryKind
{
    File = 0,
    Folder = 1,
    /// <summary>A symbolic or hard link: listed, never read.</summary>
    Link = 2,
    /// <summary>A device, a FIFO, a socket: listed, never read.</summary>
    Other = 3,
}

/// <summary>
/// One entry as a zip's central directory lists it, before its name is checked (<see cref="ArchiveEntryNames"/>).
/// Ported from SFE without its ciphers: an encrypted entry is listed and refused when opened.
/// </summary>
/// <param name="Name">The name as written in the archive (any separator, maybe hostile).</param>
/// <param name="Time">The modified time as stored (local time of the machine that made it, or the UTC extra field).</param>
/// <param name="Locator">The offset of the entry's local header (prepended data counted): one seek opens it.</param>
/// <param name="Method">Compression method: 0 stored, 8 deflate, 9 Deflate64, 12 BZip2, 14 LZMA, 93 Zstandard, 95 XZ,
/// 98 PPMd.</param>
/// <param name="Crc">CRC-32 of the data.</param>
public sealed record ArchiveEntry(
    string Name,
    ArchiveEntryKind Kind,
    long Size,
    long Packed,
    DateTime? Time,
    bool Encrypted,
    long Locator,
    int Method,
    uint Crc);
