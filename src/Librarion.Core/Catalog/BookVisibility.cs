using System.Collections.Concurrent;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Catalog;

/// <summary>A group's content filter: books with one of <see cref="Allowed"/> (or a descendant; any book when empty), none of <see cref="Denied"/>.</summary>
public sealed record FilterProfile(IReadOnlyList<int> Allowed, IReadOnlyList<int> Denied)
{
    public bool IsOpen => Allowed.Count == 0 && Denied.Count == 0;
}

/// <summary>
/// Which books an account may browse (D5, D22, agents/docs/features/rights.md): the libraries where it has
/// <see cref="Rights.Browse"/>, each with the content filters of the groups giving it there. A book is seen when one
/// of those groups admits it; a group without filters admits every book of its libraries.
/// </summary>
/// <param name="Libraries">Library id → the profiles of the groups giving browse there; an open profile = every book.</param>
/// <param name="Merging">Of those, the libraries where it may merge books (<see cref="Rights.MergeBooks"/>): where
/// <c>special:duplicates</c> finds books (stage 6 chunk 15). None when not given.</param>
public sealed record BookVisibility(IReadOnlyDictionary<int, IReadOnlyList<FilterProfile>> Libraries, IReadOnlySet<int>? Merging = null)
{
    public static readonly BookVisibility None = new(new Dictionary<int, IReadOnlyList<FilterProfile>>());

    public bool IsOpenIn(int libraryId) => Libraries.TryGetValue(libraryId, out var profiles) && profiles.Any(p => p.IsOpen);
}

/// <summary>Works out <see cref="BookVisibility"/> per account and tenant, cached until accounts, groups or libraries change (<see cref="AccessChanges"/>).</summary>
public sealed class BookVisibilities(TenantDb tenantDb, AccessChanges changes, RightsEvaluator rights)
{
    private readonly ConcurrentDictionary<AccessSubject, (long Version, BookVisibility Visibility)> _cache = new();

    public async Task<BookVisibility> GetAsync(AccessSubject subject, CancellationToken cancellationToken = default)
    {
        var version = changes.Version;
        if (_cache.TryGetValue(subject, out var cached) && cached.Version == version)
            return cached.Visibility;
        var visibility = await ComputeAsync(subject, cancellationToken);
        _cache[subject] = (version, visibility);
        return visibility;
    }

    private async Task<BookVisibility> ComputeAsync(AccessSubject subject, CancellationToken cancellationToken)
    {
        var access = await rights.GetAsync(subject, cancellationToken);
        var libraries = access.LibrariesWith(Rights.Browse).ToHashSet();
        if (libraries.Count == 0 || subject.TenantId is not { } tenantId)
            return BookVisibility.None;
        var open = new FilterProfile([], []);
        var merging = access.LibrariesWith(Rights.MergeBooks).Where(libraries.Contains).ToHashSet();
        if (access.IsServerAdmin)
            return new BookVisibility(libraries.ToDictionary(id => id, IReadOnlyList<FilterProfile> (_) => [open]), merging);

        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var groups = await db.Groups.AsNoTracking()
            .Where(g => g.Members.Any(m => m.UserId == subject.UserId) && g.Rights.Any(r => r.Right == Rights.Browse))
            .Select(g => new
            {
                g.AllLibraries,
                Libraries = g.Libraries.Select(l => l.LibraryId).ToList(),
                Filters = g.TagFilters.Select(f => new { f.TagId, f.Deny }).ToList(),
            })
            .ToListAsync(cancellationToken);

        var result = libraries.ToDictionary(id => id, _ => new List<FilterProfile>());
        foreach (var group in groups)
        {
            var profile = new FilterProfile(
                group.Filters.Where(f => !f.Deny).Select(f => f.TagId).Order().ToList(),
                group.Filters.Where(f => f.Deny).Select(f => f.TagId).Order().ToList());
            foreach (var library in group.AllLibraries ? libraries : group.Libraries.Where(libraries.Contains))
                result[library].Add(profile);
        }

        return new BookVisibility(result.Where(r => r.Value.Count > 0)
            .ToDictionary(r => r.Key, IReadOnlyList<FilterProfile> (r) => r.Value.Any(p => p.IsOpen) ? [open] : r.Value.Distinct().ToList()), merging);
    }
}
