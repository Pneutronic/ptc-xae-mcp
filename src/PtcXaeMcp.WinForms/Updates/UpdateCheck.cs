using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Win32;

namespace PtcXaeMcp.WinForms.Updates;

/// <summary>What a check found.</summary>
internal enum UpdateCheckKind
{
    /// <summary>The installed version is the latest release (or newer, e.g. a local build).</summary>
    UpToDate,

    /// <summary>A newer release exists; <see cref="UpdateCheckOutcome.Latest"/> names it.</summary>
    UpdateAvailable,

    /// <summary>The release page could not be reached or understood; nothing is known.</summary>
    Unavailable,
}

/// <param name="Kind">See <see cref="UpdateCheckKind"/>.</param>
/// <param name="Current">The installed version (without build metadata), or <c>null</c> when unparsable.</param>
/// <param name="Latest">The latest released version, when known.</param>
/// <param name="Message">One English sentence for the log and the GUI.</param>
internal sealed record UpdateCheckOutcome(UpdateCheckKind Kind, Version? Current, Version? Latest, string Message)
{
    /// <summary>The page a person should open: the latest release when one is newer, else the releases list.</summary>
    public string Url => Latest is { } v && Kind == UpdateCheckKind.UpdateAvailable
        ? UpdateCheck.ReleaseTagUrl(v)
        : UpdateCheck.ReleasesUrl;
}

/// <summary>
/// The opt-in "is there a newer release?" check. It never downloads or runs anything: it sends
/// ONE request to GitHub, the redirect of <see cref="LatestUrl"/>, and reads the version from the
/// <c>Location</c> header (<c>…/releases/tag/v2.3.0</c>). No API, no JSON, no token, and nothing
/// but the User-Agent (product and version) leaves the machine. The MCP exposes no tool for it:
/// a check runs only from the host window (at start when enabled in Settings, or from the menu).
/// <para>
/// Order of precedence: the machine policy (<see cref="PolicyKeyPath"/>, <see cref="PolicyValueName"/>
/// = 1, HKLM so only an administrator can set it) switches the check off for every user and hides
/// the setting; then <see cref="EnvVar"/> = 0 switches it off for this process; then the per-user
/// setting in ui.json (<c>checkForUpdates</c>, default off).
/// </para>
/// </summary>
internal static class UpdateCheck
{
    public const string ReleasesUrl = "https://github.com/Pneutronic/ptc-xae-mcp/releases";
    public const string LatestUrl = ReleasesUrl + "/latest";

    /// <summary><c>PTC_XAE_MCP_UPDATE_CHECK=0</c> disables the check for this process (scripts, CI, kiosks).</summary>
    public const string EnvVar = "PTC_XAE_MCP_UPDATE_CHECK";

    /// <summary>Machine policy key (under HKEY_LOCAL_MACHINE).</summary>
    public const string PolicyKeyPath = @"SOFTWARE\Policies\Pneutronic\ptc-xae-mcp";

    /// <summary>DWORD 1 under <see cref="PolicyKeyPath"/>: no update check, for any user, ever.</summary>
    public const string PolicyValueName = "DisableUpdateCheck";

    /// <summary>The whole request must finish within this; a blocked network must not delay the start.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static string ReleaseTagUrl(Version v) => $"{ReleasesUrl}/tag/v{v.ToString(3)}";

    // ---- policy ----------------------------------------------------------------------------

    /// <summary>True when the machine policy forbids the check (reads HKLM; never throws).</summary>
    public static bool IsDisabledByPolicy()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKeyPath);
            return IsDisabledByPolicy(key?.GetValue(PolicyValueName));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The policy value as read from the registry: only a DWORD/QWORD 1 (or the string "1") disables.</summary>
    public static bool IsDisabledByPolicy(object? registryValue) => registryValue switch
    {
        int i => i == 1,
        long l => l == 1,
        string s => s.Trim() == "1",
        _ => false,
    };

    /// <summary>True when <see cref="EnvVar"/> is set to <c>0</c> (case/whitespace-insensitive).</summary>
    public static bool IsDisabledByEnvironment(Func<string, string?> getEnv) =>
        getEnv(EnvVar)?.Trim() == "0";

    /// <summary>
    /// Whether a check may run now. <paramref name="userOptIn"/> is the ui.json setting.
    /// </summary>
    public static bool IsEnabled(bool userOptIn, bool disabledByPolicy, Func<string, string?> getEnv) =>
        userOptIn && !disabledByPolicy && !IsDisabledByEnvironment(getEnv);

    /// <summary>The Settings-dialog row text: "on", "off", "off (machine policy)" or "off (PTC_XAE_MCP_UPDATE_CHECK=0)".</summary>
    public static string DescribeState(bool userOptIn, bool disabledByPolicy, Func<string, string?> getEnv)
    {
        if (disabledByPolicy) return "off (machine policy)";
        if (IsDisabledByEnvironment(getEnv)) return $"off ({EnvVar}=0)";
        return userOptIn ? "on" : "off";
    }

    // ---- pure evaluation ----------------------------------------------------------------------

    /// <summary>
    /// The version in a release-tag URL or tag (<c>…/releases/tag/v2.3.0</c>, <c>v2.3.0</c>, <c>2.3.0</c>),
    /// or <c>null</c> for anything else (a redirect to the releases list means "no release yet").
    /// </summary>
    public static Version? ParseTagVersion(string? locationOrTag)
    {
        if (string.IsNullOrWhiteSpace(locationOrTag)) return null;
        string s = locationOrTag.Trim();
        int slash = s.LastIndexOf('/');
        if (slash >= 0)
        {
            // Only a /releases/tag/<tag> URL carries a version; anything else is not a release.
            if (!s.Contains("/releases/tag/", StringComparison.OrdinalIgnoreCase)) return null;
            s = s[(slash + 1)..];
        }
        s = Uri.UnescapeDataString(s);
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        return ParseVersion(s);
    }

    /// <summary>The installed version without build metadata (<c>2.3.0+abc</c> → 2.3.0), or <c>null</c>.</summary>
    public static Version? ParseCurrent(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;
        string s = informationalVersion;
        int plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) s = s[..plus];
        int dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0) s = s[..dash];
        return ParseVersion(s);
    }

    private static Version? ParseVersion(string s)
    {
        // Version.TryParse accepts "2.3" and "2.3.0.0"; normalise to three parts so 2.3 == 2.3.0.
        if (!Version.TryParse(s, out var v) || v.Major < 0) return null;
        return new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
    }

    /// <summary>Compares the installed version with the redirect target of the latest-release page.</summary>
    public static UpdateCheckOutcome Evaluate(string? currentInformationalVersion, string? latestLocation)
    {
        Version? current = ParseCurrent(currentInformationalVersion);
        Version? latest = ParseTagVersion(latestLocation);
        if (latest is null)
            return new UpdateCheckOutcome(UpdateCheckKind.Unavailable, current, null,
                "Update check: the latest release could not be determined.");
        if (current is null)
            return new UpdateCheckOutcome(UpdateCheckKind.Unavailable, null, latest,
                $"Update check: the installed version is unknown; the latest release is {latest.ToString(3)}.");
        if (latest > current)
            return new UpdateCheckOutcome(UpdateCheckKind.UpdateAvailable, current, latest,
                $"Update available: {latest.ToString(3)} (installed: {current.ToString(3)}). {ReleaseTagUrl(latest)}");
        return new UpdateCheckOutcome(UpdateCheckKind.UpToDate, current, latest,
            $"Up to date: {current.ToString(3)} is the latest release.");
    }

    // ---- the one network request ------------------------------------------------------------

    /// <summary>
    /// Runs the check: one GET of <see cref="LatestUrl"/> without following the redirect. Any
    /// failure (no network, proxy, timeout, unexpected status) yields <see cref="UpdateCheckKind.Unavailable"/>
    /// with the reason in the message; it never throws.
    /// </summary>
    public static async Task<UpdateCheckOutcome> RunAsync(string currentInformationalVersion, CancellationToken ct = default)
    {
        string? location;
        try
        {
            location = await FetchLatestLocationAsync(currentInformationalVersion, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                       or InvalidOperationException or UriFormatException)
        {
            return new UpdateCheckOutcome(UpdateCheckKind.Unavailable, ParseCurrent(currentInformationalVersion), null,
                $"Update check: github.com not reachable ({ex.GetType().Name}: {ex.Message}).");
        }
        return Evaluate(currentInformationalVersion, location);
    }

    /// <summary>The <c>Location</c> header of the latest-release redirect, or <c>null</c> when there is none.</summary>
    internal static async Task<string?> FetchLatestLocationAsync(string currentInformationalVersion, CancellationToken ct)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ptc-xae-mcp",
            ParseCurrent(currentInformationalVersion)?.ToString(3) ?? "0.0.0"));
        using var request = new HttpRequestMessage(HttpMethod.Head, LatestUrl);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } loc)
            return loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(LatestUrl), loc).ToString();
        return null;
    }
}
