using Librarion.Core.Data;

namespace Librarion.Api.Endpoints;

public sealed record SystemInfo(string Version, string? Commit, DatabaseProvider Database);

public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/system").WithTags("System");

        // Anonymous until stage 1 brings accounts (it then needs a right: it tells about the server).
        group.MapGet("/info", (DatabaseOptions database) => TypedResults.Ok(new SystemInfo(AppVersion.Version, AppVersion.Commit, database.Provider)))
            .WithName("GetSystemInfo")
            .WithSummary("The running version and database provider.")
            .AllowAnonymous();
    }
}
