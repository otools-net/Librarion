using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Librarion.Api.Endpoints;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests;

/// <summary>Setup, sign-in, sessions, rights over HTTP (agents/docs/features/accounts.md, rights.md, setup.md).</summary>
[TestClass]
public sealed class AccessTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Cancel => TestContext.CancellationToken;

    [TestMethod]
    public async Task TheServerAsksForSetupUntilAnAdministratorExists()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var client = ApiClients.Create(factory);

        using var blocked = await client.GetAsync("/api/system/info", Cancel);
        var state = await client.GetAsync<SetupState>("/api/setup", Cancel);
        using var wrong = await client.PostAsJsonAsync("/api/setup", new SetupInput("AAAA-BBBB-CCCC-DDDD", "admin", ApiClients.Password), ApiClients.Json, Cancel);
        var code = factory.Services.GetRequiredService<GeneratedSetupCode>().Ensure();
        using var done = await client.PostAsJsonAsync("/api/setup", new SetupInput(code.ToLowerInvariant().Replace("-", " "), "admin", ApiClients.Password, TenantName: "Family"), ApiClients.Json, Cancel);
        var session = await done.Content.ReadFromJsonAsync<SessionInfo>(ApiClients.Json, Cancel);
        using var again = await client.PostAsJsonAsync("/api/setup", new SetupInput(code, "second", ApiClients.Password), ApiClients.Json, Cancel);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        StringAssert.Contains(await blocked.Content.ReadAsStringAsync(Cancel), RequestGuard.SetupRequiredType);
        Assert.IsTrue(state.Required);
        Assert.IsTrue(state.CodeGenerated);
        Assert.IsNotNull(state.CodeFile);
        Assert.AreEqual(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, done.StatusCode, await done.Content.ReadAsStringAsync(Cancel));
        Assert.IsTrue(session!.User!.IsServerAdmin);
        Assert.AreEqual("Family", session.Tenant!.Name);
        CollectionAssert.Contains(session.Rights.ToList(), Rights.ManageTenants);
        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
        Assert.IsFalse((await client.GetAsync<SetupState>("/api/setup", Cancel)).Required);
    }

    [TestMethod]
    public async Task AWrongPasswordIsRefusedAndLogged()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        using var visitor = ApiClients.Create(factory);

        using var refused = await visitor.PostAsJsonAsync("/api/session/login", new SignInRequest(ApiClients.AdminName, "wrong password"), ApiClients.Json, Cancel);
        var problem = await refused.Content.ReadFromJsonAsync<ProblemDetails>(ApiClients.Json, Cancel);
        var events = await admin.GetAsync<List<SecurityEventInfo>>("/api/security-events?all=true", Cancel);

        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.AreEqual(SessionEndpoints.SignInFailedType, problem!.Type);
        Assert.IsTrue(events.Any(e => e.Kind == SecurityEventKind.SignInFailed && e.UserName == ApiClients.AdminName), string.Join(", ", events.Select(e => e.Kind)));
        Assert.IsTrue(events.Any(e => e.Kind == SecurityEventKind.SetupCompleted));
        Assert.IsNull((await visitor.GetAsync<SessionInfo>("/api/session", Cancel)).User);
    }

    [TestMethod]
    public async Task AnAddressIsHeldBackAfterTooManyWrongPasswords()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog, settings => settings["Librarion:Security:MaxFailedSignIns"] = "3");
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        using var visitor = ApiClients.Create(factory);

        for (var i = 0; i < 3; i++)
            using (await visitor.PostAsJsonAsync("/api/session/login", new SignInRequest(ApiClients.AdminName, "wrong"), ApiClients.Json, Cancel)) { }
        using var held = await visitor.PostAsJsonAsync("/api/session/login", new SignInRequest(ApiClients.AdminName, ApiClients.Password), ApiClients.Json, Cancel);
        var problem = await held.Content.ReadFromJsonAsync<ProblemDetails>(ApiClients.Json, Cancel);

        Assert.AreEqual(SessionEndpoints.LockedOutType, problem!.Type);
        Assert.IsTrue(problem.Extensions.ContainsKey("until"));
    }

    [TestMethod]
    public async Task AnAccountIsLockedAfterWrongPasswordsFromEverywhereUntilUnlocked()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog, settings =>
        {
            settings["Librarion:Security:MaxFailedSignIns"] = "100";
            settings["Librarion:Security:AccountLockoutFailures"] = "2";
        });
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        var reader = await ApiClients.CreateUserAsync(admin, new NewUser("reader", Password: ApiClients.Password), Cancel);
        using var visitor = ApiClients.Create(factory);

        for (var i = 0; i < 2; i++)
            using (await visitor.PostAsJsonAsync("/api/session/login", new SignInRequest("reader", "wrong"), ApiClients.Json, Cancel)) { }
        using var locked = await visitor.PostAsJsonAsync("/api/session/login", new SignInRequest("reader", ApiClients.Password), ApiClients.Json, Cancel);
        var listed = await admin.GetAsync<UserInfo>($"/api/users/{reader.Id}", Cancel);
        using (await admin.PutAsJsonAsync($"/api/users/{reader.Id}", new UserInput("reader", Unlock: true), ApiClients.Json, Cancel)) { }

        Assert.AreEqual(SessionEndpoints.LockedOutType, (await locked.Content.ReadFromJsonAsync<ProblemDetails>(ApiClients.Json, Cancel))!.Type);
        Assert.IsTrue(listed.LockedOut);
        using (await ApiClients.SignInAsync(factory, "reader", ApiClients.Password, Cancel)) { }
    }

    [TestMethod]
    public async Task ChangingThePasswordEndsTheOtherSessions()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        var users = (await admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel)).Single(g => g.BuiltIn == BuiltInGroup.Users);
        await ApiClients.CreateUserAsync(admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [users.Id]), Cancel);
        using var phone = await ApiClients.SignInAsync(factory, "reader", ApiClients.Password, Cancel);
        using var laptop = await ApiClients.SignInAsync(factory, "reader", ApiClients.Password, Cancel);

        using var wrongCurrent = await phone.PutAsJsonAsync("/api/session/password", new PasswordChange("not it", "a new password"), ApiClients.Json, Cancel);
        using var changed = await phone.PutAsJsonAsync("/api/session/password", new PasswordChange(ApiClients.Password, "a new password"), ApiClients.Json, Cancel);

        Assert.AreEqual(HttpStatusCode.BadRequest, wrongCurrent.StatusCode, await wrongCurrent.Content.ReadAsStringAsync(Cancel));
        Assert.AreEqual(HttpStatusCode.NoContent, changed.StatusCode, await changed.Content.ReadAsStringAsync(Cancel));
        Assert.AreEqual("reader", (await phone.GetAsync<SessionInfo>("/api/session", Cancel)).User?.UserName, "The session that changed it goes on");
        Assert.IsNull((await laptop.GetAsync<SessionInfo>("/api/session", Cancel)).User, "The other session ended");
        using (await ApiClients.SignInAsync(factory, "reader", "a new password", Cancel)) { }
    }

    [TestMethod]
    public async Task DisablingAnAccountEndsItsSessions()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        var reader = await ApiClients.CreateUserAsync(admin, new NewUser("reader", Password: ApiClients.Password), Cancel);
        using var session = await ApiClients.SignInAsync(factory, "reader", ApiClients.Password, Cancel);

        using (await admin.PutAsJsonAsync($"/api/users/{reader.Id}", new UserInput("reader", Disabled: true), ApiClients.Json, Cancel)) { }
        using var refused = await ApiClients.Create(factory).PostAsJsonAsync("/api/session/login", new SignInRequest("reader", ApiClients.Password), ApiClients.Json, Cancel);

        Assert.IsNull((await session.GetAsync<SessionInfo>("/api/session", Cancel)).User);
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [TestMethod]
    public async Task AMemberHasTheMembersRightsOnly()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        var users = (await admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel)).Single(g => g.BuiltIn == BuiltInGroup.Users);
        await ApiClients.CreateUserAsync(admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [users.Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(factory, "reader", ApiClients.Password, Cancel);

        var session = await reader.GetAsync<SessionInfo>("/api/session", Cancel);
        using var info = await reader.GetAsync("/api/system/info", Cancel);
        using var accounts = await reader.GetAsync("/api/users", Cancel);
        using var tenants = await reader.GetAsync("/api/tenants", Cancel);
        using var anonymous = await ApiClients.Create(factory).GetAsync("/api/users", Cancel);

        CollectionAssert.AreEquivalent(
            RightCatalog.All.Where(r => r.DefaultGroups.Contains(BuiltInGroup.Users)).Select(r => r.Id).ToList(),
            session.Rights.ToList());
        Assert.AreEqual(HttpStatusCode.OK, info.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, accounts.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, tenants.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [TestMethod]
    public async Task ACuratorCuratesButDoesNotAdminister()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        var curators = (await admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel)).Single(g => g.BuiltIn == BuiltInGroup.Curators);
        await ApiClients.CreateUserAsync(admin, new NewUser("curator", Password: ApiClients.Password, GroupIds: [curators.Id]), Cancel);
        using var curator = await ApiClients.SignInAsync(factory, "curator", ApiClients.Password, Cancel);

        var session = await curator.GetAsync<SessionInfo>("/api/session", Cancel);
        using var accounts = await curator.GetAsync("/api/users", Cancel);
        using var deleted = await admin.DeleteAsync($"/api/groups/{curators.Id}", Cancel);

        CollectionAssert.AreEquivalent(
            RightCatalog.All.Where(r => r.DefaultGroups.Contains(BuiltInGroup.Curators)).Select(r => r.Id).ToList(),
            session.Rights.ToList());
        CollectionAssert.IsSubsetOf(
            new[] { Rights.Browse, Rights.Download, Rights.TagBooks, Rights.EditBooks, Rights.MergeBooks, Rights.ImportedTags, Rights.DefaultTags, Rights.ManageTags },
            session.Rights.ToList());
        Assert.AreEqual(HttpStatusCode.Forbidden, accounts.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, deleted.StatusCode, "Built-in groups stay");
    }

    [TestMethod]
    public async Task LibraryRightsApplyInTheGroupsLibrariesOnly()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        var second = await Created<LibraryInfo>(await admin.PostAsJsonAsync("/api/libraries", new LibraryInput("Comics"), ApiClients.Json, Cancel));
        var group = await Created<GroupInfo>(await admin.PostAsJsonAsync("/api/groups",
            new GroupInput("Comic readers", Rights: [Rights.Browse], LibraryIds: [second.Id]), ApiClients.Json, Cancel));
        await ApiClients.CreateUserAsync(admin, new NewUser("reader", Password: ApiClients.Password, GroupIds: [group.Id]), Cancel);
        using var reader = await ApiClients.SignInAsync(factory, "reader", ApiClients.Password, Cancel);

        var seen = await reader.GetAsync<List<LibraryInfo>>("/api/libraries", Cancel);
        var all = await admin.GetAsync<List<LibraryInfo>>("/api/libraries", Cancel);

        Assert.AreEqual(2, all.Count);
        Assert.AreEqual("Comics", seen.Single().Name);
    }

    [TestMethod]
    public async Task ATenantAdministratorCannotReachAnotherTenantOrTheServerAdministrators()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        var (admin, session) = await ApiClients.SetUpAsync(factory, Cancel);
        using var _ = admin;
        var home = session.Tenant!.Id;
        var other = await Created<TenantInfo>(await admin.PostAsJsonAsync("/api/tenants", new TenantInput("Other"), ApiClients.Json, Cancel));
        var otherUser = await ApiClients.CreateUserAsync(admin, new NewUser("stranger", Password: ApiClients.Password, TenantId: other.Id), Cancel);
        var otherGroup = (await admin.GetAsync<List<GroupInfo>>($"/api/groups?tenant={other.Id}", Cancel)).First();
        var otherLibrary = (await admin.GetAsync<List<LibraryInfo>>($"/api/libraries?tenant={other.Id}", Cancel)).First();
        var administrators = (await admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel)).Single(g => g.BuiltIn == BuiltInGroup.Administrators);
        await ApiClients.CreateUserAsync(admin, new NewUser("boss", Password: ApiClients.Password, GroupIds: [administrators.Id]), Cancel);
        using var boss = await ApiClients.SignInAsync(factory, "boss", ApiClients.Password, Cancel);

        var own = await boss.GetAsync<List<UserInfo>>("/api/users", Cancel);
        var refused = new List<string>();
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, $"/api/users?tenant={other.Id}", null),
                     (HttpMethod.Get, $"/api/users/{otherUser.Id}", null),
                     (HttpMethod.Put, $"/api/users/{otherUser.Id}", new UserInput("stranger", Disabled: true)),
                     (HttpMethod.Delete, $"/api/users/{otherUser.Id}", null),
                     (HttpMethod.Get, $"/api/users/{session.User!.Id}", null),
                     (HttpMethod.Put, $"/api/users/{session.User.Id}", new UserInput(ApiClients.AdminName, IsServerAdmin: true, Disabled: true)),
                     (HttpMethod.Get, "/api/users?admins=true", null),
                     (HttpMethod.Post, "/api/users", new NewUser("sneaky", Password: ApiClients.Password, TenantId: other.Id)),
                     (HttpMethod.Post, "/api/users", new NewUser("sneaky", Password: ApiClients.Password, IsServerAdmin: true)),
                     (HttpMethod.Get, $"/api/groups?tenant={other.Id}", null),
                     (HttpMethod.Put, $"/api/groups/{otherGroup.Id}", new GroupInput("Mine", Rights: [Rights.ManageUsers])),
                     (HttpMethod.Put, $"/api/groups/{otherGroup.Id}?tenant={other.Id}", new GroupInput("Mine")),
                     (HttpMethod.Get, $"/api/libraries?tenant={other.Id}", null),
                     (HttpMethod.Put, $"/api/libraries/{otherLibrary.Id}", new LibraryInput("Mine")),
                     (HttpMethod.Delete, $"/api/libraries/{otherLibrary.Id}", null),
                     (HttpMethod.Get, $"/api/settings?level=tenant&owner={other.Id}", null),
                     (HttpMethod.Get, $"/api/settings?level=library&owner={otherLibrary.Id}", null),
                     (HttpMethod.Get, $"/api/security-events?tenant={other.Id}", null),
                     (HttpMethod.Get, "/api/tenants", null),
                     (HttpMethod.Get, "/api/settings?level=server", null),
                 })
        {
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: ApiClients.Json) };
            using var response = await boss.SendAsync(request, Cancel);
            if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Forbidden))
                refused.Add($"{method} {path}: {(int)response.StatusCode}");
        }

        Assert.IsEmpty(refused, "Reached: " + string.Join("; ", refused));
        Assert.AreEqual("boss", own.Single().UserName, "Only the tenant's accounts, never the server administrators");
        var stranger = await admin.GetAsync<UserInfo>($"/api/users/{otherUser.Id}", Cancel);
        Assert.IsFalse(stranger.Disabled);
        Assert.AreEqual(home, (await boss.GetAsync<SessionInfo>("/api/session", Cancel)).Tenant!.Id);
    }

    [TestMethod]
    public async Task TheLastServerAdministratorStays()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        var (admin, session) = await ApiClients.SetUpAsync(factory, Cancel);
        using var _ = admin;

        using var disabled = await admin.PutAsJsonAsync($"/api/users/{session.User!.Id}", new UserInput(ApiClients.AdminName, IsServerAdmin: true, Disabled: true), ApiClients.Json, Cancel);
        using var deleted = await admin.DeleteAsync($"/api/users/{session.User.Id}", Cancel);

        Assert.AreEqual(HttpStatusCode.Conflict, disabled.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, deleted.StatusCode);
    }

    [TestMethod]
    public async Task ATenantKeepsAnAdministrator()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        var administrators = (await admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel)).Single(g => g.BuiltIn == BuiltInGroup.Administrators);
        var boss = await ApiClients.CreateUserAsync(admin, new NewUser("boss", Password: ApiClients.Password, GroupIds: [administrators.Id]), Cancel);
        using var client = await ApiClients.SignInAsync(factory, "boss", ApiClients.Password, Cancel);

        using var leaving = await client.PutAsJsonAsync($"/api/users/{boss.Id}", new UserInput("boss", GroupIds: []), ApiClients.Json, Cancel);
        using var emptied = await client.PutAsJsonAsync($"/api/groups/{administrators.Id}",
            new GroupInput("Administrators", AllLibraries: true, MemberIds: []), ApiClients.Json, Cancel);

        Assert.AreEqual(HttpStatusCode.Conflict, leaving.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, emptied.StatusCode);
        Assert.AreEqual("boss", (await client.GetAsync<SessionInfo>("/api/session", Cancel)).User?.UserName);
    }

    [TestMethod]
    public async Task RecoveryGivesAServerAdministratorANewPassword()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using ((await ApiClients.SetUpAsync(factory, Cancel)).Client) { }
        using var visitor = ApiClients.Create(factory);

        using var asked = await visitor.PostAsJsonAsync("/api/session/recovery", new RecoveryRequest(ApiClients.AdminName), ApiClients.Json, Cancel);
        using var unknown = await visitor.PostAsJsonAsync("/api/session/recovery", new RecoveryRequest("nobody"), ApiClients.Json, Cancel);
        var recovery = factory.Services.GetRequiredService<AccountRecovery>();
        var pending = await recovery.RequestAsync(ApiClients.AdminName, Cancel);
        Assert.IsTrue(File.Exists(recovery.FilePath), "Written where the server runs");
        using var wrong = await visitor.PostAsJsonAsync("/api/session/recovery/complete",
            new RecoveryCompletion(ApiClients.AdminName, "AAAA-AAAA-AAAA-AAAA", "a brand new password"), ApiClients.Json, Cancel);
        using var done = await visitor.PostAsJsonAsync("/api/session/recovery/complete",
            new RecoveryCompletion(ApiClients.AdminName, pending!.Value.Code.Code, "a brand new password"), ApiClients.Json, Cancel);

        Assert.AreEqual(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.AreEqual(HttpStatusCode.Accepted, unknown.StatusCode, "The same answer whatever the name");
        Assert.IsFalse(pending.Value.New, "Asking again keeps the code");
        Assert.IsFalse(File.Exists(recovery.FilePath), "Used up: the file goes");
        Assert.AreEqual(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, done.StatusCode, await done.Content.ReadAsStringAsync(Cancel));
        Assert.IsTrue((await visitor.GetAsync<SessionInfo>("/api/session", Cancel)).User!.IsServerAdmin);
        using var old = await ApiClients.Create(factory).PostAsJsonAsync("/api/session/login", new SignInRequest(ApiClients.AdminName, ApiClients.Password), ApiClients.Json, Cancel);
        Assert.AreEqual(HttpStatusCode.BadRequest, old.StatusCode);
    }

    [TestMethod]
    public async Task RightsTheServerDisablesAreNobodys()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog, settings => settings["Librarion:Rights:Disabled:0"] = RightCatalog.WriteSelector);
        var (admin, session) = await ApiClients.SetUpAsync(factory, Cancel);
        using var _ = admin;

        using var create = await admin.PostAsJsonAsync("/api/users", new NewUser("reader", Password: ApiClients.Password), ApiClients.Json, Cancel);

        Assert.AreEqual(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.IsTrue(session.Rights.All(r => RightCatalog.Find(r)!.Kind == RightKind.Read), string.Join(", ", session.Rights));

        using var other = new TestCatalog();
        await using var libraryOff = ApiClients.CreateFactory(other, settings => settings["Librarion:Rights:Disabled:0"] = "library.*");
        var (second, secondSession) = await ApiClients.SetUpAsync(libraryOff, Cancel);
        using var __ = second;
        var rights = await second.GetAsync<List<RightInfo>>("/api/rights", Cancel);
        Assert.IsTrue(rights.Single(r => r.Id == Rights.Browse).DisabledByServer);
        Assert.IsFalse(rights.Single(r => r.Id == Rights.ManageUsers).DisabledByServer);
        Assert.IsFalse(secondSession.Rights.Contains(Rights.Browse));
    }

    [TestMethod]
    public async Task AServerAdministratorSwitchesTenants()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        var other = await Created<TenantInfo>(await admin.PostAsJsonAsync("/api/tenants", new TenantInput("Other", LibraryName: "TestLibrary"), ApiClients.Json, Cancel));

        using var switched = await admin.PostAsJsonAsync("/api/session/tenant", new TenantChoice(other.Id), ApiClients.Json, Cancel);
        var session = await switched.Content.ReadFromJsonAsync<SessionInfo>(ApiClients.Json, Cancel);
        var libraries = await admin.GetAsync<List<LibraryInfo>>("/api/libraries", Cancel);

        Assert.AreEqual("Other", session!.Tenant!.Name);
        Assert.AreEqual(2, session.Tenants.Count);
        Assert.AreEqual("TestLibrary", libraries.Single().Name);
        Assert.AreEqual("other", other.Slug);
    }

    [TestMethod]
    public async Task ATenantAdministratorReadsItsTenantsSecurityLogOnly()
    {
        using var catalog = new TestCatalog();
        await using var factory = ApiClients.CreateFactory(catalog);
        using var admin = (await ApiClients.SetUpAsync(factory, Cancel)).Client;
        await Created<TenantInfo>(await admin.PostAsJsonAsync("/api/tenants", new TenantInput("Other"), ApiClients.Json, Cancel));
        var administrators = (await admin.GetAsync<List<GroupInfo>>("/api/groups", Cancel)).Single(g => g.BuiltIn == BuiltInGroup.Administrators);
        await ApiClients.CreateUserAsync(admin, new NewUser("boss", Password: ApiClients.Password, GroupIds: [administrators.Id]), Cancel);
        using var boss = await ApiClients.SignInAsync(factory, "boss", ApiClients.Password, Cancel);

        var seen = await boss.GetAsync<List<SecurityEventInfo>>("/api/security-events", Cancel);
        var all = await admin.GetAsync<List<SecurityEventInfo>>("/api/security-events?all=true", Cancel);
        var page = await admin.GetAsync<List<SecurityEventInfo>>($"/api/security-events?all=true&before={all[0].Id}", Cancel);

        Assert.IsTrue(seen.Any(e => e.Kind == SecurityEventKind.SignedIn && e.UserName == "boss"));
        Assert.IsTrue(seen.All(e => e.TenantId == seen[0].TenantId && e.TenantId is not null), "Only its tenant's");
        Assert.IsTrue(all.Any(e => e.Kind == SecurityEventKind.TenantCreated));
        Assert.AreEqual(all.Count - 1, page.Count);
        Assert.IsTrue(all.All(e => e.Detail?.Contains(ApiClients.Password, StringComparison.Ordinal) != true), "No secret in the log");
    }

    private async Task<T> Created<T>(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.IsTrue(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Cancel)}");
            return (await response.Content.ReadFromJsonAsync<T>(ApiClients.Json, Cancel))!;
        }
    }
}
