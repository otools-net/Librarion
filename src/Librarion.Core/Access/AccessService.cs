using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Librarion.Core.Access;

/// <summary>
/// Tenants, libraries, accounts and groups (ported from SFE's, for tenants holding libraries). Whatever concerns one
/// tenant goes through a context made for it (<see cref="TenantDb.ForTenantAsync"/>): an id of another tenant's row
/// is simply not found. Validation errors carry codes the client translates (<c>error.&lt;code&gt;</c>).
/// </summary>
public sealed class AccessService(TenantDb tenantDb, AccessChanges changes, IOptionsMonitor<SecurityOptions> security, TimeProvider time)
{
    public const string DefaultLibraryName = "Library";

    private readonly ConcurrentDictionary<int, (long Version, SessionUser? User)> _sessionUsers = new();
    private readonly ConcurrentDictionary<int, (long Version, int? TenantId)> _libraryTenants = new();

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ---- Accounts as sessions see them ----

    /// <summary>The account as a request needs it; null when it no longer exists. Cached until the next change.</summary>
    public async Task<SessionUser?> GetSessionUserAsync(int userId, CancellationToken cancellationToken = default)
    {
        var version = changes.Version;
        if (_sessionUsers.TryGetValue(userId, out var cached) && cached.Version == version)
            return cached.User;

        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new SessionUser(u.Id, u.TenantId, u.UserName, u.DisplayName, u.Source, u.IsServerAdmin, u.Disabled || (u.Tenant != null && u.Tenant.Disabled), u.SecurityStamp))
            .FirstOrDefaultAsync(cancellationToken);
        _sessionUsers[userId] = (version, user);
        return user;
    }

    /// <summary>
    /// The tenant a library belongs to (null when it doesn't exist), for server-wide code that is given a library alone
    /// (a server administrator's OPDS address). Cached until the next change of accounts or libraries.
    /// </summary>
    public async Task<int?> LibraryTenantAsync(int libraryId, CancellationToken cancellationToken = default)
    {
        var version = changes.Version;
        if (_libraryTenants.TryGetValue(libraryId, out var cached) && cached.Version == version)
            return cached.TenantId;

        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        var tenantId = await db.Libraries.AsNoTracking().Where(l => l.Id == libraryId).Select(l => (int?)l.TenantId).FirstOrDefaultAsync(cancellationToken);
        _libraryTenants[libraryId] = (version, tenantId);
        return tenantId;
    }

    public async Task<bool> HasServerAdminAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        return await db.Users.AnyAsync(u => u.IsServerAdmin && !u.Disabled, cancellationToken);
    }

    public async Task RecordSignInAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSignIn, Now), cancellationToken);
    }

    /// <summary>Changes one's own local password, after checking the current one; the account's other sessions end.</summary>
    public async Task<AccessResult<SessionUser>> ChangeOwnPasswordAsync(int userId, string? current, string? password, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.Source == UserAccount.LocalSource, cancellationToken);
        if (user is null)
            return AccessResult<SessionUser>.Missing();
        if (!PasswordHashing.Verify(current ?? string.Empty, user.PasswordHash, out _))
            return AccessResult<SessionUser>.Invalid("current", "password.wrong");
        if (PasswordProblem(password) is { } problem)
            return AccessResult<SessionUser>.Invalid("password", problem);

        user.PasswordHash = PasswordHashing.Hash(password!);
        user.SecurityStamp = PasswordHashing.NewStamp();
        await db.SaveChangesAsync(cancellationToken);
        changes.Changed();
        return AccessResult<SessionUser>.Ok((await GetSessionUserAsync(userId, cancellationToken))!);
    }

    // ---- Tenants (server-wide) ----

    public async Task<IReadOnlyList<TenantInfo>> ListTenantsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        var tenants = await db.Tenants.AsNoTracking().Include(t => t.DisabledRights).OrderBy(t => t.Name).ThenBy(t => t.Id).ToListAsync(cancellationToken);
        return tenants.Select(ToInfo).ToList();
    }

    public async Task<TenantInfo?> GetTenantAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().Include(t => t.DisabledRights).FirstOrDefaultAsync(cancellationToken);
        return tenant is null ? null : ToInfo(tenant);
    }

    /// <summary>A new tenant with its built-in groups (default rights, every library), its first library and its built-in tags and fields.</summary>
    public async Task<AccessResult<TenantInfo>> CreateTenantAsync(TenantInput input, CancellationToken cancellationToken = default)
    {
        var name = input.Name?.Trim() ?? string.Empty;
        if (NameProblem(name) is { } problem)
            return AccessResult<TenantInfo>.Invalid("name", problem);
        var libraryName = string.IsNullOrWhiteSpace(input.LibraryName) ? DefaultLibraryName : input.LibraryName.Trim();
        if (NameProblem(libraryName) is { } libraryProblem)
            return AccessResult<TenantInfo>.Invalid("libraryName", libraryProblem);
        if (DisabledRightsProblem(input.DisabledRights) is { } rightsProblem)
            return AccessResult<TenantInfo>.Invalid("disabledRights", rightsProblem);

        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        var slug = await UniqueSlugAsync(db, string.IsNullOrWhiteSpace(input.Slug) ? name : input.Slug, cancellationToken);
        var tenant = new Tenant
        {
            Slug = slug,
            Name = name,
            Disabled = input.Disabled,
            Created = Now,
            DisabledRights = (input.DisabledRights ?? []).Distinct().Select(r => new TenantDisabledRight { Right = r }).ToList(),
            Libraries = [new Library { Name = libraryName, Created = Now }],
            Groups = Enum.GetValues<BuiltInGroup>().Select(builtIn => new UserGroup
            {
                Name = builtIn.ToString(),
                BuiltIn = builtIn,
                AllLibraries = true,
                Rights = RightCatalog.All.Where(r => r.DefaultGroups.Contains(builtIn)).Select(r => new GroupRight { Right = r.Id }).ToList(),
            }).ToList(),
        };
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);
        var (tags, closure) = BuiltInTags.Create(tenant.Id);
        db.Tags.AddRange(tags);
        db.TagClosure.AddRange(closure);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        changes.Changed();
        return AccessResult<TenantInfo>.Ok(ToInfo(tenant));
    }

    public async Task<AccessResult<TenantInfo>> UpdateTenantAsync(int tenantId, TenantInput input, CancellationToken cancellationToken = default)
    {
        var name = input.Name?.Trim() ?? string.Empty;
        if (NameProblem(name) is { } problem)
            return AccessResult<TenantInfo>.Invalid("name", problem);
        if (DisabledRightsProblem(input.DisabledRights) is { } rightsProblem)
            return AccessResult<TenantInfo>.Invalid("disabledRights", rightsProblem);

        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var tenant = await db.Tenants.Include(t => t.DisabledRights).FirstOrDefaultAsync(cancellationToken);
        if (tenant is null)
            return AccessResult<TenantInfo>.Missing();

        tenant.Name = name;
        tenant.Disabled = input.Disabled;
        if (input.DisabledRights is { } disabled)
        {
            tenant.DisabledRights.RemoveAll(r => !disabled.Contains(r.Right));
            foreach (var right in disabled.Distinct().Where(r => tenant.DisabledRights.All(d => d.Right != r)))
                tenant.DisabledRights.Add(new TenantDisabledRight { Right = right });
        }

        await db.SaveChangesAsync(cancellationToken);
        changes.Changed();
        return AccessResult<TenantInfo>.Ok(ToInfo(tenant));
    }

    /// <summary>Deletes a tenant with everything it owns: libraries, accounts, groups, settings.</summary>
    public async Task<bool> DeleteTenantAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var tenant = await db.Tenants.FirstOrDefaultAsync(cancellationToken);
        if (tenant is null)
            return false;
        // Rows without a foreign key to the tenant.
        await db.Settings.ExecuteDeleteAsync(cancellationToken);
        db.Tenants.Remove(tenant);
        await db.SaveChangesAsync(cancellationToken);
        changes.Changed();
        return true;
    }

    // ---- Libraries (one tenant) ----

    public async Task<IReadOnlyList<LibraryInfo>> ListLibrariesAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        return await db.Libraries.AsNoTracking().OrderBy(l => l.Name).ThenBy(l => l.Id)
            .Select(l => new LibraryInfo(l.Id, l.TenantId, l.Name, l.Created, l.BookCount)).ToListAsync(cancellationToken);
    }

    public async Task<AccessResult<LibraryInfo>> CreateLibraryAsync(int tenantId, string? name, CancellationToken cancellationToken = default)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (NameProblem(trimmed) is { } problem)
            return AccessResult<LibraryInfo>.Invalid("name", problem);

        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (!await db.Tenants.AnyAsync(cancellationToken))
            return AccessResult<LibraryInfo>.Missing();
        var library = new Library { TenantId = tenantId, Name = trimmed, Created = Now };
        db.Libraries.Add(library);
        await db.SaveChangesAsync(cancellationToken);
        changes.Changed();
        return AccessResult<LibraryInfo>.Ok(new LibraryInfo(library.Id, tenantId, library.Name, library.Created, library.BookCount));
    }

    public async Task<AccessResult<LibraryInfo>> RenameLibraryAsync(int tenantId, int libraryId, string? name, CancellationToken cancellationToken = default)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (NameProblem(trimmed) is { } problem)
            return AccessResult<LibraryInfo>.Invalid("name", problem);

        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var library = await db.Libraries.FirstOrDefaultAsync(l => l.Id == libraryId, cancellationToken);
        if (library is null)
            return AccessResult<LibraryInfo>.Missing();
        library.Name = trimmed;
        await db.SaveChangesAsync(cancellationToken);
        changes.Changed();
        return AccessResult<LibraryInfo>.Ok(new LibraryInfo(library.Id, tenantId, library.Name, library.Created, library.BookCount));
    }

    public async Task<bool> DeleteLibraryAsync(int tenantId, int libraryId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var library = await db.Libraries.FirstOrDefaultAsync(l => l.Id == libraryId, cancellationToken);
        if (library is null)
            return false;
        await db.Settings.Where(s => s.Level == SettingLevel.Library && s.OwnerId == libraryId).ExecuteDeleteAsync(cancellationToken);
        db.Libraries.Remove(library);
        await db.SaveChangesAsync(cancellationToken);
        changes.Changed();
        return true;
    }

    // ---- Accounts ----

    /// <summary>
    /// Accounts of a tenant, or the server administrators (<paramref name="tenantId"/> null), by name from
    /// <paramref name="after"/> (keyset), those whose name or display name starts with <paramref name="search"/>.
    /// </summary>
    public async Task<IReadOnlyList<UserInfo>> ListUsersAsync(int? tenantId, string? search = null, string? after = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var db = tenantId is { } id ? await tenantDb.ForTenantAsync(id, cancellationToken) : await tenantDb.ForServerAsync(cancellationToken);
        var users = db.Users.AsNoTracking().Where(u => u.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var prefix = search.Trim().ToUpperInvariant();
            users = users.Where(u => u.NormalizedUserName.StartsWith(prefix) || (u.DisplayName != null && u.DisplayName.ToUpper().StartsWith(prefix)));
        }
        if (!string.IsNullOrEmpty(after))
        {
            var key = after.ToUpperInvariant();
            users = users.Where(u => string.Compare(u.NormalizedUserName, key) > 0);
        }

        var rows = await users.OrderBy(u => u.NormalizedUserName).Take(Math.Clamp(limit, 1, 500))
            .Include(u => u.Groups).ThenInclude(m => m.Group)
            .ToListAsync(cancellationToken);
        return rows.Select(ToInfo).ToList();
    }

    /// <param name="tenantId">Only an account of this tenant; null = any account (server-wide callers).</param>
    public async Task<UserInfo?> GetUserAsync(int userId, int? tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await ContextFor(tenantId, cancellationToken);
        var user = await db.Users.AsNoTracking().Include(u => u.Groups).ThenInclude(m => m.Group).FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? null : ToInfo(user);
    }

    /// <summary>A new account: in <paramref name="tenantId"/>, or a server administrator (no tenant).</summary>
    public async Task<AccessResult<UserInfo>> CreateUserAsync(UserInput input, int? tenantId, CancellationToken cancellationToken = default)
    {
        if (input.IsServerAdmin != (tenantId is null))
            return AccessResult<UserInfo>.Invalid("tenantId", input.IsServerAdmin ? "user.adminHasNoTenant" : "user.tenantRequired");
        var userName = input.UserName?.Trim() ?? string.Empty;
        if (UserNameProblem(userName) is { } nameProblem)
            return AccessResult<UserInfo>.Invalid("userName", nameProblem);
        if (DisplayNameProblem(input.DisplayName) is { } displayProblem)
            return AccessResult<UserInfo>.Invalid("displayName", displayProblem);
        if (PasswordProblem(input.Password) is { } passwordProblem)
            return AccessResult<UserInfo>.Invalid("password", passwordProblem);

        await using var db = await ContextFor(tenantId, cancellationToken);
        if (tenantId is not null && !await db.Tenants.AnyAsync(cancellationToken))
            return AccessResult<UserInfo>.Missing();
        if (await NameTakenAsync(userName, null, cancellationToken))
            return AccessResult<UserInfo>.Invalid("userName", "userName.taken");
        var groupIds = input.GroupIds ?? [];
        if (tenantId is null && groupIds.Count > 0 || await db.Groups.CountAsync(g => groupIds.Contains(g.Id), cancellationToken) != groupIds.Distinct().Count())
            return AccessResult<UserInfo>.Invalid("groupIds", "group.unknown");

        var user = new UserAccount
        {
            TenantId = tenantId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            DisplayName = Blank(input.DisplayName),
            PasswordHash = PasswordHashing.Hash(input.Password!),
            IsServerAdmin = input.IsServerAdmin,
            Disabled = input.Disabled,
            SecurityStamp = PasswordHashing.NewStamp(),
            Created = Now,
            Groups = groupIds.Distinct().Select(id => new GroupMember { GroupId = id }).ToList(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        changes.Changed();
        return AccessResult<UserInfo>.Ok((await GetUserAsync(user.Id, tenantId, cancellationToken))!);
    }

    /// <summary>
    /// Changes an account. Its tenant and whether it is a server administrator are fixed once created. A new password,
    /// disabling or enabling it gives it a new stamp (its sessions end).
    /// </summary>
    /// <param name="tenantId">Only an account of this tenant; null = any account (server-wide callers).</param>
    public async Task<AccessResult<UserInfo>> UpdateUserAsync(int userId, UserInput input, int? tenantId, CancellationToken cancellationToken = default)
    {
        var userName = input.UserName?.Trim() ?? string.Empty;
        if (UserNameProblem(userName) is { } nameProblem)
            return AccessResult<UserInfo>.Invalid("userName", nameProblem);
        if (DisplayNameProblem(input.DisplayName) is { } displayProblem)
            return AccessResult<UserInfo>.Invalid("displayName", displayProblem);
        if (input.Password is not null && PasswordProblem(input.Password) is { } passwordProblem)
            return AccessResult<UserInfo>.Invalid("password", passwordProblem);

        await using var db = await ContextFor(tenantId, cancellationToken);
        var user = await db.Users.Include(u => u.Groups).FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return AccessResult<UserInfo>.Missing();
        if (input.IsServerAdmin != user.IsServerAdmin)
            return AccessResult<UserInfo>.Invalid("isServerAdmin", "user.adminFixed");
        if (await NameTakenAsync(userName, userId, cancellationToken))
            return AccessResult<UserInfo>.Invalid("userName", "userName.taken");
        if (input.GroupIds is { } groupIds)
        {
            await using var tenantGroups = user.TenantId is { } own ? await tenantDb.ForTenantAsync(own, cancellationToken) : null;
            if (tenantGroups is null && groupIds.Count > 0
                || tenantGroups is not null && await tenantGroups.Groups.CountAsync(g => groupIds.Contains(g.Id), cancellationToken) != groupIds.Distinct().Count())
                return AccessResult<UserInfo>.Invalid("groupIds", "group.unknown");
        }

        if (user.IsServerAdmin && input.Disabled && !user.Disabled && !await OtherEnabledServerAdminAsync(db, userId, cancellationToken))
            return AccessResult<UserInfo>.Conflict("user.lastServerAdmin");

        if (user.Disabled != input.Disabled || input.Password is not null)
            user.SecurityStamp = PasswordHashing.NewStamp();
        user.UserName = userName;
        user.NormalizedUserName = userName.ToUpperInvariant();
        user.DisplayName = Blank(input.DisplayName);
        user.Disabled = input.Disabled;
        if (input.Password is not null && user.Source == UserAccount.LocalSource)
            user.PasswordHash = PasswordHashing.Hash(input.Password);
        if (input.Unlock)
        {
            user.LockedUntil = null;
            user.FailedSignIns = 0;
        }
        if (input.GroupIds is { } wanted)
        {
            user.Groups.RemoveAll(m => !wanted.Contains(m.GroupId));
            foreach (var id in wanted.Distinct().Where(id => user.Groups.All(m => m.GroupId != id)))
                user.Groups.Add(new GroupMember { GroupId = id });
        }

        if (await SaveKeepingAdministratorsAsync(db, user.TenantId, cancellationToken) is { } conflict)
            return AccessResult<UserInfo>.Conflict(conflict);
        changes.Changed();
        return AccessResult<UserInfo>.Ok((await GetUserAsync(userId, tenantId, cancellationToken))!);
    }

    /// <param name="tenantId">Only an account of this tenant; null = any account (server-wide callers).</param>
    public async Task<AccessResult<bool>> DeleteUserAsync(int userId, int? tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await ContextFor(tenantId, cancellationToken);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return AccessResult<bool>.Missing();
        if (user.IsServerAdmin && !await OtherEnabledServerAdminAsync(db, userId, cancellationToken))
            return AccessResult<bool>.Conflict("user.lastServerAdmin");

        await db.Settings.Where(s => s.Level == SettingLevel.User && s.OwnerId == userId).ExecuteDeleteAsync(cancellationToken);
        db.Users.Remove(user);
        if (await SaveKeepingAdministratorsAsync(db, user.TenantId, cancellationToken) is { } conflict)
            return AccessResult<bool>.Conflict(conflict);
        changes.Changed();
        return AccessResult<bool>.Ok(true);
    }

    // ---- Groups (one tenant) ----

    public async Task<IReadOnlyList<GroupInfo>> ListGroupsAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var groups = await db.Groups.AsNoTracking().Include(g => g.Rights).Include(g => g.Libraries).Include(g => g.Members)
            .OrderBy(g => g.BuiltIn == null).ThenBy(g => g.Name).ToListAsync(cancellationToken);
        return groups.Select(ToInfo).ToList();
    }

    public async Task<AccessResult<GroupInfo>> CreateGroupAsync(int tenantId, GroupInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        if (!await db.Tenants.AnyAsync(cancellationToken))
            return AccessResult<GroupInfo>.Missing();
        var group = new UserGroup { TenantId = tenantId };
        if (await ApplyAsync(db, group, input, cancellationToken) is { } problem)
            return problem;
        db.Groups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
        changes.Changed();
        return AccessResult<GroupInfo>.Ok(ToInfo(group));
    }

    public async Task<AccessResult<GroupInfo>> UpdateGroupAsync(int tenantId, int groupId, GroupInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var group = await db.Groups.Include(g => g.Rights).Include(g => g.Libraries).Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
            return AccessResult<GroupInfo>.Missing();
        if (await ApplyAsync(db, group, input, cancellationToken) is { } problem)
            return problem;
        if (await SaveKeepingAdministratorsAsync(db, tenantId, cancellationToken) is { } conflict)
            return AccessResult<GroupInfo>.Conflict(conflict);
        changes.Changed();
        return AccessResult<GroupInfo>.Ok(ToInfo(group));
    }

    public async Task<AccessResult<bool>> DeleteGroupAsync(int tenantId, int groupId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantDb.ForTenantAsync(tenantId, cancellationToken);
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
            return AccessResult<bool>.Missing();
        if (group.BuiltIn is not null)
            return AccessResult<bool>.Conflict("group.builtIn");
        db.Groups.Remove(group);
        await db.SaveChangesAsync(cancellationToken);
        changes.Changed();
        return AccessResult<bool>.Ok(true);
    }

    // ---- Helpers ----

    private Task<LibrarionDbContext> ContextFor(int? tenantId, CancellationToken cancellationToken) =>
        tenantId is { } id ? tenantDb.ForTenantAsync(id, cancellationToken) : tenantDb.ForServerAsync(cancellationToken);

    private async Task<AccessResult<GroupInfo>?> ApplyAsync(LibrarionDbContext db, UserGroup group, GroupInput input, CancellationToken cancellationToken)
    {
        var name = input.Name?.Trim() ?? string.Empty;
        if (NameProblem(name) is { } problem)
            return AccessResult<GroupInfo>.Invalid("name", problem);
        if (input.Rights?.FirstOrDefault(r => !RightCatalog.IsGrantable(r)) is not null)
            return AccessResult<GroupInfo>.Invalid("rights", "right.unknown");
        if (input.LibraryIds is { } libraryIds && await db.Libraries.CountAsync(l => libraryIds.Contains(l.Id), cancellationToken) != libraryIds.Distinct().Count())
            return AccessResult<GroupInfo>.Invalid("libraryIds", "library.unknown");
        if (input.MemberIds is { } memberIds && await db.Users.CountAsync(u => memberIds.Contains(u.Id), cancellationToken) != memberIds.Distinct().Count())
            return AccessResult<GroupInfo>.Invalid("memberIds", "user.unknown");

        // Built-in groups keep their names: the client translates them.
        if (group.BuiltIn is null)
            group.Name = name;
        else if (string.IsNullOrEmpty(group.Name))
            group.Name = group.BuiltIn.ToString()!;
        group.AllLibraries = input.AllLibraries;
        group.ExternalName = Blank(input.ExternalName);
        if (input.Rights is { } rights)
        {
            group.Rights.RemoveAll(r => !rights.Contains(r.Right));
            foreach (var right in rights.Distinct().Where(r => group.Rights.All(g => g.Right != r)))
                group.Rights.Add(new GroupRight { Right = right });
        }
        if (input.LibraryIds is { } libraries)
        {
            group.Libraries.RemoveAll(l => !libraries.Contains(l.LibraryId));
            foreach (var id in libraries.Distinct().Where(id => group.Libraries.All(l => l.LibraryId != id)))
                group.Libraries.Add(new GroupLibrary { LibraryId = id });
        }
        if (input.MemberIds is { } members)
        {
            group.Members.RemoveAll(m => !members.Contains(m.UserId));
            foreach (var id in members.Distinct().Where(id => group.Members.All(m => m.UserId != id)))
                group.Members.Add(new GroupMember { UserId = id });
        }

        return null;
    }

    /// <summary>
    /// Saves, unless the tenant had enabled members in its Administrators group and would have none left (a tenant
    /// administrator locking everyone out): then nothing is saved and the problem code is returned.
    /// </summary>
    private static async Task<string?> SaveKeepingAdministratorsAsync(LibrarionDbContext db, int? tenantId, CancellationToken cancellationToken)
    {
        if (tenantId is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        var before = await EnabledAdministratorsAsync(db, tenantId.Value, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (before > 0 && await EnabledAdministratorsAsync(db, tenantId.Value, cancellationToken) == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return "tenant.lastAdministrator";
        }

        await transaction.CommitAsync(cancellationToken);
        return null;
    }

    private static Task<int> EnabledAdministratorsAsync(LibrarionDbContext db, int tenantId, CancellationToken cancellationToken) =>
        db.GroupMembers.CountAsync(m => m.Group!.TenantId == tenantId && m.Group.BuiltIn == BuiltInGroup.Administrators && !m.User!.Disabled, cancellationToken);

    private static Task<bool> OtherEnabledServerAdminAsync(LibrarionDbContext db, int userId, CancellationToken cancellationToken) =>
        db.Users.IgnoreQueryFilters().AnyAsync(u => u.IsServerAdmin && !u.Disabled && u.Id != userId, cancellationToken);

    private async Task<bool> NameTakenAsync(string userName, int? exceptId, CancellationToken cancellationToken)
    {
        // Names are unique across tenants: looked up server-wide.
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        var normalized = userName.ToUpperInvariant();
        return await db.Users.AnyAsync(u => u.NormalizedUserName == normalized && u.Id != exceptId, cancellationToken);
    }

    private static async Task<string> UniqueSlugAsync(LibrarionDbContext db, string from, CancellationToken cancellationToken)
    {
        var slug = Slugify(from);
        var candidate = slug;
        for (var n = 2; await db.Tenants.AnyAsync(t => t.Slug == candidate, cancellationToken); n++)
            candidate = $"{slug}-{n.ToString(CultureInfo.InvariantCulture)}";
        return candidate;
    }

    /// <summary>Lower-case ASCII letters, digits and dashes; accents dropped; "tenant" when nothing is left.</summary>
    internal static string Slugify(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsAsciiLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > 40)
            slug = slug[..40].TrimEnd('-');
        return slug.Length == 0 ? "tenant" : slug;
    }

    private static string? NameProblem(string name) =>
        name.Length == 0 ? "name.required" : name.Length > LibrarionDbContext.MaxNameLength ? "name.tooLong" : null;

    /// <summary>OPDS readers send <c>name:password</c>: a colon can't be in a name; nor control characters.</summary>
    internal static string? UserNameProblem(string userName) =>
        userName.Length == 0 ? "userName.required"
        : userName.Length > LibrarionDbContext.MaxNameLength ? "userName.tooLong"
        : userName.Any(c => c == ':' || char.IsControl(c)) ? "userName.invalid"
        : null;

    private static string? DisplayNameProblem(string? displayName) =>
        displayName is { Length: > LibrarionDbContext.MaxNameLength } ? "displayName.tooLong" : null;

    private string? PasswordProblem(string? password) =>
        string.IsNullOrEmpty(password) ? "password.required"
        : password.Length < security.CurrentValue.MinimumPasswordLength ? "password.tooShort"
        : password.Length > 1000 ? "password.tooLong"
        : null;

    private static string? DisabledRightsProblem(IReadOnlyList<string>? rights) =>
        rights?.Any(r => !RightCatalog.IsGrantable(r)) == true ? "right.unknown" : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static TenantInfo ToInfo(Tenant t) =>
        new(t.Id, t.Slug, t.Name, t.Disabled, t.Created, t.DisabledRights.Select(r => r.Right).Order(StringComparer.Ordinal).ToList());

    private UserInfo ToInfo(UserAccount u) =>
        new(u.Id, u.TenantId, u.UserName, u.DisplayName, u.Source, u.IsServerAdmin, u.Disabled, u.LockedUntil > Now, u.Created, u.LastSignIn,
            u.Groups.Where(m => m.Group is not null).Select(m => new MembershipInfo(m.GroupId, m.Group!.Name)).OrderBy(m => m.Group, StringComparer.Ordinal).ToList());

    private static GroupInfo ToInfo(UserGroup g) =>
        new(g.Id, g.TenantId, g.Name, g.BuiltIn, g.ExternalName, g.AllLibraries,
            g.Rights.Select(r => r.Right).Order(StringComparer.Ordinal).ToList(),
            g.Libraries.Select(l => l.LibraryId).Order().ToList(),
            g.Members.Select(m => m.UserId).Order().ToList());
}
