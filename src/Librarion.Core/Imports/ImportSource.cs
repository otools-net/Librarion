using Librarion.Core.Data;
using Librarion.Core.Import;

namespace Librarion.Core.Imports;

/// <summary>A file of an imported book: a loose file, or <paramref name="Entry"/> of the ZIP container at <paramref name="Path"/>.</summary>
/// <param name="Path">Absolute.</param>
public sealed record ImportFile(string Path, string? Entry = null);

/// <summary>A book as a source gives it (agents/docs/features/imports.md).</summary>
/// <param name="SourceId">The source's own id of the book (Calibre's uuid, an INPX record's collection and id): a re-import
/// finds the book by it when none of its files is known, kept as an identifier of <see cref="IImportSource.IdScheme"/>.</param>
/// <param name="Files">Its files, formats or copies of one book.</param>
/// <param name="Cover">The source has a cover picture of it (Calibre's <c>cover.jpg</c>, next to its files).</param>
public sealed record ImportRecord(string SourceId, BookMetadata Metadata, IReadOnlyList<ImportFile> Files, bool Cover = false);

/// <summary>What a source couldn't use, told to the run: records skipped, and the first problems.</summary>
public sealed class ImportReport
{
    public const int MaxProblems = 100;
    private readonly List<string> problems = [];

    public IReadOnlyList<string> Problems => problems;

    /// <summary>Records not imported (deleted in the source, broken, without a file).</summary>
    public int Skipped { get; private set; }

    public void Problem(string problem)
    {
        if (problems.Count < MaxProblems)
            problems.Add(problem);
    }

    /// <summary>A record left out; <paramref name="problem"/> listed when it says why.</summary>
    public void Skip(string? problem = null)
    {
        Skipped++;
        if (problem is not null)
            Problem(problem);
    }
}

/// <summary>
/// An opened source of an import (a Calibre library, an INPX catalog): read once, front to back, by
/// <see cref="CatalogImporter"/>. One class per source; a new source is one more (agents/docs/features/imports.md).
/// </summary>
public interface IImportSource : IDisposable
{
    /// <summary>Who owns the books it writes.</summary>
    MetadataSource Owner { get; }

    /// <summary>The identifier scheme of <see cref="ImportRecord.SourceId"/> (<c>calibre-uuid</c>, <c>inpx</c>).</summary>
    string IdScheme { get; }

    /// <summary>The books, in the source's order; what can't be read is told to <paramref name="report"/>, never thrown.</summary>
    IEnumerable<ImportRecord> Read(ImportReport report, CancellationToken cancellationToken);
}

/// <summary>A kind of source an administrator can import from: its key, a check of the path given, and how to open it.</summary>
public abstract class ImportFormat
{
    /// <summary>Lower case: <c>calibre</c>, <c>inpx</c>.</summary>
    public abstract string Key { get; }

    /// <summary>Null when <paramref name="path"/> (an existing file) is a source of this kind; else why not, as a message.</summary>
    public abstract string? Check(string path);

    public abstract IImportSource Open(string path);
}

/// <summary>The kinds of source known, by key (registered in the services: a test adds its own).</summary>
public sealed class ImportFormats(IEnumerable<ImportFormat> formats)
{
    private readonly Dictionary<string, ImportFormat> byKey = formats.ToDictionary(f => f.Key, StringComparer.Ordinal);

    public IReadOnlyCollection<string> Keys => byKey.Keys;

    public ImportFormat? Find(string? key) => key is null ? null : byKey.GetValueOrDefault(key);
}
