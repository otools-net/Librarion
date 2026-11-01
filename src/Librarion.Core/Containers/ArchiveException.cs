namespace Librarion.Core.Containers;

/// <summary>
/// An archive that can't be read as asked: broken, split, encrypted, past a limit, or using a method Librarion doesn't
/// decompress. The message is for the person (no path, nothing of the server). Ported from SFE.
/// </summary>
public sealed class ArchiveException(string message, Exception? inner = null) : IOException(message, inner);
