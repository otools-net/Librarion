using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Tagging;

/// <summary>
/// Which tags are plain tags (no field, not a field's own row), read by key once per id and kept: what a writer sets
/// <see cref="Book.Untagged"/> from in memory for the books of a batch (stage 6 chunk 15), instead of a query per book.
/// A tag's kind is fixed when it is made, so what is kept stays true.
/// </summary>
public sealed class PlainTags
{
    private const int MaxCached = 500_000;
    private readonly Dictionary<int, bool> plain = [];

    /// <summary>Reads the kinds of <paramref name="tagIds"/> not known yet; call before <see cref="AnyIn"/> for those ids.</summary>
    public async Task LoadAsync(LibrarionDbContext db, IEnumerable<int> tagIds, CancellationToken cancellationToken)
    {
        if (plain.Count > MaxCached)
            plain.Clear();
        var unknown = tagIds.Where(id => !plain.ContainsKey(id)).Distinct().ToList();
        foreach (var chunk in unknown.Chunk(1000))
        {
            var ids = chunk.ToList();
            foreach (var row in await db.Tags.Where(t => EF.Parameter(ids).Contains(t.Id)).Select(t => new { t.Id, Plain = t.FieldId == null && t.FieldKey == null })
                         .ToListAsync(cancellationToken))
                plain[row.Id] = row.Plain;
        }
    }

    /// <summary>Whether one of <paramref name="tagIds"/> (loaded) is a plain tag.</summary>
    public bool AnyIn(IEnumerable<int> tagIds) => tagIds.Any(id => plain.GetValueOrDefault(id));
}
