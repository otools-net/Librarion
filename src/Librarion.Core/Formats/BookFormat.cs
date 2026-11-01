using Librarion.Core.Import;

namespace Librarion.Core.Formats;

/// <summary>
/// Everything Librarion knows about one book format, in one place (agents/docs/features/book-formats.md): its key
/// (stored on each file), the file extensions that mean it, its media type, and what can be read from it. A format
/// known only by name is an instance of this class; one with metadata or a cover overrides what it can read. A new
/// format is one class (or one line) added to <see cref="BookFormats.All"/>.
/// </summary>
/// <param name="key">Lower case, stored in <c>BookFiles.Format</c>: never renamed once released.</param>
/// <param name="extensions">With their dot; a compound one (<c>.kepub.epub</c>) wins over its last part.</param>
public class BookFormat(string key, string contentType, params string[] extensions)
{
    public string Key { get; } = key;
    public string ContentType { get; } = contentType;
    public IReadOnlyList<string> Extensions { get; } = extensions;

    /// <summary>Whether books of this format can carry a cover of their own (<see cref="ExtractCover"/>).</summary>
    public virtual bool HasCovers => false;

    /// <summary>
    /// The metadata from the book's head (bounded: never the whole book), or null for none. <paramref name="open"/>
    /// gives the bytes from the start, as often as asked (a zip entry: not seekable).
    /// </summary>
    public virtual BookMetadata? ReadMetadata(Func<Stream> open, long size, CancellationToken cancellationToken) => null;

    /// <summary>The cover inside the book, still encoded (JPEG, PNG…), or null; at most <see cref="Covers.CoverExtractor.MaxCoverBytes"/>.</summary>
    public virtual byte[]? ExtractCover(Func<Stream> open, long size, CancellationToken cancellationToken) => null;
}
