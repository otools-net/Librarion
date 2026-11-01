using System.Text.Json;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Librarion.Core.Settings;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Librarion.Api.Endpoints;

/// <param name="Local">A local account: its password can be changed here.</param>
public sealed record SessionAccount(int Id, string UserName, string? DisplayName, bool IsServerAdmin, bool Local);

public sealed record SessionTenant(int Id, string Slug, string Name);

/// <param name="User">The signed-in account; null for a visitor without one.</param>
/// <param name="Tenant">The tenant the session works in.</param>
/// <param name="Tenants">The tenants it can switch to (server administrators: every one).</param>
/// <param name="Rights">Its rights in the tenant (library rights: held in some library), for the client to show what it may do.</param>
/// <param name="SetupRequired">No administrator yet: only the setup page works.</param>
/// <param name="Preferences">The settings in effect for the account (its own over its tenant's and the server's).</param>
public sealed record SessionInfo(
    SessionAccount? User, SessionTenant? Tenant, IReadOnlyList<SessionTenant> Tenants, IReadOnlyList<string> Rights, bool SetupRequired,
    IReadOnlyDictionary<string, JsonElement> Preferences);

public sealed record SignInRequest(string? UserName, string? Password, bool Remember = false);

public sealed record TenantChoice(int TenantId);

public sealed record PasswordChange(string? Current, string? Password);

public sealed record RecoveryRequest(string? UserName);

/// <param name="Minutes">How long a code works.</param>
public sealed record RecoveryRequested(int Minutes);

/// <param name="Code">The code the server showed in its console (or its recovery file).</param>
public sealed record RecoveryCompletion(string? UserName, string? Code, string? Password, bool Remember = false);

public static class SessionEndpoints
{
    /// <summary>Problem type of a refused sign-in (wrong name or password, disabled account).</summary>
    public const string SignInFailedType = "sign-in-failed";

    /// <summary>Problem type of a sign-in refused for a while (this address, or the account itself); <c>until</c> says till when.</summary>
    public const string LockedOutType = "locked-out";

    public static void MapSessionEndpoints(this IEndpointRouteBuilder api)
    {
        var session = api.MapGroup("/session").WithTags("Session");

        // The token keeps the lambda from binding as a RequestDelegate (HttpContext alone), which would drop its result.
        session.MapGet("/", async (HttpContext context, CancellationToken _) => TypedResults.Ok(await DescribeAsync(context)))
            .AllowAnonymous()
            .WithName("GetSession")
            .WithSummary("Who is signed in, in which tenant, with which rights.");

        session.MapPost("/login", SignIn)
            .AllowAnonymous()
            .RequireRateLimiting(SecurityServiceCollectionExtensions.SignInRateLimit)
            .WithName("SignIn")
            .WithSummary("Signs in with a name and password.")
            .WithDescription("`remember` keeps the session after the browser closes. Refused: 400 `sign-in-failed`, or `locked-out` with `until`.");

        session.MapPost("/logout", SignOut)
            .AllowAnonymous()
            .WithName("SignOut")
            .WithSummary("Signs out.");

        session.MapPost("/tenant", SwitchTenant)
            .RequireRight(Rights.ManageTenants)
            .WithName("SwitchTenant")
            .WithSummary("Makes a server administrator work in another tenant.");

        session.MapPost("/recovery", RequestRecovery)
            .AllowAnonymous()
            .RequireRateLimiting(SecurityServiceCollectionExtensions.SignInRateLimit)
            .WithName("RequestAccountRecovery")
            .WithSummary("Asks for a one-time code to recover a server administrator's account; the server shows it only where it runs.")
            .WithDescription($"The code appears in the server's console and in `recovery-code.txt` beside the setup code file, for {AccountRecovery.Lifetime.TotalMinutes:0} minutes. The answer is the same whatever the name.");

        session.MapPost("/recovery/complete", CompleteRecovery)
            .AllowAnonymous()
            .RequireRateLimiting(SecurityServiceCollectionExtensions.SignInRateLimit)
            .WithName("CompleteAccountRecovery")
            .WithSummary("Sets a new password with the recovery code, unlocks the account and signs it in.");

        session.MapPut("/password", ChangePassword)
            .RequireRight(Rights.OwnPassword)
            .RequireRateLimiting(SecurityServiceCollectionExtensions.SignInRateLimit)
            .WithName("ChangeOwnPassword")
            .WithSummary("Changes the password of the signed-in local account; its other sessions end.");
    }

    internal static async Task<SessionInfo> DescribeAsync(HttpContext context)
    {
        var services = context.RequestServices;
        var state = await services.GetRequiredService<CurrentAccess>().GetAsync();
        var setupRequired = await services.GetRequiredService<SetupService>().IsRequiredAsync(context.RequestAborted);
        if (state.User is not { } user)
            return new SessionInfo(null, null, [], [], setupRequired, new Dictionary<string, JsonElement>());

        var accounts = services.GetRequiredService<AccessService>();
        var tenants = user.IsServerAdmin
            ? await accounts.ListTenantsAsync(context.RequestAborted)
            : user.TenantId is { } own && await accounts.GetTenantAsync(own, context.RequestAborted) is { } tenant ? [tenant] : [];
        var current = tenants.FirstOrDefault(t => t.Id == state.TenantId);
        var preferences = await services.GetRequiredService<SettingsService>().EffectiveForUserAsync(user.Id, context.RequestAborted);
        return new SessionInfo(
            new SessionAccount(user.Id, user.UserName, user.DisplayName, user.IsServerAdmin, user.IsLocal),
            current is null ? null : ToSession(current),
            tenants.Select(ToSession).ToList(),
            state.Access.Rights.Order(StringComparer.Ordinal).ToList(),
            setupRequired,
            preferences);
    }

    private static SessionTenant ToSession(TenantInfo tenant) => new(tenant.Id, tenant.Slug, tenant.Name);

    private static async Task<Results<Ok<SessionInfo>, ProblemHttpResult>> SignIn(
        SignInRequest request, HttpContext context, SignInService signIn, SignInThrottle throttle, SecurityLog log)
    {
        var address = context.Address();
        if (throttle.LockedUntil(request.UserName, address) is { } waiting)
            return LockedOut(waiting);

        var result = await signIn.SignInAsync(request.UserName, request.Password, context.RequestAborted);
        if (result.Status != SignInStatus.Ok)
        {
            var until = result.Status == SignInStatus.LockedOut ? result.LockedUntil : throttle.Failed(request.UserName, address)?.UtcDateTime;
            await log.WriteAsync(until is null ? SecurityEventKind.SignInFailed : SecurityEventKind.LockedOut, null, null, request.UserName?.Trim(), address,
                result.Status == SignInStatus.Disabled ? "disabled" : null, context.RequestAborted);
            return until is { } lockedUntil ? LockedOut(lockedUntil) : TypedResults.Problem("Wrong name or password.", statusCode: StatusCodes.Status400BadRequest, type: SignInFailedType);
        }

        throttle.Succeeded(request.UserName, address);
        var user = result.User!;
        await LibrarionSession.SignInAsync(context, user, user.TenantId, request.Remember);
        await log.WriteAsync(SecurityEventKind.SignedIn, user.TenantId, user.Id, user.UserName, address, null, context.RequestAborted);
        return TypedResults.Ok(await DescribeAsync(context));
    }

    private static ProblemHttpResult LockedOut(DateTimeOffset until) =>
        TypedResults.Problem("Too many wrong passwords: try again later.", statusCode: StatusCodes.Status400BadRequest, type: LockedOutType,
            extensions: new Dictionary<string, object?> { ["until"] = until.UtcDateTime });

    private static async Task<Ok<SessionInfo>> SignOut(HttpContext context, CurrentAccess current, SecurityLog log)
    {
        var state = await current.GetAsync();
        if (state.User is { } user)
            await log.LogAsync(context, state, SecurityEventKind.SignedOut, user.TenantId, null);
        await LibrarionSession.SignOutAsync(context);
        return TypedResults.Ok(await DescribeAsync(context));
    }

    private static async Task<Results<Ok<SessionInfo>, NotFound>> SwitchTenant(TenantChoice choice, HttpContext context, CurrentAccess current, AccessService accounts)
    {
        var state = await current.GetAsync();
        if (await accounts.GetTenantAsync(choice.TenantId, context.RequestAborted) is null)
            return TypedResults.NotFound();
        await LibrarionSession.RenewAsync(context, state.User!, choice.TenantId);
        return TypedResults.Ok(await DescribeAsync(context));
    }

    private static async Task<Accepted<RecoveryRequested>> RequestRecovery(RecoveryRequest request, HttpContext context, AccountRecovery recovery, SecurityLog log)
    {
        if (await recovery.RequestAsync(request.UserName, context.RequestAborted) is { New: true } made)
        {
            // The console only, never a logger: logs are stored and shown in the app.
            Console.Out.WriteLine($"""

                Recovery code for {made.Code.UserName}: {made.Code.Code}
                (valid {AccountRecovery.Lifetime.TotalMinutes:0} minutes, also in {recovery.FilePath})

                """);
            await log.WriteAsync(SecurityEventKind.RecoveryRequested, null, made.Code.UserId, made.Code.UserName, context.Address(), null, context.RequestAborted);
        }

        return TypedResults.Accepted((string?)null, new RecoveryRequested((int)AccountRecovery.Lifetime.TotalMinutes));
    }

    private static async Task<IResult> CompleteRecovery(RecoveryCompletion request, HttpContext context, AccountRecovery recovery, SecurityLog log)
    {
        var result = await recovery.CompleteAsync(request.UserName, request.Code, request.Password, context.RequestAborted);
        if (result.Status != AccessStatus.Ok)
        {
            await log.WriteAsync(SecurityEventKind.SignInFailed, null, null, request.UserName?.Trim(), context.Address(), "recovery", context.RequestAborted);
            return ApiResults.From(result);
        }

        var user = result.Value!;
        await LibrarionSession.SignInAsync(context, user, null, request.Remember);
        await log.WriteAsync(SecurityEventKind.RecoveryCompleted, null, user.Id, user.UserName, context.Address(), null, context.RequestAborted);
        return TypedResults.Ok(await DescribeAsync(context));
    }

    private static async Task<IResult> ChangePassword(PasswordChange change, HttpContext context, CurrentAccess current, AccessService accounts, SecurityLog log)
    {
        var state = await current.GetAsync();
        var result = await accounts.ChangeOwnPasswordAsync(state.User!.Id, change.Current, change.Password, context.RequestAborted);
        if (result.Status != AccessStatus.Ok)
            return ApiResults.From(result);

        // A new stamp ended every session: this one goes on with the new stamp.
        await LibrarionSession.RenewAsync(context, result.Value!, state.TenantId);
        await log.LogAsync(context, state, SecurityEventKind.PasswordChanged, state.User.TenantId, null);
        return TypedResults.NoContent();
    }
}
