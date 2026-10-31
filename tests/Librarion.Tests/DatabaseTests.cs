using Librarion.Api;
using Librarion.Core.Data;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

[TestClass]
public sealed class DatabaseTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheStartBringsTheDatabaseUpAndRecordsTheVersion()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var client = ApiClients.Create(factory);

        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<LibrarionDbContext>>().CreateDbContextAsync(TestContext.CancellationToken);

        Assert.IsEmpty(await db.Database.GetPendingMigrationsAsync(TestContext.CancellationToken));
        var version = await db.ServerProperties.SingleAsync(p => p.Key == ServerProperties.Version, TestContext.CancellationToken);
        Assert.AreEqual(AppVersion.Version, version.Value);
    }

    [TestMethod]
    public async Task ASecondStartOnTheSameDatabaseChangesNothing()
    {
        using var catalog = new TestCatalog();
        await using (var first = ApiClients.CreateFactory(catalog))
            using (ApiClients.Create(first)) { }

        await using var second = ApiClients.CreateFactory(catalog);
        using var client = ApiClients.Create(second);
        using var response = await client.GetAsync("/api/system/info", TestContext.CancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>A model change without its migration (for either provider) fails here, not at a user's start.</summary>
    [TestMethod]
    public void EveryModelChangeHasItsMigrationForBothProviders()
    {
        using var sqlite = new SqliteDesignTimeFactory().CreateDbContext([]);
        using var postgres = new PostgresDesignTimeFactory().CreateDbContext([]);

        Assert.IsFalse(sqlite.Database.HasPendingModelChanges(),
            "dotnet ef migrations add <Name> --context SqliteLibrarionDbContext --project src/Librarion.Core --output-dir Data/Migrations/Sqlite");
        Assert.IsFalse(postgres.Database.HasPendingModelChanges(),
            "dotnet ef migrations add <Name> --context PostgresLibrarionDbContext --project src/Librarion.Core --output-dir Data/Migrations/Postgres");
    }

    /// <summary>
    /// Both providers' schemas have the same tables, columns (name, nullability, length), keys and indexes: what one
    /// provider's code relies on exists on the other (column types are the providers' own).
    /// </summary>
    [TestMethod]
    public void ModelsOfBothProvidersAgree()
    {
        using var sqlite = new SqliteDesignTimeFactory().CreateDbContext([]);
        using var postgres = new PostgresDesignTimeFactory().CreateDbContext([]);

        CollectionAssert.AreEqual(Describe(sqlite.GetService<IDesignTimeModel>().Model), Describe(postgres.GetService<IDesignTimeModel>().Model));
    }

    private static List<string> Describe(IModel model)
    {
        var lines = new List<string>();
        foreach (var table in model.GetRelationalModel().Tables.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            lines.Add($"table {table.Name}");
            lines.AddRange(table.Columns.OrderBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => $"  column {c.Name} null={c.IsNullable} max={c.PropertyMappings.First().Property.GetMaxLength()}"));
            if (table.PrimaryKey is { } key)
                lines.Add($"  key {string.Join(",", key.Columns.Select(c => c.Name))}");
            lines.AddRange(table.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal)
                .Select(i => $"  index {i.Name} ({string.Join(",", i.Columns.Select(c => c.Name))}) unique={i.IsUnique}"));
            lines.AddRange(table.ForeignKeyConstraints.OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => $"  fk {f.Name} -> {f.PrincipalTable.Name} {f.OnDeleteAction}"));
        }

        return lines;
    }
}
