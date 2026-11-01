using System.Text.Json;
using System.Text.Json.Serialization;
using Librarion.Api;
using Librarion.Api.Endpoints;
using Librarion.Api.Security;
using Librarion.Core;
using Librarion.Core.Access;
using Librarion.Core.Data;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.Json;

var builder = WebApplication.CreateBuilder(args);

// This machine's own values (appsettings.Local.json, git-ignored) right after appsettings.json, so that
// appsettings.{Environment}.json, the environment (LIBRARION__DATABASE__PROVIDER…) and the command line still win.
var local = new JsonConfigurationSource { Path = Path.Combine(builder.Environment.ContentRootPath, "appsettings.Local.json"), Optional = true, ReloadOnChange = true };
local.ResolveFileProvider();
var main = builder.Configuration.Sources.ToList().FindIndex(source => source is JsonConfigurationSource { Path: "appsettings.json" });
builder.Configuration.Sources.Insert(main + 1, local);

// `dotnet run` without a launch profile runs as Production: without this, the client's files built into wwwroot
// aren't found ("Static Web Assets are not enabled"). A published app has no such manifest: then it does nothing.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddLibrarionCore(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddLibrarionSecurity(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton<ClientPage>();

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Librarion API";
    document.Info.Version = AppVersion.Version;
    return Task.CompletedTask;
}));

var app = builder.Build();
// Disposing the configuration leaves the file provider it did not create watching (an inotify instance on Linux,
// 128 per user): the tests start many servers in one process (SFE's finding).
app.Lifetime.ApplicationStopped.Register(() => (local.FileProvider as IDisposable)?.Dispose());
app.Logger.LogInformation("Librarion {Version} ({Commit})", AppVersion.Version, AppVersion.Commit ?? "unknown commit");

app.UseForwardedHeaders();
// First, so that every answer (errors too) carries them.
app.UseMiddleware<SecurityHeaders>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseMiddleware<RequestGuard>();
app.UseRateLimiter();
app.UseAuthorization();

app.MapOpenApi().RequireRight(Rights.ApiDocs);

app.UseDefaultFiles();
app.MapStaticAssets();

var api = app.MapGroup("/api");
api.MapSetupEndpoints();
api.MapSessionEndpoints();
api.MapSystemEndpoints();
api.MapTenantEndpoints();
api.MapLibraryEndpoints();
api.MapBookEndpoints();
api.MapUserEndpoints();
api.MapGroupEndpoints();
api.MapSettingsEndpoints();
api.MapSecurityEventEndpoints();
api.MapFallback(() => TypedResults.NotFound()).AllowAnonymous();

// Addresses the client routes itself: its page, with the app's root as its base (ClientPage).
app.MapFallback((HttpContext context, ClientPage page) => page.Serve(context));

await LibrarionDatabase.InitializeAsync(
    app.Services.GetRequiredService<IDbContextFactory<LibrarionDbContext>>(), AppVersion.Version, app.Logger);
await SetupCodeNotice.ShowIfNeededAsync(app);

await app.RunAsync();

/// <summary>Entry point marker for integration tests.</summary>
public partial class Program;
