using System.Diagnostics;
using Librarion.Benchmarks.Data;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Benchmarks.Generation;

/// <summary>
/// <c>generate</c>: a fresh database with one tenant ("Bench", library "Books"), the generated catalog bulk-loaded
/// into it, counts written, and one group (with an account) per narrow allow-list of D22.
/// </summary>
public static class GenerateCommand
{
    public const string Password = "benchmark-password";

    public static async Task<CatalogTarget> RunAsync(BenchTarget target, CatalogShape shape, string workFolder, Action<string> report, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        await BenchDatabase.RecreateAsync(target, cancellationToken);
        using var host = await BenchDatabase.OpenAsync(target, workFolder, cancellationToken);
        var accounts = host.Services.GetRequiredService<AccessService>();
        var tenantDb = host.Services.GetRequiredService<TenantDb>();
        var tenant = (await accounts.CreateTenantAsync(new TenantInput("Bench", LibraryName: "Books"), cancellationToken)).Value!;

        CatalogTarget catalogTarget;
        await using (var db = await tenantDb.ForTenantAsync(tenant.Id, cancellationToken))
        {
            var library = await db.Libraries.SingleAsync(cancellationToken);
            var fields = await db.Tags.Where(t => t.FieldKey != null).ToDictionaryAsync(t => t.BuiltIn!.Value, t => t.Id, cancellationToken);
            var firstTag = await db.Tags.MaxAsync(t => t.Id, cancellationToken) + 1;
            var root = new LibraryRoot { TenantId = tenant.Id, LibraryId = library.Id, Path = Path.Combine(workFolder, "books"), Created = DateTime.UtcNow };
            db.LibraryRoots.Add(root);
            await db.SaveChangesAsync(cancellationToken);
            catalogTarget = new CatalogTarget(tenant.Id, library.Id, firstTag, fields, root.Id);
        }

        var generator = new CatalogGenerator(shape, catalogTarget);
        var counts = generator.NewCounts();
        var direct = generator.NewCounts();
        await using (var writer = BulkWriter.Open(target.Provider, target.ConnectionString))
        {
            await writer.BeginAsync(cancellationToken);
            async Task Load(string table, string[] columns, IEnumerable<object?[]> rows)
            {
                var watch = Stopwatch.StartNew();
                var written = await writer.WriteAsync(table, columns, rows, cancellationToken);
                report($"{table}: {written:N0} rows in {watch.Elapsed.TotalSeconds:0.0} s");
            }

            await Load("Tags", CatalogGenerator.TagColumns, generator.Tags());
            await Load("TagAliases", CatalogGenerator.AliasColumns, generator.TagAliases());
            await Load("TagParents", CatalogGenerator.ParentColumns, generator.TagParents());
            await Load("TagClosure", CatalogGenerator.ClosureColumns, generator.Closure());
            await Load("Archives", CatalogGenerator.ArchiveColumns, generator.Archives());
            await Load("Folders", CatalogGenerator.FolderColumns, generator.Folders());
            await Load("Books", CatalogGenerator.BookColumns, generator.Books());
            await Load("BookFiles", CatalogGenerator.FileColumns, generator.Files());
            await Load("BookIdentifiers", CatalogGenerator.IdentifierColumns, generator.Identifiers());
            await Load("BookTags", CatalogGenerator.BookTagColumns, generator.BookTags(counts, direct));
            await Load("TagCounts", CatalogGenerator.CountColumns, generator.TagCounts(counts, direct));
            await writer.ExecuteAsync($"UPDATE \"Libraries\" SET \"BookCount\" = {shape.Books} WHERE \"Id\" = {catalogTarget.LibraryId}", cancellationToken);

            var indexes = Stopwatch.StartNew();
            await writer.EndAsync(sql => report("  " + sql), cancellationToken);
            report($"indexes and statistics in {indexes.Elapsed.TotalSeconds:0.0} s");
        }

        // One group per narrow allow-list, its library rights limited to the books with its filter tag, and an account in it.
        for (var i = 0; i < CatalogShape.FilterShares.Length; i++)
        {
            var name = $"filter-{i}";
            var group = (await accounts.CreateGroupAsync(tenant.Id, new GroupInput(name, AllLibraries: true, Rights: [Rights.Browse, Rights.SystemInfo]), cancellationToken)).Value!;
            await accounts.CreateUserAsync(new UserInput(name, Password: Password, GroupIds: [group.Id]), tenant.Id, cancellationToken);
            await using var db = await tenantDb.ForTenantAsync(tenant.Id, cancellationToken);
            db.GroupTagFilters.Add(new GroupTagFilter { GroupId = group.Id, TagId = generator.FilterTag(i) });
            await db.SaveChangesAsync(cancellationToken);
        }

        report($"generated {shape.Books:N0} books in {total.Elapsed.TotalMinutes:0.0} min");
        return catalogTarget;
    }
}
