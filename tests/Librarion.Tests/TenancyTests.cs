using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Settings;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>Tenant isolation in the database (agents/docs/features/tenants.md).</summary>
[TestClass]
public sealed class TenancyTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    /// <summary>
    /// Every entity a tenant owns (a <c>TenantId</c>, or a group's link) has a query filter, and a context made for a
    /// tenant sees none of another tenant's rows in any set, whatever the query asks.
    /// </summary>
    [TestMethod]
    public async Task NoQueryCrossesTenants()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using (ApiClients.Create(factory)) { }
        var services = factory.Services;
        var accounts = services.GetRequiredService<AccessService>();
        var tenantDb = services.GetRequiredService<TenantDb>();

        var a = (await accounts.CreateTenantAsync(new TenantInput("A"), Cancel)).Value!;
        var b = (await accounts.CreateTenantAsync(new TenantInput("B"), Cancel)).Value!;
        foreach (var tenant in new[] { a, b })
        {
            var groups = await accounts.ListGroupsAsync(tenant.Id, Cancel);
            var user = (await accounts.CreateUserAsync(new UserInput("user-" + tenant.Slug, Password: ApiClients.Password, GroupIds: [groups[0].Id]), tenant.Id, Cancel)).Value!;
            var library = (await accounts.ListLibrariesAsync(tenant.Id, Cancel))[0];
            await accounts.UpdateGroupAsync(tenant.Id, groups[1].Id, new GroupInput(groups[1].Name, LibraryIds: [library.Id], Rights: [Rights.Browse]), Cancel);
            await accounts.UpdateTenantAsync(tenant.Id, new TenantInput(tenant.Name, DisabledRights: [Rights.LibrarySettings]), Cancel);
            var settings = services.GetRequiredService<SettingsService>();
            await settings.SaveAsync(new SettingOwner(SettingLevel.Tenant, tenant.Id), null, new SettingChanges(Lock: [SettingCatalog.Theme]), Cancel);
            await settings.SaveAsync(new SettingOwner(SettingLevel.User, user.Id), null,
                new SettingChanges(new() { [SettingCatalog.PageSize] = System.Text.Json.JsonDocument.Parse("100").RootElement }), Cancel);
            await services.GetRequiredService<SecurityLog>().WriteAsync(SecurityEventKind.UserCreated, tenant.Id, null, "x", null, cancellationToken: Cancel);
            var owner = (await accounts.GetSessionUserAsync(user.Id, Cancel))!;
            Assert.AreEqual(AccessStatus.Ok, (await services.GetRequiredService<ApiKeys>().CreateAsync(owner, new ApiKeyInput("key", [ApiKeyScopes.Opds], [library.Id]), Cancel)).Status);
            await using var tenantRows = await tenantDb.ForTenantAsync(tenant.Id, Cancel);
            await CatalogRows.AddAsync(tenantRows, tenant.Id, Cancel);
        }

        await using var db = await tenantDb.ForTenantAsync(a.Id, Cancel);
        var unfiltered = db.Model.GetEntityTypes()
            .Where(t => t.FindProperty("TenantId") is not null || t.FindNavigation("Group") is not null || t.ClrType == typeof(Tenant))
            .Where(t => t.GetDeclaredQueryFilters().Count == 0)
            .Select(t => t.ClrType.Name)
            .ToList();
        var leaks = new Dictionary<string, int>
        {
            ["Tenants"] = await db.Tenants.CountAsync(t => t.Id == b.Id, Cancel),
            ["Libraries"] = await db.Libraries.CountAsync(l => l.TenantId == b.Id, Cancel),
            ["Users"] = await db.Users.CountAsync(u => u.TenantId != a.Id, Cancel),
            ["Groups"] = await db.Groups.CountAsync(g => g.TenantId == b.Id, Cancel),
            ["GroupMembers"] = await db.GroupMembers.CountAsync(m => m.User!.TenantId == b.Id, Cancel),
            ["GroupRights"] = await db.GroupRights.CountAsync(r => r.Group!.TenantId == b.Id, Cancel),
            ["GroupLibraries"] = await db.GroupLibraries.CountAsync(l => l.Library!.TenantId == b.Id, Cancel),
            ["TenantDisabledRights"] = await db.TenantDisabledRights.CountAsync(r => r.TenantId == b.Id, Cancel),
            ["Settings"] = await db.Settings.CountAsync(s => s.TenantId != a.Id, Cancel),
            ["SecurityEvents"] = await db.SecurityEvents.CountAsync(e => e.TenantId != a.Id, Cancel),
            ["Books"] = await db.Books.CountAsync(x => x.TenantId != a.Id, Cancel),
            ["BookFiles"] = await db.BookFiles.CountAsync(x => x.TenantId != a.Id, Cancel),
            ["Archives"] = await db.Archives.CountAsync(x => x.TenantId != a.Id, Cancel),
            ["BookIdentifiers"] = await db.BookIdentifiers.CountAsync(x => x.TenantId != a.Id, Cancel),
            ["Tags"] = await db.Tags.CountAsync(x => x.TenantId != a.Id, Cancel),
            ["TagAliases"] = await db.TagAliases.CountAsync(x => x.TenantId != a.Id, Cancel),
            ["TagParents"] = await db.TagParents.CountAsync(x => x.TenantId != a.Id, Cancel),
            ["TagClosure"] = await db.TagClosure.CountAsync(x => x.TenantId != a.Id, Cancel),
            ["BookTags"] = await db.BookTags.CountAsync(x => x.TenantId != a.Id, Cancel),
            ["TagCounts"] = await db.TagCounts.CountAsync(x => x.TenantId != a.Id, Cancel),
            ["GroupTagFilters"] = await db.GroupTagFilters.CountAsync(x => x.Tag!.TenantId != a.Id, Cancel),
            ["ApiKeys"] = await db.ApiKeys.CountAsync(x => x.TenantId != a.Id, Cancel),
            ["ApiKeyLibraries"] = await db.ApiKeyLibraries.CountAsync(x => x.TenantId != a.Id, Cancel),
        };
        var seenOfA = await db.Users.CountAsync(Cancel) + await db.GroupLibraries.CountAsync(Cancel) + await db.Settings.CountAsync(Cancel);
        Assert.AreEqual(1, await db.Books.CountAsync(Cancel), "The tenant's own book is seen");

        Assert.IsEmpty(unfiltered, "Tenant-owned without a query filter: " + string.Join(", ", unfiltered));
        Assert.IsTrue(leaks.All(l => l.Value <= 0), "Rows of another tenant seen: " + string.Join(", ", leaks.Where(l => l.Value > 0)));
        Assert.IsGreaterThanOrEqualTo(3, seenOfA, "The tenant's own rows are seen");
        await using var server = await tenantDb.ForServerAsync(Cancel);
        Assert.AreEqual(2, await server.Tenants.CountAsync(Cancel), "A server context sees every tenant");
    }

    [TestMethod]
    public async Task DeletingATenantTakesEverythingItOwns()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using (ApiClients.Create(factory)) { }
        var accounts = factory.Services.GetRequiredService<AccessService>();
        var tenant = (await accounts.CreateTenantAsync(new TenantInput("Gone"), Cancel)).Value!;
        await accounts.CreateUserAsync(new UserInput("gone-user", Password: ApiClients.Password), tenant.Id, Cancel);
        await factory.Services.GetRequiredService<SettingsService>()
            .SaveAsync(new SettingOwner(SettingLevel.Tenant, tenant.Id), null, new SettingChanges(Lock: [SettingCatalog.Density]), Cancel);
        await using (var rows = await factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(tenant.Id, Cancel))
            await CatalogRows.AddAsync(rows, tenant.Id, Cancel);

        Assert.IsTrue(await accounts.DeleteTenantAsync(tenant.Id, Cancel));

        await using var db = await factory.Services.GetRequiredService<TenantDb>().ForServerAsync(Cancel);
        Assert.AreEqual(0, await db.Users.CountAsync(Cancel));
        Assert.AreEqual(0, await db.Groups.CountAsync(Cancel));
        Assert.AreEqual(0, await db.Libraries.CountAsync(Cancel));
        Assert.AreEqual(0, await db.Settings.CountAsync(Cancel));
        Assert.AreEqual(0, await db.Books.CountAsync(Cancel) + await db.BookFiles.CountAsync(Cancel) + await db.Archives.CountAsync(Cancel)
            + await db.BookIdentifiers.CountAsync(Cancel), "Books, files, archives and identifiers");
        Assert.AreEqual(0, await db.Tags.CountAsync(Cancel) + await db.TagAliases.CountAsync(Cancel) + await db.TagParents.CountAsync(Cancel)
            + await db.TagClosure.CountAsync(Cancel) + await db.BookTags.CountAsync(Cancel) + await db.TagCounts.CountAsync(Cancel)
            + await db.GroupTagFilters.CountAsync(Cancel), "Tags and their links");
    }

    [TestMethod]
    public async Task UserNamesAreUniqueAcrossTenantsWhateverTheCase()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using (ApiClients.Create(factory)) { }
        var accounts = factory.Services.GetRequiredService<AccessService>();
        var a = (await accounts.CreateTenantAsync(new TenantInput("A"), Cancel)).Value!;
        var b = (await accounts.CreateTenantAsync(new TenantInput("A"), Cancel)).Value!;

        var first = await accounts.CreateUserAsync(new UserInput("Ana", Password: ApiClients.Password), a.Id, Cancel);
        var second = await accounts.CreateUserAsync(new UserInput("ANA", Password: ApiClients.Password), b.Id, Cancel);
        var colon = await accounts.CreateUserAsync(new UserInput("an:a", Password: ApiClients.Password), b.Id, Cancel);
        var shortPassword = await accounts.CreateUserAsync(new UserInput("bea", Password: "short"), b.Id, Cancel);

        Assert.AreEqual(AccessStatus.Ok, first.Status);
        Assert.AreEqual("userName.taken", second.Errors!["userName"][0]);
        Assert.AreEqual("userName.invalid", colon.Errors!["userName"][0]);
        Assert.AreEqual("password.tooShort", shortPassword.Errors!["password"][0]);
        Assert.AreEqual("a-2", b.Slug, "Slugs stay unique");
    }
}
