using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;

namespace Librarion.Api.Endpoints;

/// <summary>
/// API keys (agents/docs/features/opds-auth.md): one's own (<see cref="Rights.ApiKeys"/>), and an account's for its
/// administrators (list and revoke only). Managing keys needs the web session: a request signed in with a key is
/// refused (403 <see cref="NeedsSessionType"/>), so a key can't make a wider one.
/// </summary>
public static class ApiKeyEndpoints
{
    public const string NeedsSessionType = "apiKey.needsSession";

    public static void MapApiKeyEndpoints(this IEndpointRouteBuilder api)
    {
        var own = api.MapGroup("/account/api-keys").WithTags("API keys").RequireRight(Rights.ApiKeys);

        own.MapGet("/", async (HttpContext context, CurrentAccess current, ApiKeys keys) =>
                await current.GetAsync() is var state && state.KeyId is not null
                    ? NeedsSession()
                    : Results.Ok(await keys.ListAsync(state.User!.Id, context.RequestAborted)))
            .WithName("ListOwnApiKeys")
            .WithSummary("The signed-in account's API keys, newest first (never their secrets).");

        own.MapPost("/", async (ApiKeyInput input, HttpContext context, CurrentAccess current, ApiKeys keys, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                if (state.KeyId is not null)
                    return NeedsSession();
                var result = await keys.CreateAsync(state.User!, input, context.RequestAborted);
                if (result.Status == AccessStatus.Ok)
                {
                    var info = result.Value!.Info;
                    await log.LogAsync(context, state, SecurityEventKind.ApiKeyCreated, state.User!.TenantId, $"{info.Name} ({string.Join(' ', info.Scopes)})");
                }
                return ApiResults.From(result);
            })
            .WithName("CreateApiKey")
            .WithSummary("Makes an API key: name, scopes (`opds`, `api:read`, `api:write`), optional libraries, path use and expiry. The answer holds the key's text, shown this once.");

        own.MapDelete("/{id:int}", async (int id, HttpContext context, CurrentAccess current, ApiKeys keys, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                if (state.KeyId is not null)
                    return NeedsSession();
                var result = await keys.RevokeAsync(state.User!.Id, id, context.RequestAborted);
                if (result.Status == AccessStatus.Ok)
                    await log.LogAsync(context, state, SecurityEventKind.ApiKeyRevoked, state.User.TenantId, result.Value!.Name);
                return ApiResults.From(result);
            })
            .WithName("RevokeOwnApiKey")
            .WithSummary("Ends one of the signed-in account's keys at once; it stays listed as revoked.");

        var users = api.MapGroup("/users/{userId:int}/api-keys").WithTags("API keys").RequireRight(Rights.ManageUsers, Rights.ManageAllUsers);

        users.MapGet("/", async (int userId, HttpContext context, CurrentAccess current, AccessService accounts, ApiKeys keys) =>
            {
                var state = await current.GetAsync();
                if (state.KeyId is not null)
                    return NeedsSession();
                return await accounts.GetUserAsync(userId, Scope(state), context.RequestAborted) is null
                    ? Results.NotFound()
                    : Results.Ok(await keys.ListAsync(userId, context.RequestAborted));
            })
            .WithName("ListUserApiKeys")
            .WithSummary("An account's API keys, for its administrators.");

        users.MapDelete("/{id:int}", async (int userId, int id, HttpContext context, CurrentAccess current, AccessService accounts, ApiKeys keys, SecurityLog log) =>
            {
                var state = await current.GetAsync();
                if (state.KeyId is not null)
                    return NeedsSession();
                if (await accounts.GetUserAsync(userId, Scope(state), context.RequestAborted) is not { } user)
                    return Results.NotFound();
                var result = await keys.RevokeAsync(userId, id, context.RequestAborted);
                if (result.Status == AccessStatus.Ok)
                    await log.LogAsync(context, state, SecurityEventKind.ApiKeyRevoked, user.TenantId, $"{result.Value!.Name}, of {user.UserName}");
                return ApiResults.From(result);
            })
            .WithName("RevokeUserApiKey")
            .WithSummary("Ends an account's key at once, for its administrators.");
    }

    private static IResult NeedsSession() =>
        TypedResults.Problem("API keys are managed from the app, not with a key.", statusCode: StatusCodes.Status403Forbidden, type: NeedsSessionType);

    /// <summary>Every account for <see cref="Rights.ManageAllUsers"/>; the request's tenant's otherwise (-1: none), as the account routes.</summary>
    private static int? Scope(AccessState state) => state.Has(Rights.ManageAllUsers) ? null : state.TenantId ?? -1;
}
