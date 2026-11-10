namespace Librarion.Core.Access;

/// <summary><c>Librarion:Security</c>: sign-in, sessions and the security log.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Librarion:Security";

    /// <summary>Wrong passwords from one address for one name before that address waits (<see cref="LockoutMinutes"/>).</summary>
    public int MaxFailedSignIns { get; set; } = 5;

    /// <summary>Wrong passwords in a row from all addresses before the account itself is locked; 0 = never.</summary>
    public int AccountLockoutFailures { get; set; } = 20;

    public int LockoutMinutes { get; set; } = 15;

    /// <summary>How long a remembered session lasts without use.</summary>
    public int SessionDays { get; set; } = 30;

    /// <summary>Sign-in attempts per address and minute (all names together).</summary>
    public int SignInsPerMinute { get; set; } = 20;

    /// <summary>Requests per minute of one reader app's account (or address) on the OPDS feeds, covers and downloads included.</summary>
    public int OpdsRequestsPerMinute { get; set; } = 600;

    public int MinimumPasswordLength { get; set; } = 8;

    /// <summary>Where the keys signing the session cookies are kept (relative to the application's folder).</summary>
    public string KeysDirectory { get; set; } = "data/keys";

    /// <summary>Marks the session cookie secure always (the server is only reached over https).</summary>
    public bool RequireHttps { get; set; }

    /// <summary>Reverse proxies (addresses or networks) whose forwarded headers are believed, beside this computer's.</summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>Days the security log keeps its events.</summary>
    public int EventDays { get; set; } = 365;
}

/// <summary><c>Librarion:Setup</c>: the first administrator.</summary>
public sealed class SetupOptions
{
    public const string SectionName = "Librarion:Setup";
    public const int MinimumCodeLength = 12;

    /// <summary>The code the setup page asks for; without one, the server makes one (<see cref="GeneratedSetupCode"/>).</summary>
    public string? Code { get; set; }

    /// <summary>Where a generated code is kept (relative to the application's folder); recovery codes go beside it.</summary>
    public string CodeFile { get; set; } = "data/setup-code.txt";

    /// <summary>Opens the setup page again on a set-up server, to reset or add a local server administrator.</summary>
    public bool Recover { get; set; }

    /// <summary>The code came from <see cref="CodeFile"/>, not the configuration (set by <see cref="GeneratedSetupCode"/>).</summary>
    public bool CodeIsGenerated { get; set; }

    public bool HasUsableCode => Code?.Trim() is { Length: >= MinimumCodeLength };

    /// <summary>A generated code compares without dashes, spaces and case; a configured one exactly. Constant time.</summary>
    public bool Matches(string? given)
    {
        if (!HasUsableCode || string.IsNullOrEmpty(given))
            return false;
        return CodeIsGenerated
            ? PasswordHashing.SameSecret(GeneratedSetupCode.Normalize(given), GeneratedSetupCode.Normalize(Code))
            : PasswordHashing.SameSecret(given.Trim(), Code!.Trim());
    }
}

/// <summary><c>Librarion:Rights</c>: rights the server takes from everyone.</summary>
public sealed class RightOptions
{
    public const string SectionName = "Librarion:Rights";

    /// <summary>Ids, <c>prefix*</c>, or <c>kind:write</c> (every right that changes something).</summary>
    public List<string> Disabled { get; set; } = [];
}
