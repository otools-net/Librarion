using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Librarion.Core.Access;

/// <param name="Required">No enabled server administrator yet: only the setup page works.</param>
/// <param name="Available">The setup page can be used: <paramref name="Required"/>, or <see cref="SetupOptions.Recover"/> is on.</param>
/// <param name="CodeConfigured">A setup code is there to be used (configured, or generated into the file).</param>
/// <param name="CodeTooShort">A configured setup code is shorter than <see cref="SetupOptions.MinimumCodeLength"/>.</param>
/// <param name="Recover">The setup resets or adds a local server administrator rather than starting from nothing.</param>
/// <param name="CodeGenerated">The code is the one the server made (<see cref="GeneratedSetupCode"/>), shown in its console.</param>
/// <param name="CodeFile">Where that code is kept, as configured; only while the page needs it.</param>
public sealed record SetupState(bool Required, bool Available, bool CodeConfigured, bool CodeTooShort, bool Recover, int MinimumCodeLength,
    bool CodeGenerated = false, string? CodeFile = null);

/// <param name="Code">The setup code.</param>
/// <param name="TenantName">Name of the first tenant (ignored in recovery); empty = "Home".</param>
/// <param name="LibraryName">Name of its first library; empty = "Library".</param>
public sealed record SetupInput(string? Code, string? UserName, string? Password, string? DisplayName = null, string? TenantName = null, string? LibraryName = null);

/// <param name="TenantId">The tenant the administrator works in first; null in recovery when no tenant exists.</param>
public sealed record SetupDone(SessionUser User, int? TenantId);

/// <summary>
/// The first server administrator and the first tenant (as SFE's setup). Until one exists, nothing but the setup page
/// works; the page needs the setup code, so only whoever runs the server can claim it.
/// </summary>
public sealed class SetupService(
    TenantDb tenantDb,
    AccessService accounts,
    AccessChanges changes,
    IOptionsMonitor<SetupOptions> options,
    GeneratedSetupCode generatedCode,
    ILogger<SetupService> logger)
{
    public const string DefaultTenantName = "Home";

    private (long Version, bool HasAdmin)? _hasAdmin;

    /// <summary>No server administrator yet. Asked on every request: cached until the next change of the accounts.</summary>
    public async Task<bool> IsRequiredAsync(CancellationToken cancellationToken = default)
    {
        var version = changes.Version;
        if (_hasAdmin is { } cached && cached.Version == version)
            return !cached.HasAdmin;

        var hasAdmin = await accounts.HasServerAdminAsync(cancellationToken);
        _hasAdmin = (version, hasAdmin);
        return !hasAdmin;
    }

    public async Task<SetupState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var setup = options.CurrentValue;
        var required = await IsRequiredAsync(cancellationToken);
        var available = required || setup.Recover;
        if (available)
            setup = EnsureCode(setup);
        var code = setup.Code?.Trim();
        return new SetupState(
            required,
            available,
            setup.HasUsableCode,
            !string.IsNullOrEmpty(code) && code.Length < SetupOptions.MinimumCodeLength,
            setup.Recover,
            SetupOptions.MinimumCodeLength,
            setup.CodeIsGenerated,
            available && setup.CodeIsGenerated ? setup.CodeFile : null);
    }

    /// <summary>
    /// Creates the first server administrator and the first tenant with its library; in recovery, resets the password
    /// of the local server administrator with that name (enabled and unlocked) or adds one.
    /// </summary>
    public async Task<AccessResult<SetupDone>> CompleteAsync(SetupInput input, CancellationToken cancellationToken = default)
    {
        var setup = options.CurrentValue;
        if (!setup.Recover && !await IsRequiredAsync(cancellationToken))
            return AccessResult<SetupDone>.Conflict("setup.done");
        setup = EnsureCode(setup);
        if (!setup.HasUsableCode)
            return AccessResult<SetupDone>.Conflict("setup.noCode");
        if (!setup.Matches(input.Code))
        {
            logger.LogWarning("Setup refused: wrong setup code");
            return AccessResult<SetupDone>.Invalid("code", "setup.wrongCode");
        }

        var userName = input.UserName?.Trim() ?? string.Empty;
        var existing = setup.Recover ? await FindLocalAdminAsync(userName, cancellationToken) : null;
        var result = existing is null
            ? await accounts.CreateUserAsync(new UserInput(userName, input.DisplayName, input.Password, IsServerAdmin: true), null, cancellationToken)
            : await accounts.UpdateUserAsync(existing.Id,
                new UserInput(existing.UserName, input.DisplayName ?? existing.DisplayName, input.Password ?? string.Empty, IsServerAdmin: true, Disabled: false, Unlock: true),
                null, cancellationToken);
        if (result.Status != AccessStatus.Ok)
            return result.As<SetupDone>();

        int? tenantId;
        if (setup.Recover)
        {
            tenantId = (await accounts.ListTenantsAsync(cancellationToken)).FirstOrDefault()?.Id;
        }
        else
        {
            var tenantName = string.IsNullOrWhiteSpace(input.TenantName) ? DefaultTenantName : input.TenantName;
            var tenant = await accounts.CreateTenantAsync(new TenantInput(tenantName, LibraryName: input.LibraryName), cancellationToken);
            if (tenant.Status != AccessStatus.Ok)
            {
                await accounts.DeleteUserAsync(result.Value!.Id, null, cancellationToken);
                return tenant.As<SetupDone>();
            }
            tenantId = tenant.Value!.Id;
        }

        logger.LogWarning(setup.Recover
            ? "Recovery: {User} is a server administrator again. Turn Librarion:Setup:Recover off."
            : "Setup done: {User} is the server administrator.", result.Value!.UserName);
        return AccessResult<SetupDone>.Ok(new SetupDone((await accounts.GetSessionUserAsync(result.Value.Id, cancellationToken))!, tenantId));
    }

    /// <summary>Without a configured code, the one the server makes itself (Program.cs also does it at start-up and prints it).</summary>
    private SetupOptions EnsureCode(SetupOptions setup)
    {
        if (!string.IsNullOrWhiteSpace(setup.Code))
            return setup;
        try
        {
            generatedCode.Ensure();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Cannot write the setup code to {Path}: set Librarion:Setup:Code instead", generatedCode.FilePath);
        }

        return options.CurrentValue;
    }

    private async Task<UserAccount?> FindLocalAdminAsync(string userName, CancellationToken cancellationToken)
    {
        var normalized = userName.ToUpperInvariant();
        await using var db = await tenantDb.ForServerAsync(cancellationToken);
        return await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == normalized && u.Source == UserAccount.LocalSource && u.IsServerAdmin, cancellationToken);
    }
}
