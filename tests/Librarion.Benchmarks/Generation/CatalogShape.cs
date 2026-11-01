namespace Librarion.Benchmarks.Generation;

/// <summary>
/// The sizes of a generated catalog, in proportion to its books as in the scale budgets
/// (agents/docs/development/foundation-plan.md: 2M books, 500k authors, 100k series, 50k tags, 2.5M files).
/// </summary>
public sealed record CatalogShape(int Books, int Seed)
{
    public int Authors => Math.Max(10, Books / 4);
    public int Series => Math.Max(5, Books / 20);
    public int PlainTags => Math.Max(20, Math.Min(50_000, Books / 40));
    public int Publishers => Math.Max(5, Books / 200);
    public int Categories => Names.CategoryCount;
    public int Languages => Names.Languages.Length;
    public const int FirstYear = 1900;
    public const int Years = 126;

    /// <summary>Entries per container archive (archived files are spread over the archives by book id).</summary>
    public int EntriesPerArchive { get; init; } = 100_000;

    public int Archives => Math.Max(1, (int)Math.Ceiling(Books * 0.6 / EntriesPerArchive));

    /// <summary>The narrow allow-lists of D22: a filter tag on this share of the books each.</summary>
    public static readonly double[] FilterShares = [0.001, 0.01, 0.1];
}
