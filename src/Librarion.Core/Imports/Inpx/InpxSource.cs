using System.Globalization;
using System.Text;
using Librarion.Core.Containers;
using Librarion.Core.Data;
using Librarion.Core.Formats;
using Librarion.Core.Import;
using Librarion.Core.Import.Metadata;

namespace Librarion.Core.Imports.Inpx;

/// <summary>An INPX catalog (MyHomeLib, the Lib.rus.ec family) and the zips of books next to it (agents/docs/features/imports.md#inpx).</summary>
public sealed class InpxFormat : ImportFormat
{
    public override string Key => "inpx";

    public override string? Check(string path)
    {
        try
        {
            using var source = new InpxSource(path, Path.GetDirectoryName(path)!);
            return source.Problem();
        }
        catch (Exception ex) when (ex is ArchiveException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    public override IImportSource Open(string path, string filesFolder) => new InpxSource(path, filesFolder);
}

/// <summary>
/// Reads an INPX file (a zip of <c>.inp</c> files; layout learnt from MyHomeLib, MIT): each line a book, its fields
/// separated by <c>\x04</c> in the order <c>structure.info</c> gives (else the Lib.rus.ec default); authors
/// <c>last,first,middle:</c>, genres <c>code:</c>; the book is the entry <c>FILE.EXT</c> of the zip named by
/// <c>FOLDER</c>, else of the zip named as its <c>.inp</c> (<c>fb2-000024-030559.inp</c> ↔ <c>….zip</c>), in the
/// files' folder. Deleted records (<c>DEL</c> = 1) are skipped and counted; a broken line is reported, never fatal.
/// </summary>
public sealed class InpxSource : IImportSource
{
    /// <summary>The fields of Lib.rus.ec's catalogs, when <c>structure.info</c> doesn't say.</summary>
    public const string DefaultStructure = "AUTHOR;GENRE;TITLE;SERIES;SERNO;FILE;SIZE;LIBID;DEL;EXT;DATE;LANG;LIBRATE;KEYWORDS";

    private const int MaxLine = 64 * 1024;
    private const int MaxInfo = 64 * 1024;
    private const int MaxKeywords = 20;

    private static readonly Encoding Utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
    private static readonly Encoding Cyrillic;

    private readonly FileStream stream;
    private readonly ZipDirectory directory;
    private readonly List<ArchiveEntry> entries;
    private readonly string folder;

    static InpxSource()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cyrillic = Encoding.GetEncoding(1251);
    }

    public InpxSource(string path, string filesFolder)
    {
        folder = filesFolder;
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024);
        try
        {
            directory = ZipDirectory.Locate(stream);
            entries = directory.Read(stream, CancellationToken.None).Where(e => e.Kind == ArchiveEntryKind.File && !e.Encrypted).ToList();
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        var collection = Info("collection.info")?.Split('\n', 2)[0].Trim();
        Collection = string.IsNullOrEmpty(collection) ? Path.GetFileNameWithoutExtension(path) : collection;
    }

    public MetadataSource Owner => MetadataSource.Inpx;
    public string IdScheme => "inpx";

    /// <summary>The collection's name (first line of <c>collection.info</c>, else the file's name): part of each book's source id.</summary>
    public string Collection { get; }

    /// <summary>Null when the zip holds <c>.inp</c> files; else why it isn't an INPX.</summary>
    public string? Problem() => Inps().Any() ? null : "no .inp file inside: not an INPX catalog";

    public IEnumerable<ImportRecord> Read(ImportReport report, CancellationToken cancellationToken)
    {
        var fields = Fields(Info("structure.info") ?? DefaultStructure);
        foreach (var inp in Inps())
        {
            var archive = Path.ChangeExtension(inp.Name.Replace('\\', '/'), ".zip");
            var number = 0;
            foreach (var line in Lines(inp, report, cancellationToken))
            {
                number++;
                if (line.Length == 0)
                    continue;
                ImportRecord? record;
                try
                {
                    record = Record(line, fields, archive, report, $"{inp.Name}:{number}");
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
                {
                    report.Skip($"{inp.Name}:{number}: {ex.Message}");
                    continue;
                }

                if (record is not null)
                    yield return record;
            }
        }
    }

    private enum Field
    {
        Other,
        Author,
        Genre,
        Title,
        Series,
        SerNo,
        File,
        Ext,
        LibId,
        Deleted,
        Lang,
        LibRate,
        Keywords,
        Folder,
    }

    private static Field[] Fields(string structure) => structure.Trim().Split(';').Select(f => f.Trim().ToUpperInvariant() switch
    {
        "AUTHOR" => Field.Author,
        "GENRE" => Field.Genre,
        "TITLE" => Field.Title,
        "SERIES" => Field.Series,
        "SERNO" => Field.SerNo,
        "FILE" => Field.File,
        "EXT" => Field.Ext,
        "LIBID" => Field.LibId,
        "DEL" => Field.Deleted,
        "LANG" => Field.Lang,
        "LIBRATE" => Field.LibRate,
        "KEYWORDS" => Field.Keywords,
        "FOLDER" => Field.Folder,
        _ => Field.Other,
    }).ToArray();

    private ImportRecord? Record(string line, Field[] fields, string archive, ImportReport report, string where)
    {
        var parts = line.Split('\x04');
        string? title = null, series = null, file = null, ext = null, libId = null, folderName = null, language = null;
        double? number = null;
        byte? rating = null;
        var values = new List<FieldValue>();
        var tags = new List<string>();
        for (var i = 0; i < Math.Min(parts.Length, fields.Length); i++)
        {
            var text = parts[i].Trim();
            switch (fields[i])
            {
                case Field.Author:
                    foreach (var item in text.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (Author(item) is var (person, sort))
                            values.Add(new FieldValue("author", person, values.Count(v => v.Field == "author"), SortName: sort));
                    break;
                case Field.Genre:
                    foreach (var code in text.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        values.Add(new FieldValue("category", Fb2Genres.Name(code)));
                    break;
                case Field.Title: title = text; break;
                case Field.Series: series = text; break;
                case Field.SerNo: number = Fb2Reader.Number(text) is > 0 and var n ? n : null; break;
                case Field.File: file = text; break;
                case Field.Ext: ext = text.TrimStart('.'); break;
                case Field.LibId: libId = text; break;
                case Field.Deleted:
                    // As MyHomeLib: "1" (or anything sorting after it) marks the book deleted from the library.
                    if (string.CompareOrdinal(text, "1") >= 0)
                    {
                        report.Skip();
                        return null;
                    }

                    break;
                case Field.Lang: language = Languages.Normalize(text); break;
                case Field.LibRate:
                    if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var stars) && stars is > 0 and <= 5)
                        rating = (byte)(stars * 2);
                    break;
                case Field.Keywords:
                    tags.AddRange(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxKeywords));
                    break;
                case Field.Folder: folderName = text; break;
            }
        }

        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(ext))
        {
            report.Skip($"{where}: no file name");
            return null;
        }

        var name = $"{file}.{ext}";
        if (BookFormats.Of(name) is null)
        {
            report.Skip($"{where}: {name} is not a book format Librarion knows");
            return null;
        }

        if (series is { Length: > 0 })
            values.Add(new FieldValue("series", series, Number: number));
        if (language is not null)
            values.Add(new FieldValue("language", language));
        var zip = string.IsNullOrEmpty(folderName) ? archive : folderName.Replace('\\', '/');
        var identifiers = new List<(string, string)>();
        if (libId is { Length: > 0 })
            identifiers.Add(("libid", libId));
        var metadata = new BookMetadata(ImportTagSourceKind.Inpx, string.IsNullOrEmpty(title) ? null : title, Values: values, Tags: tags, Identifiers: identifiers,
            Rating: rating);
        var sourceId = libId is { Length: > 0 } ? $"{Collection}:{libId}" : $"{Collection}:{zip}/{name}";
        return new ImportRecord(sourceId, metadata, [new ImportFile(Path.Combine(folder, zip.Replace('/', Path.DirectorySeparatorChar)), name)]);
    }

    /// <summary><c>Last,First,Middle</c> as a name ("First Middle Last") and its sort name ("Last, First Middle").</summary>
    private static (string Name, string Sort)? Author(string item)
    {
        var parts = item.Split(',', 3, StringSplitOptions.TrimEntries);
        var last = parts[0];
        var given = string.Join(' ', parts.Skip(1).Where(p => p.Length > 0));
        if (last.Length == 0 && given.Length == 0)
            return null;
        if (last.Length == 0)
            return (given, given);
        return given.Length == 0 ? (last, last) : ($"{given} {last}", $"{last}, {given}");
    }

    /// <summary>The <c>.inp</c> files in their order in the zip; <c>extra.inp</c> lists an online collection's books that aren't local.</summary>
    private IEnumerable<ArchiveEntry> Inps() => entries.Where(e => e.Name.EndsWith(".inp", StringComparison.OrdinalIgnoreCase)
        && !e.Name.Equals("extra.inp", StringComparison.OrdinalIgnoreCase));

    /// <summary>The lines of an <c>.inp</c>: UTF-8, or windows-1251 for an older file that isn't.</summary>
    private IEnumerable<string> Lines(ArchiveEntry inp, ImportReport report, CancellationToken cancellationToken)
    {
        var encoding = Utf8;
        try
        {
            using var check = directory.OpenEntry(stream, inp, leaveOpen: true);
            using var reader = new StreamReader(check, Utf8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
            var buffer = new char[64 * 1024];
            while (reader.Read(buffer, 0, buffer.Length) > 0)
                cancellationToken.ThrowIfCancellationRequested();
        }
        catch (DecoderFallbackException)
        {
            encoding = Cyrillic;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArchiveException)
        {
            report.Problem($"{inp.Name}: {ex.Message}");
            yield break;
        }

        using var data = directory.OpenEntry(stream, inp, leaveOpen: true);
        using var lines = new StreamReader(data, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line;
            try
            {
                line = lines.ReadLine();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                report.Problem($"{inp.Name}: {ex.Message}");
                yield break;
            }

            if (line is null)
                yield break;
            yield return line.Length > MaxLine ? "" : line.TrimEnd('\r');
        }
    }

    /// <summary>A small text entry of the INPX (<c>structure.info</c>, <c>collection.info</c>); null when there is none.</summary>
    private string? Info(string name)
    {
        var entry = entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Size > MaxInfo)
            return null;
        using var data = directory.OpenEntry(stream, entry, leaveOpen: true);
        using var memory = new MemoryStream();
        data.CopyTo(memory);
        var bytes = memory.ToArray();
        try
        {
            return Utf8.GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Cyrillic.GetString(bytes);
        }
    }

    public void Dispose() => stream.Dispose();
}
