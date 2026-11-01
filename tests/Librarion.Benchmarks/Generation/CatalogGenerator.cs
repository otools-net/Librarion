using System.Globalization;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Tagging;

namespace Librarion.Benchmarks.Generation;

/// <summary>Ids of the tenant's rows the generated catalog hangs on.</summary>
public sealed record CatalogTarget(int TenantId, int LibraryId, int FirstTagId, IReadOnlyDictionary<BuiltInTag, int> Fields);

/// <summary>
/// A real-shaped catalog as rows, deterministic for a seed (agents/tools/benchmark/README.md): each
/// book is planned from its id alone (<see cref="Plan"/>), so every table is streamed in its own pass without holding
/// millions of books in memory. Planted on purpose: books without a title, duplicates of three kinds (same file, same
/// identifier, same work), and filter tags on 0.1 %, 1 % and 10 % of the books for the narrow allow-lists of D22.
/// </summary>
public sealed class CatalogGenerator
{
    private static readonly DateTime Start = new(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly CatalogShape _shape;
    private readonly CatalogTarget _target;
    private readonly Zipf _authors;
    private readonly Zipf _series;
    private readonly Zipf _plainTags;
    private readonly Zipf _publishers;
    private readonly Zipf _categories;
    private readonly Zipf _languages;

    public CatalogGenerator(CatalogShape shape, CatalogTarget target)
    {
        _shape = shape;
        _target = target;
        _authors = new Zipf(shape.Authors, 0.8);
        _series = new Zipf(shape.Series, 0.7);
        _plainTags = new Zipf(shape.PlainTags, 1.0);
        _publishers = new Zipf(shape.Publishers, 1.0);
        _categories = new Zipf(shape.Categories, 0.9);
        _languages = new Zipf(shape.Languages, 1.5);
    }

    // Tag ids, in blocks after the tenant's existing tags.
    public int AuthorTag(int i) => _target.FirstTagId + i;
    public int SeriesTag(int i) => AuthorTag(_shape.Authors) + i;
    public int CategoryTag(int i) => SeriesTag(_shape.Series) + i;
    public int LanguageTag(int i) => CategoryTag(_shape.Categories) + i;
    public int PublisherTag(int i) => LanguageTag(_shape.Languages) + i;
    public int YearTag(int i) => PublisherTag(_shape.Publishers) + i;
    public int PlainTag(int i) => YearTag(CatalogShape.Years) + i;
    public int FilterTag(int i) => PlainTag(_shape.PlainTags) + i;
    public int LastTagId => FilterTag(CatalogShape.FilterShares.Length) - 1;

    public static readonly string[] TagColumns = ["Id", "TenantId", "Name", "NormalizedName", "SortName", "IsCategory", "IsHidden", "FieldId"];

    public IEnumerable<object?[]> Tags()
    {
        object?[] Row(int id, string name, string sortName, int? field, bool category = false) =>
            [id, _target.TenantId, name, TagNames.Normalize(name), sortName, category, false, field];

        for (var i = 0; i < _shape.Authors; i++)
        {
            var name = Names.Author(i);
            yield return Row(AuthorTag(i), name, TagNames.PersonSortName(name), _target.Fields[BuiltInTag.Author]);
        }

        for (var i = 0; i < _shape.Series; i++)
        {
            var name = Names.Series(i);
            yield return Row(SeriesTag(i), name, name, _target.Fields[BuiltInTag.Series]);
        }

        for (var i = 0; i < _shape.Categories; i++)
        {
            var (name, parent) = Names.Category(i);
            yield return Row(CategoryTag(i), name, name, _target.Fields[BuiltInTag.Category], category: parent is null);
        }

        for (var i = 0; i < _shape.Languages; i++)
            yield return Row(LanguageTag(i), Names.Languages[i], Names.Languages[i], _target.Fields[BuiltInTag.Language]);
        for (var i = 0; i < _shape.Publishers; i++)
            yield return Row(PublisherTag(i), Names.Publisher(i), Names.Publisher(i), _target.Fields[BuiltInTag.Publisher]);
        for (var i = 0; i < CatalogShape.Years; i++)
        {
            var year = (CatalogShape.FirstYear + i).ToString(CultureInfo.InvariantCulture);
            yield return Row(YearTag(i), year, year, _target.Fields[BuiltInTag.Year]);
        }

        for (var i = 0; i < _shape.PlainTags; i++)
            yield return Row(PlainTag(i), Names.PlainTag(i), Names.PlainTag(i), null);
        for (var i = 0; i < CatalogShape.FilterShares.Length; i++)
        {
            var name = $"filter {CatalogShape.FilterShares[i].ToString("0.###%", CultureInfo.InvariantCulture)}";
            yield return Row(FilterTag(i), name, name, null);
        }
    }

    public static readonly string[] ParentColumns = ["TenantId", "ChildId", "ParentId"];

    /// <summary>Subcategories under their genre (field values are under their field through <c>FieldId</c>, not here).</summary>
    public IEnumerable<object?[]> TagParents()
    {
        for (var i = 0; i < _shape.Categories; i++)
        {
            if (Names.Category(i).Parent is { } parent)
                yield return [_target.TenantId, CategoryTag(i), CategoryTag(parent)];
        }
    }

    public static readonly string[] ClosureColumns = ["TenantId", "AncestorId", "DescendantId", "Depth"];

    public IEnumerable<object?[]> Closure()
    {
        object?[] Row(int ancestor, int descendant, int depth) => [_target.TenantId, ancestor, descendant, depth];

        IEnumerable<object?[]> Values(int first, int count, BuiltInTag field)
        {
            for (var id = first; id < first + count; id++)
            {
                yield return Row(id, id, 0);
                yield return Row(_target.Fields[field], id, 1);
            }
        }

        foreach (var row in Values(AuthorTag(0), _shape.Authors, BuiltInTag.Author))
            yield return row;
        foreach (var row in Values(SeriesTag(0), _shape.Series, BuiltInTag.Series))
            yield return row;
        for (var i = 0; i < _shape.Categories; i++)
        {
            var id = CategoryTag(i);
            var parent = Names.Category(i).Parent;
            yield return Row(id, id, 0);
            yield return Row(_target.Fields[BuiltInTag.Category], id, parent is null ? 1 : 2);
            if (parent is { } p)
                yield return Row(CategoryTag(p), id, 1);
        }

        foreach (var row in Values(LanguageTag(0), _shape.Languages, BuiltInTag.Language))
            yield return row;
        foreach (var row in Values(PublisherTag(0), _shape.Publishers, BuiltInTag.Publisher))
            yield return row;
        foreach (var row in Values(YearTag(0), CatalogShape.Years, BuiltInTag.Year))
            yield return row;
        for (var id = PlainTag(0); id <= LastTagId; id++)
            yield return Row(id, id, 0);
    }

    public static readonly string[] ArchiveColumns = ["Id", "TenantId", "LibraryId", "Path", "PathKey", "Size", "Modified", "Fingerprint", "EntryCount", "ScanState"];

    public IEnumerable<object?[]> Archives()
    {
        for (var i = 0; i < _shape.Archives; i++)
        {
            var path = ArchivePath(i);
            yield return [i + 1, _target.TenantId, _target.LibraryId, path, PathKeys.Of(path), 4L << 30, Start, (long)i * 7919, _shape.EntriesPerArchive, (int)ArchiveScanState.Done];
        }
    }

    private static string ArchivePath(int index) => $"archives/fb2-{index:D4}.zip";

    public static readonly string[] BookColumns =
    [
        "Id", "TenantId", "LibraryId", "Title", "SortTitle", "SortAuthor", "SeriesId", "SeriesNumber", "Description", "Rating", "Added",
        "Updated", "CoverSource", "GeneratedTemplate", "GeneratedVariant", "CoverState", "MergedIntoId", "WorkKey",
    ];

    public IEnumerable<object?[]> Books()
    {
        for (var id = 1; id <= _shape.Books; id++)
        {
            var book = Plan(id);
            var sortAuthor = book.Authors.Length > 0 ? Truncate(TagNames.PersonSortName(Names.Author(book.Authors[0])), Book.MaxSortLength) : "";
            yield return
            [
                id, _target.TenantId, _target.LibraryId, book.Title, BookTitles.SortKey(book.Title ?? book.FileStem), sortAuthor,
                book.Series is { } s ? SeriesTag(s) : null, book.SeriesNumber, book.Description, book.Rating, book.Added, book.Added,
                (int)(book.GeneratedCover ? CoverSource.Generated : CoverSource.Book),
                book.GeneratedCover ? (short)(id % 5) : null, book.GeneratedCover ? (short)(id / 5 % 8) : null,
                (int)CoverState.Unknown, null, book.WorkKey,
            ];
        }
    }

    public static readonly string[] FileColumns =
        ["Id", "TenantId", "LibraryId", "BookId", "ArchiveId", "Path", "PathKey", "Format", "Size", "Crc32", "Modified", "Locator", "Method", "PackedSize"];

    public IEnumerable<object?[]> Files()
    {
        var fileId = 0;
        for (var id = 1; id <= _shape.Books; id++)
        {
            foreach (var file in Plan(id).Files)
            {
                yield return
                [
                    ++fileId, _target.TenantId, _target.LibraryId, id, file.Archive is { } a ? a + 1 : null, file.Path, PathKeys.Of(file.Path),
                    file.Format, file.Size, file.Crc32, Start, file.Archive is null ? null : (long)id * 1024, file.Archive is null ? null : (short)8,
                    file.Archive is null ? null : file.Size / 3,
                ];
            }
        }
    }

    public static readonly string[] IdentifierColumns = ["BookId", "TenantId", "Scheme", "Value"];

    public IEnumerable<object?[]> Identifiers()
    {
        for (var id = 1; id <= _shape.Books; id++)
        {
            var book = Plan(id);
            if (book.Isbn is { } isbn)
                yield return [id, _target.TenantId, "isbn", isbn];
            if (book.DocumentId is { } documentId)
                yield return [id, _target.TenantId, "fb2-id", documentId];
        }
    }

    public static readonly string[] BookTagColumns = ["TenantId", "BookId", "TagId", "Source", "Ordinal", "Number"];

    /// <summary>The links; counts per tag (direct, via parents and per field: once per book) gathered on the way.</summary>
    public IEnumerable<object?[]> BookTags(int[] counts)
    {
        var seen = new HashSet<int>();
        for (var id = 1; id <= _shape.Books; id++)
        {
            var book = Plan(id);
            seen.Clear();

            object?[] Link(int tagId, int? ordinal = null, double? number = null, int? field = null, int? parent = null)
            {
                Count(tagId);
                if (field is { } f)
                    Count(f);
                if (parent is { } p)
                    Count(p);
                return [_target.TenantId, id, tagId, (byte)TagSource.Imported, ordinal, number];
            }

            void Count(int tagId)
            {
                if (seen.Add(tagId))
                    counts[tagId - _target.FirstTagId + Fields.Length]++;
            }

            for (var i = 0; i < book.Authors.Length; i++)
                yield return Link(AuthorTag(book.Authors[i]), ordinal: i, field: FieldSlot(BuiltInTag.Author));
            if (book.Series is { } series)
                yield return Link(SeriesTag(series), number: book.SeriesNumber, field: FieldSlot(BuiltInTag.Series));
            foreach (var category in book.Categories)
            {
                var parent = Names.Category(category).Parent;
                yield return Link(CategoryTag(category), field: FieldSlot(BuiltInTag.Category), parent: parent is { } p ? CategoryTag(p) : null);
            }

            yield return Link(LanguageTag(book.Language), field: FieldSlot(BuiltInTag.Language));
            if (book.Publisher is { } publisher)
                yield return Link(PublisherTag(publisher), field: FieldSlot(BuiltInTag.Publisher));
            if (book.Year is { } year)
                yield return Link(YearTag(year), field: FieldSlot(BuiltInTag.Year));
            foreach (var tag in book.PlainTags)
                yield return Link(PlainTag(tag));
            for (var i = 0; i < CatalogShape.FilterShares.Length; i++)
            {
                if (book.Filters[i])
                    yield return Link(FilterTag(i));
            }
        }
    }

    /// <summary>The built-in fields' counts sit before the generated tags in the counts array.</summary>
    private static readonly BuiltInTag[] Fields = [BuiltInTag.Author, BuiltInTag.Series, BuiltInTag.Category, BuiltInTag.Language, BuiltInTag.Publisher, BuiltInTag.Year];

    private int FieldSlot(BuiltInTag field) => _target.FirstTagId - Fields.Length + Array.IndexOf(Fields, field);

    /// <summary>The counts array <see cref="BookTags"/> fills: the fields first, then every generated tag.</summary>
    public int[] NewCounts() => new int[Fields.Length + LastTagId - _target.FirstTagId + 1];

    public static readonly string[] CountColumns = ["TenantId", "LibraryId", "TagId", "BookCount"];

    public IEnumerable<object?[]> TagCounts(int[] counts)
    {
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0)
                continue;
            var tagId = i < Fields.Length ? _target.Fields[Fields[i]] : _target.FirstTagId + i - Fields.Length;
            yield return [_target.TenantId, _target.LibraryId, tagId, counts[i]];
        }
    }

    /// <summary>What one book is made of, from its id and the seed only.</summary>
    public BookPlan Plan(int id)
    {
        var book = BasePlan(id);
        // Planted duplicates of the book before: the same file in another archive, the same ISBN, the same work.
        if (id > 1 && id % 100 == 7)
        {
            var other = BasePlan(id - 1).Files[0];
            book.Files[0] = book.Files[0] with { Size = other.Size, Crc32 = other.Crc32 };
        }
        else if (id > 1 && id % 100 == 13)
        {
            book.Isbn = BasePlan(id - 1).Isbn ?? book.Isbn;
        }
        else if (id > 1 && id % 50 == 21)
        {
            var other = BasePlan(id - 1);
            book.Title = other.Title;
            book.Authors = other.Authors;
            book.WorkKey = other.WorkKey;
        }

        return book;
    }

    private BookPlan BasePlan(int id)
    {
        // Not HashCode.Combine: it is randomized per process, and the catalog must be the same on every run.
        var random = new Random(unchecked(_shape.Seed * 1_000_003 + id * 7_919));
        var book = new BookPlan { Id = id };
        book.Title = random.Next(100) < 3 ? null : Names.Title(random);
        book.FileStem = id.ToString(CultureInfo.InvariantCulture);
        var authorCount = random.Next(100) switch { < 3 => 0, < 82 => 1, < 96 => 2, _ => 3 };
        book.Authors = Distinct(random, _authors, authorCount);
        if (random.Next(100) < 30)
        {
            book.Series = _series.Next(random);
            book.SeriesNumber = 1 + random.Next(20) + (random.Next(10) == 0 ? 0.5 : 0);
        }

        book.Categories = Distinct(random, _categories, 1 + random.Next(3));
        book.Language = _languages.Next(random);
        book.Publisher = random.Next(100) < 70 ? _publishers.Next(random) : null;
        book.Year = random.Next(100) < 80 ? random.Next(CatalogShape.Years) : null;
        book.PlainTags = Distinct(random, _plainTags, random.Next(100) switch { < 30 => 0, < 55 => 1, < 75 => 2, < 90 => 3, _ => 5 });
        book.Filters = CatalogShape.FilterShares.Select(share => random.NextDouble() < share).ToArray();
        book.Description = random.Next(10) == 0 ? $"A generated description of book {id}: {Names.Title(random)}." : null;
        book.Rating = random.Next(4) == 0 ? (byte)random.Next(11) : null;
        book.Added = Start.AddSeconds(id * 150.0 + random.Next(100));
        book.GeneratedCover = random.Next(100) < 30;
        book.Isbn = random.Next(100) < 40 ? $"978{random.NextInt64(1_000_000_000L, 9_999_999_999L)}" : null;
        if (random.Next(100) < 40)
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            book.DocumentId = new Guid(bytes).ToString();
        }

        var archived = random.Next(100) < 60;
        var files = new List<FilePlan>
        {
            archived
                ? new FilePlan(id % _shape.Archives, $"{id}.fb2", "fb2", 50_000 + random.Next(2_000_000), (long)(uint)random.Next() | ((long)random.Next(2) << 31))
                : new FilePlan(null, $"books/{id % 1000:D3}/{id}.epub", "epub", 100_000 + random.Next(5_000_000), (long)(uint)random.Next()),
        };
        if (random.Next(100) < 20)
            files.Add(new FilePlan(null, $"books/{id % 1000:D3}/{id}.pdf", "pdf", 500_000 + random.Next(20_000_000), (long)(uint)random.Next()));
        book.Files = files;
        var firstAuthor = book.Authors.Length > 0 ? TagNames.Normalize(Names.Author(book.Authors[0])) : "";
        book.WorkKey = PathKeys.Of(TagNames.Normalize(book.Title ?? book.FileStem), firstAuthor);
        return book;
    }

    private static int[] Distinct(Random random, Zipf zipf, int count)
    {
        var picked = new int[Math.Min(count, zipf.Count)];
        var n = 0;
        while (n < picked.Length)
        {
            var next = zipf.Next(random);
            if (Array.IndexOf(picked, next, 0, n) < 0)
                picked[n++] = next;
        }

        return picked;
    }

    private static string Truncate(string text, int length) => text.Length > length ? text[..length] : text;
}

public sealed class BookPlan
{
    public int Id { get; init; }
    public string? Title { get; set; }
    public string FileStem { get; set; } = "";
    public int[] Authors { get; set; } = [];
    public int? Series { get; set; }
    public double? SeriesNumber { get; set; }
    public int[] Categories { get; set; } = [];
    public int Language { get; set; }
    public int? Publisher { get; set; }
    public int? Year { get; set; }
    public int[] PlainTags { get; set; } = [];
    public bool[] Filters { get; set; } = [];
    public string? Description { get; set; }
    public byte? Rating { get; set; }
    public DateTime Added { get; set; }
    public bool GeneratedCover { get; set; }
    public string? Isbn { get; set; }
    public string? DocumentId { get; set; }
    public List<FilePlan> Files { get; set; } = [];
    public long WorkKey { get; set; }
}

/// <param name="Archive">The archive's index; null for a loose file.</param>
public sealed record FilePlan(int? Archive, string Path, string Format, long Size, long Crc32);
