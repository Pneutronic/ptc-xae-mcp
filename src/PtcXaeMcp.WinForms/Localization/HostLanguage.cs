using System.Globalization;
using Microsoft.Win32;

namespace PtcXaeMcp.WinForms.Localization;

/// <summary>
/// One language the host ships texts for: its code, its own name (as the language list shows it) and whether a native
/// speaker has checked the texts (an unchecked language is labeled as such in the list).
/// </summary>
internal sealed record HostLanguageInfo(string Code, string NativeName, bool Reviewed);

/// <summary>
/// The one language of this host process: window, dialogs, texts a human reads and the prompts. Tool results, the
/// <c>[Error]</c>/<c>Next:</c> lines and tool descriptions are not part of it (they stay English).
/// Resolved once at start and again when the Settings dialog saves a new choice; read through <see cref="Current"/>.
/// It is deliberately not a <see cref="CultureInfo"/> on a thread: nothing here may change how a tool formats a
/// number or a date.
/// </summary>
internal static class HostLanguage
{
    /// <summary>Environment variable that overrides every other source (a language code such as <c>de</c>).</summary>
    public const string EnvVar = "PTC_XAE_MCP_LANGUAGE";

    /// <summary>The neutral language: the text of every key, and the fallback for a missing key or language.</summary>
    public const string Fallback = "en";

    /// <summary>Where the installer leaves the language chosen in its wizard (seed for the first start).</summary>
    public const string InstallerKeyPath = @"SOFTWARE\Pneutronic\ptc-xae-mcp";
    public const string InstallerValueName = "InstallLanguage";

    /// <summary>The languages with a complete text set, in the order the language list shows them.</summary>
    public static readonly IReadOnlyList<HostLanguageInfo> Shipped =
    [
        new("en", "English", Reviewed: true),
        new("de", "Deutsch", Reviewed: false),
        new("fr", "Français", Reviewed: false),
        new("it", "Italiano", Reviewed: false),
        new("es", "Español", Reviewed: false),
        new("pt", "Português (Brasil)", Reviewed: false),
        new("nl", "Nederlands", Reviewed: false),
        new("pl", "Polski", Reviewed: false),
        new("tr", "T\u00fcrk\u00e7e", Reviewed: false), // escaped: the source stays free of German umlauts (EnglishOnlyCodeTests)
        new("zh", "中文（简体）", Reviewed: false),
    ];

    private static string _current = Fallback;

    /// <summary>The language code in force (always one of <see cref="Shipped"/>).</summary>
    public static string Current => _current;

    /// <summary>Raised after <see cref="Apply"/> changed the language.</summary>
    public static event Action? Changed;

    /// <summary>
    /// The shipped code a value stands for, or <c>null</c>: <c>de</c>, <c>DE</c>, <c>de-CH</c>, <c>de_AT</c> and the
    /// Windows names (<c>German</c>, <c>Deutsch</c>) all give <c>de</c>; anything else, empty or unknown gives <c>null</c>.
    /// </summary>
    public static string? Match(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string text = value.Trim();
        foreach (var language in Shipped)
        {
            if (string.Equals(text, language.NativeName, StringComparison.OrdinalIgnoreCase)) return language.Code;
            if (string.Equals(text, EnglishName(language.Code), StringComparison.OrdinalIgnoreCase)) return language.Code;
        }
        if (IsTraditionalChinese(text)) return null;
        int cut = text.IndexOfAny(['-', '_']);
        string primary = (cut < 0 ? text : text[..cut]).ToLowerInvariant();
        return Shipped.Any(l => l.Code == primary) ? primary : null;
    }

    private static string EnglishName(string code) => code switch
    {
        "en" => "English", "de" => "German", "fr" => "French", "it" => "Italian", "es" => "Spanish",
        "pt" => "Portuguese", "nl" => "Dutch", "pl" => "Polish", "tr" => "Turkish", "zh" => "Chinese", _ => code,
    };

    // Traditional Chinese (zh-TW, zh-HK, zh-MO, zh-Hant) is not Simplified Chinese: such a Windows gets English, not text
    // in a script its user does not read.
    private static bool IsTraditionalChinese(string text) =>
        text.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        && (text.Contains("Hant", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) || text.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith("-MO", StringComparison.OrdinalIgnoreCase) || text.EndsWith("_TW", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith("_HK", StringComparison.OrdinalIgnoreCase) || text.EndsWith("_MO", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The language to use: <see cref="EnvVar"/>, then the saved choice (<c>ui.json</c>), then the one the installer left,
    /// then the Windows display language, then English. A source that names an unknown language is skipped, never an error;
    /// <paramref name="ignored"/> receives the rejected environment value so the caller can log it.
    /// </summary>
    public static string Resolve(Func<string, string?> getEnv, string? saved, string? installerSeed, string? windowsUi,
        out string? ignored)
    {
        ignored = null;
        string? fromEnv = getEnv(EnvVar);
        if (Match(fromEnv) is { } env) return env;
        if (!string.IsNullOrWhiteSpace(fromEnv)) ignored = fromEnv.Trim();
        return Match(saved) ?? Match(installerSeed) ?? Match(windowsUi) ?? Fallback;
    }

    /// <summary>Makes <paramref name="code"/> the language in force; an unknown code leaves English.</summary>
    public static void Apply(string? code)
    {
        string next = Match(code) ?? Fallback;
        if (next == _current) return;
        _current = next;
        Changed?.Invoke();
    }

    /// <summary>The installer's value (HKLM first, then the current user), or <c>null</c> when the key is not there.</summary>
    public static string? ReadInstallerSeed()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(InstallerKeyPath);
                if (key?.GetValue(InstallerValueName) is string value && !string.IsNullOrWhiteSpace(value)) return value;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // No access to a hive is the same as no seed.
            }
        }
        return null;
    }
}
