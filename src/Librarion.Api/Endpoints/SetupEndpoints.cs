using Librarion.Api.Security;
using Librarion.Core.Access;
using Librarion.Core.Data;

namespace Librarion.Api.Endpoints;

public static class SetupEndpoints
{
    public static void MapSetupEndpoints(this IEndpointRouteBuilder api)
    {
        var setup = api.MapGroup("/setup").WithTags("Setup");

        setup.MapGet("/", async (SetupService service, CancellationToken cancellationToken) => TypedResults.Ok(await service.GetStateAsync(cancellationToken)))
            .AllowAnonymous()
            .WithName("GetSetup")
            .WithSummary("Whether the server still needs its first administrator, and where its setup code is.");

        setup.MapPost("/", Complete)
            .AllowAnonymous()
            .RequireRateLimiting(SecurityServiceCollectionExtensions.SignInRateLimit)
            .WithName("CompleteSetup")
            .WithSummary("Creates the first server administrator and the first tenant with its library, and signs the administrator in.")
            .WithDescription("Needs the setup code (`Librarion:Setup:Code`, or the one the server wrote to `data/setup-code.txt` and printed). With `Librarion:Setup:Recover` it resets a local server administrator instead.");
    }

    private static async Task<IResult> Complete(SetupInput input, HttpContext context, SetupService setup, SecurityLog log)
    {
        var result = await setup.CompleteAsync(input, context.RequestAborted);
        if (result.Status != AccessStatus.Ok)
        {
            if (result.Errors?.ContainsKey("code") == true)
                await log.WriteAsync(SecurityEventKind.SignInFailed, null, null, input.UserName?.Trim(), context.Address(), "setup code", context.RequestAborted);
            return ApiResults.From(result);
        }

        var done = result.Value!;
        await LibrarionSession.SignInAsync(context, done.User, done.TenantId, persistent: false);
        await log.WriteAsync(SecurityEventKind.SetupCompleted, null, done.User.Id, done.User.UserName, context.Address(), null, context.RequestAborted);
        return TypedResults.Ok(await SessionEndpoints.DescribeAsync(context));
    }
}
