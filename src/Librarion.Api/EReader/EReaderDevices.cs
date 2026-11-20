namespace Librarion.Api.EReader;

/// <summary>An e-ink device family and the formats it opens, best first (D-8.8).</summary>
/// <param name="Key">Kept in the choice cookie and the form (<c>kindle</c>…).</param>
/// <param name="Name">The family's own name, shown as it is (a product name, not translated).</param>
/// <param name="Formats">Format keys (<see cref="Core.Formats.BookFormats"/>) the device's own reader opens, the one to prefer first.</param>
public sealed record EReaderDevice(string Key, string Name, IReadOnlyList<string> Formats)
{
    /// <summary>The device's own reader opens this format.</summary>
    public bool Opens(string format) => Formats.Contains(format, StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a format comes in the device's order: its place in <see cref="Formats"/>, the others after them all.</summary>
    public int Rank(string format)
    {
        for (var i = 0; i < Formats.Count; i++)
            if (string.Equals(Formats[i], format, StringComparison.OrdinalIgnoreCase))
                return i;
        return Formats.Count;
    }
}

/// <summary>
/// The device a browser is on (D-8.8, Q5): guessed from its User-Agent (Kindle, Kobo, PocketBook, Tolino, Boox; anything
/// else none), or chosen on <c>/e/device</c> and kept in a cookie for this browser. Its formats come first on a book
/// page and in lists, the others after them dimmed, still downloadable (stage 9 adds "converted for this device").
/// </summary>
public static class EReaderDevices
{
    /// <summary>The choice of this browser (a device's key, or <see cref="NoDevice"/>); absent: guessed from the User-Agent.</summary>
    public const string CookieName = "librarion.device";

    /// <summary>The choice "another device": no order, nothing dimmed.</summary>
    public const string NoDevice = "none";

    /// <summary>The choice "automatic" in the form: the cookie removed.</summary>
    public const string Automatic = "auto";

    /// <summary>The families known, in the form's order. The lists are what each one's own reader opens (not its browser's viewer).</summary>
    public static IReadOnlyList<EReaderDevice> All { get; } =
    [
        new("kindle", "Kindle", ["azw3", "azw", "mobi", "pdf", "txt"]),
        new("kobo", "Kobo", ["kepub", "epub", "pdf", "cbz", "cbr", "mobi", "txt", "rtf"]),
        new("pocketbook", "PocketBook", ["fb2", "epub", "pdf", "djvu", "mobi", "azw3", "azw", "cbz", "cbr", "txt", "rtf", "doc", "docx"]),
        new("tolino", "Tolino", ["epub", "pdf", "txt"]),
        new("boox", "Boox", ["epub", "fb2", "pdf", "djvu", "azw3", "azw", "mobi", "cbz", "cbr", "txt", "rtf", "doc", "docx"]),
    ];

    /// <summary>A family by its key; null for none or an unknown key.</summary>
    public static EReaderDevice? Find(string? key) => All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// The family a User-Agent names; null when it names none (a phone, a desktop, a device not known). Kindle by its
    /// <c>Kindle/</c> token, never a Fire tablet (<c>Silk</c>, its browser); Kobo, PocketBook, Tolino by name; Boox by
    /// <c>BOOX</c> or <c>Onyx</c> (its models without either are not recognised: they get no order, which is safe).
    /// </summary>
    public static EReaderDevice? Guess(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
            return null;
        bool Has(string token) => userAgent.Contains(token, StringComparison.OrdinalIgnoreCase);
        var key = Has("Kindle/") && !Has("Silk") ? "kindle"
            : Has("Kobo") ? "kobo"
            : Has("PocketBook") ? "pocketbook"
            : Has("tolino") ? "tolino"
            : Has("BOOX") || Has("Onyx") ? "boox"
            : null;
        return Find(key);
    }

    /// <summary>The device of this request: the choice kept in the cookie (none chosen: null), else the guess.</summary>
    public static EReaderDevice? Of(HttpRequest request) => Chosen(request) is { } choice
        ? Find(choice)
        : Guess(request.Headers.UserAgent);

    /// <summary>What this browser chose (a device's key or <see cref="NoDevice"/>); null when it chose nothing (automatic).</summary>
    public static string? Chosen(HttpRequest request) =>
        request.Cookies[CookieName] is { } value && (value == NoDevice || Find(value) is not null) ? value : null;

    /// <summary>The items in the device's order (its formats first, by its preference), the others after in their own order; no device: as they are.</summary>
    public static List<T> Order<T>(EReaderDevice? device, IEnumerable<T> items, Func<T, string> format) =>
        device is null ? items.ToList() : items.OrderBy(i => device.Rank(format(i))).ToList();

    /// <summary>Keeps the choice for a year (or removes it for <see cref="Automatic"/>), for the <c>/e</c> pages of this browser only.</summary>
    public static void Keep(HttpContext context, string choice)
    {
        var options = new CookieOptions
        {
            Path = (context.Request.PathBase.Value?.TrimEnd('/') ?? "") + EReaderEndpoints.Prefix,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            IsEssential = true,
        };
        if (choice == Automatic)
        {
            context.Response.Cookies.Delete(CookieName, options);
            return;
        }
        options.MaxAge = TimeSpan.FromDays(365);
        context.Response.Cookies.Append(CookieName, choice, options);
    }
}
