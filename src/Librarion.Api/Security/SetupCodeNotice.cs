using Librarion.Core.Access;
using Microsoft.Extensions.Options;

namespace Librarion.Api.Security;

/// <summary>
/// At start, while the server has no administrator: makes the setup code (when none is configured) and prints it in a
/// box. To the console only, never through a logger: logs are stored and shown in the app (SFE's lesson).
/// </summary>
public static class SetupCodeNotice
{
    public static async Task ShowIfNeededAsync(WebApplication app)
    {
        var setup = app.Services.GetRequiredService<SetupService>();
        if (!await setup.IsRequiredAsync())
            return;

        var options = app.Services.GetRequiredService<IOptionsMonitor<SetupOptions>>().CurrentValue;
        if (!string.IsNullOrWhiteSpace(options.Code))
        {
            app.Logger.LogWarning("Librarion has no administrator yet: open it in a browser and set it up with the configured setup code");
            return;
        }

        var generated = app.Services.GetRequiredService<GeneratedSetupCode>();
        string code;
        try
        {
            code = generated.Ensure();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            app.Logger.LogError(ex, "Cannot write the setup code to {Path}: set Librarion:Setup:Code instead", generated.FilePath);
            return;
        }

        Console.Out.WriteLine($"""

            ┌──────────────────────────────────────────────────────────┐
              Librarion needs its first administrator.
              Open it in a browser and enter this setup code:

                  {code}

              (also in {generated.FilePath})
            └──────────────────────────────────────────────────────────┘

            """);
        app.Logger.LogWarning("Librarion has no administrator yet: the setup code is in {Path}", generated.FilePath);
    }
}
