using Librarion.Core.Access;
using Librarion.Core.Covers.Generated;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Covers;

/// <summary>A generated cover template as Cover styles lists it: its index, name (translated by the client), and whether new books get it.</summary>
public sealed record CoverTemplateInfo(int Index, string Name, bool On);

/// <summary>
/// Administration › Catalog › Cover styles (stage 6 chunk 10b, <c>tenant.coverStyles</c>,
/// agents/docs/features/book-display.md#cover-styles): the generated templates turned on or off for the tenant (new books
/// get one of those on, at least one stays on; books keep theirs), and the books of a template turned off given one of
/// those on as a background run. A style a user chose on a book's page stays.
/// </summary>
public sealed class TenantCoverStyles(TenantDb tenantDb, BackgroundTasks tasks, TimeProvider time)
{
    /// <summary>Books read a batch at a time by the reassignment, keyed on <c>Books.Id</c>.</summary>
    public const int BatchSize = 1000;

    public async Task<IReadOnlyList<CoverTemplateInfo>> ListAsync(int tenantId, CancellationToken cancellationToken = default) =>
        Infos(await OffAsync(tenantId, cancellationToken));

    /// <summary>The tenant's templates turned off (bit <c>i</c> = template <c>i</c>).</summary>
    public async Task<int> OffAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        return await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.CoverTemplatesOff).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Turns a template on or off for new books; refused for a template that doesn't exist (<c>cover.style</c>) or the last one on (<c>coverStyle.lastOn</c>).</summary>
    public async Task<AccessResult<IReadOnlyList<CoverTemplateInfo>>> SetAsync(int tenantId, int template, bool on, CancellationToken cancellationToken = default)
    {
        if (template < 0 || template >= GeneratedCovers.Templates.Count)
            return AccessResult<IReadOnlyList<CoverTemplateInfo>>.Invalid("template", "cover.style");
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
            return AccessResult<IReadOnlyList<CoverTemplateInfo>>.Missing();
        var off = on ? tenant.CoverTemplatesOff & ~(1 << template) : tenant.CoverTemplatesOff | (1 << template);
        if (Infos(off).All(t => !t.On))
            return AccessResult<IReadOnlyList<CoverTemplateInfo>>.Invalid("template", "coverStyle.lastOn");
        tenant.CoverTemplatesOff = off;
        await db.SaveChangesAsync(cancellationToken);
        return AccessResult<IReadOnlyList<CoverTemplateInfo>>.Ok(Infos(off));
    }

    /// <summary>
    /// Starts the run giving the books of <paramref name="template"/> one of the templates on; refused while the template
    /// is on (<c>coverStyle.on</c>). The run's id.
    /// </summary>
    public async Task<AccessResult<long>> ReassignAsync(int tenantId, int template, int? userId, CancellationToken cancellationToken = default)
    {
        if (template < 0 || template >= GeneratedCovers.Templates.Count)
            return AccessResult<long>.Invalid("template", "cover.style");
        if ((await OffAsync(tenantId, cancellationToken) & (1 << template)) == 0)
            return AccessResult<long>.Invalid("template", "coverStyle.on");
        var run = await tasks.StartTenantAsync(tenantId, BackgroundTaskKind.CoverReassign, userId, async (progress, cancel) =>
        {
            var changed = await ReassignBooksAsync(tenantId, template, progress, cancel);
            return new ScanResult(0, changed, 0, 0, 0, 0, 0, [], []);
        }, cancellationToken);
        return AccessResult<long>.Ok(run);
    }

    /// <summary>
    /// The books of <paramref name="template"/> given a template on, as an import would choose among them (from their
    /// <c>WorkKey</c>), by batches of <see cref="BatchSize"/> keyed on <c>Books.Id</c>; those whose style a user chose
    /// are left. Nothing when the template was turned on again before the run started. How many books changed.
    /// </summary>
    internal async Task<int> ReassignBooksAsync(int tenantId, int template, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var off = await OffAsync(tenantId, cancellationToken);
        if ((off & (1 << template)) == 0)
            return 0;
        var (last, seen, changed) = (0, 0, 0);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
            var rows = await db.Books.Where(b => b.Id > last && b.GeneratedTemplate == template).OrderBy(b => b.Id).Take(BatchSize)
                .Select(b => new { b.Id, b.WorkKey, b.Edited }).ToListAsync(cancellationToken);
            if (rows.Count == 0)
                break;
            last = rows[^1].Id;
            seen += rows.Count;
            // Updated moves: the cover's address carries it, so browsers ask for the new picture.
            var now = time.GetUtcNow().UtcDateTime;
            foreach (var group in rows.Where(r => !r.Edited.HasFlag(BookEdits.Cover)).GroupBy(r => GeneratedCovers.Assign(r.WorkKey ?? r.Id, off), r => r.Id))
            {
                var ids = group.ToList();
                var (newTemplate, variant) = group.Key;
                changed += await db.Books.Where(b => ids.Contains(b.Id)).ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.GeneratedTemplate, (short?)newTemplate).SetProperty(b => b.GeneratedVariant, (short?)variant).SetProperty(b => b.Updated, now), cancellationToken);
            }

            progress?.Report(new ScanProgress(seen, 0, 0, changed, 0, GeneratedCovers.Templates[template].Name));
        }

        return changed;
    }

    private static IReadOnlyList<CoverTemplateInfo> Infos(int off) =>
        GeneratedCovers.Templates.Select((t, i) => new CoverTemplateInfo(i, t.Name, (off & (1 << i)) == 0)).ToList();
}
