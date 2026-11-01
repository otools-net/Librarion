using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Librarion.Api.Endpoints;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Settings;
using Librarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>Settings by level: inheritance, locks, rows only where they differ (agents/docs/features/settings.md).</summary>
[TestClass]
public sealed class SettingsTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [TestMethod]
    public async Task ALevelInheritsLocksAndKeepsOnlyWhatDiffers()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        var (admin, session) = await ApiClients.SetUpAsync(factory, Cancel);
        using var _ = admin;
        await ApiClients.CreateUserAsync(admin, new NewUser("reader", Password: ApiClients.Password,
            GroupIds: [(await admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel)).Single(g => g.BuiltIn == BuiltInGroup.Users).Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(factory, "reader", ApiClients.Password, Cancel);

        // The server makes dark the theme and locks it: the tenant can't change it, the reader gets it.
        using (await admin.PatchAsJsonAsync("/api/settings?level=server",
                   new SettingChanges(new() { [SettingCatalog.Theme] = Json("\"dark\"") }, Lock: [SettingCatalog.Theme]), ApiClients.Json, Cancel)) { }
        var tenant = await admin.GetAsync<List<SettingState>>($"/api/settings?level=tenant&owner={session.Tenant!.Id}", Cancel);
        using var refused = await admin.PatchAsJsonAsync("/api/settings?level=tenant",
            new SettingChanges(new() { [SettingCatalog.Theme] = Json("\"light\"") }), ApiClients.Json, Cancel);
        var locked = await reader.GetAsync<SessionInfo>("/api/session", Cancel);

        var theme = tenant.Single(s => s.Key == SettingCatalog.Theme);
        Assert.IsTrue(theme.LockedAbove);
        Assert.IsFalse(theme.Editable);
        Assert.AreEqual("dark", theme.Value.GetString());
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.AreEqual("dark", locked.Preferences[SettingCatalog.Theme].GetString());

        // Unlocked: the reader picks light; picking dark again (what it inherits) leaves no row.
        using (await admin.PatchAsJsonAsync("/api/settings?level=server", new SettingChanges(Unlock: [SettingCatalog.Theme]), ApiClients.Json, Cancel)) { }
        using var own = await reader.PatchAsJsonAsync("/api/me/preferences",
            new SettingChanges(new() { [SettingCatalog.Theme] = Json("\"light\""), [SettingCatalog.PageSize] = Json("100") }), ApiClients.Json, Cancel);
        Assert.AreEqual(HttpStatusCode.OK, own.StatusCode, await own.Content.ReadAsStringAsync(Cancel));
        Assert.AreEqual("light", (await reader.GetAsync<SessionInfo>("/api/session", Cancel)).Preferences[SettingCatalog.Theme].GetString());
        using (await reader.PatchAsJsonAsync("/api/me/preferences", new SettingChanges(new() { [SettingCatalog.Theme] = Json("\"dark\"") }), ApiClients.Json, Cancel)) { }

        await using var db = await factory.Services.GetRequiredService<TenantDb>().ForServerAsync(Cancel);
        var rows = await db.Settings.Where(s => s.Level == SettingLevel.User).Select(s => s.Key).ToListAsync(Cancel);
        CollectionAssert.AreEquivalent(new[] { SettingCatalog.PageSize }, rows);
    }

    [TestMethod]
    public async Task AValueOutOfItsRangeOrAnUnknownKeyIsRefusedWithoutSavingAnything()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;

        using var response = await admin.PatchAsJsonAsync("/api/me/preferences", new SettingChanges(new()
        {
            [SettingCatalog.Density] = Json("\"comfortable\""),
            [SettingCatalog.PageSize] = Json("5000"),
            ["no.such.key"] = Json("1"),
        }), ApiClients.Json, Cancel);
        var body = await response.Content.ReadAsStringAsync(Cancel);
        var states = await admin.GetAsync<List<SettingState>>("/api/me/preferences", Cancel);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(body, "setting.invalid");
        StringAssert.Contains(body, "setting.unknown");
        Assert.AreEqual("compact", states.Single(s => s.Key == SettingCatalog.Density).Value.GetString(), "All or nothing");
    }
}
