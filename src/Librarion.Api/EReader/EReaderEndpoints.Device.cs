using Librarion.Api.EReader.Pages;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Librarion.Api.EReader;

/// <summary>
/// The device chooser (stage 8 chunk 7, D-8.8): which device's formats come first on book pages and in lists, guessed
/// from the User-Agent unless this browser chose (a form, the choice kept in a cookie: <see cref="EReaderDevices"/>).
/// </summary>
public static partial class EReaderEndpoints
{
    private static void MapDevice(RouteGroupBuilder e)
    {
        e.MapGet("/device", DevicePageAsync).RequireRight(Rights.Browse);
        e.MapPost("/device", ChooseDeviceAsync).RequireRight(Rights.Browse);
    }

    /// <summary>The form: Automatic (with what was guessed), each device with the formats it opens, another device (no order).</summary>
    private static async Task<IResult> DevicePageAsync(string? @return, HttpContext context) =>
        new RazorComponentResult<DevicePage>(new
        {
            Frame = await EReaderFrame.ForAsync(context),
            ReturnTo = SafeReturn(@return),
            Guessed = EReaderDevices.Guess(context.Request.Headers.UserAgent),
            Chosen = EReaderDevices.Chosen(context.Request) ?? EReaderDevices.Automatic,
        });

    /// <summary>The choice posted (its token checked by <see cref="EReaderGuard"/>): kept for this browser, then back where it was made.</summary>
    private static async Task<IResult> ChooseDeviceAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var choice = form["device"].ToString();
        if (choice != EReaderDevices.Automatic && choice != EReaderDevices.NoDevice && EReaderDevices.Find(choice) is null)
            return TypedResults.BadRequest();
        EReaderDevices.Keep(context, choice);
        return TypedResults.Redirect(new EReaderLinks(context).Back(SafeReturn(form["return"])));
    }
}
