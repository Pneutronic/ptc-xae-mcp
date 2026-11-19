using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-server write protection: the
// engineer's rule is "ask every time; it should be possible" — HMI writes are
// allowed, but EVERY one is confirmed by a human in a modal popup of the MCP host.
// No tool parameter bypasses it; a rejected/expired popup means nothing is written.

internal enum HmiWriteKind
{
    MapSymbol,
    UnmapSymbol,
    WriteSymbol,
    // hmi-views: project files
    AddItem,
    WriteFile,
    ImportFile,
    ApplyBundle,
    DeleteItem,
    // hmi-ads-tmc: offline symbols of an ADS runtime
    AttachTmcFile,
    DetachTmcFile,
    // hmi-ads-tmc #161: server-side TMC of an ADS runtime (USE_TMC)
    EnableRuntimeTmc,
    DisableRuntimeTmc,
    // hmi-internal-symbols (#165)
    AddInternalSymbol,
    RemoveInternalSymbol,
    MapOfflineSymbol,
    // hmi-offline-types
    CreateProjectDatatype,
    SwitchMappingToPlc,
}

/// <summary>What the popup shows the engineer before anything is written.</summary>
/// <param name="Symbol">Mapped name (map/unmap) or symbol name (write).</param>
/// <param name="NewValue">Internal name for a map, JSON value for a write, null for an unmap.</param>
/// <param name="CurrentValue">Best-effort current state (value / mapping), null when unknown.</param>
/// <param name="WritesToPlc">True when the write reaches the live PLC through the ADS domain.</param>
internal sealed record HmiWriteRequest(
    HmiWriteKind Kind,
    string HmiProject,
    string ProjectDirectory,
    string Symbol,
    string? Domain,
    string? NewValue,
    string? CurrentValue,
    bool WritesToPlc);

internal enum HmiConfirmOutcome
{
    /// <summary>The engineer pressed the confirm button.</summary>
    Confirmed,
    /// <summary>Test seam: TEMP fixture with PTC_XAE_MCP_HMI_AUTOCONFIRM_TEMP=1.</summary>
    AutoConfirmed,
    Rejected,
    TimedOut,
    /// <summary>No GUI to ask (host window gone) — fail closed.</summary>
    Unavailable,
}

internal interface IHmiWriteConfirmer
{
    /// <summary>Blocks until the engineer answered or <paramref name="timeout"/> passed.</summary>
    HmiConfirmOutcome Confirm(HmiWriteRequest request, TimeSpan timeout);
}

/// <summary>
/// Spec E2: the ONLY way around the popup, for the live test suite. It applies when
/// BOTH hold — the host process was started with <see cref="EnvVar"/>=1 AND the HMI
/// project lives below %TEMP% (the fixture copy). A project in a real repo never
/// qualifies, whatever the environment says.
/// </summary>
internal static class HmiAutoConfirmPolicy
{
    public const string EnvVar = "PTC_XAE_MCP_HMI_AUTOCONFIRM_TEMP";

    /// <param name="toLongPath">Expands 8.3 short names. Needed live: %TEMP% is
    /// "C:\Users\SOMEUS~1\…" while the XAE reports "C:\Users\some.user\…".</param>
    public static bool Applies(string? envValue, string? projectDirectory, string? tempDirectory,
        Func<string, string> toLongPath)
    {
        if (envValue?.Trim() != "1") return false;
        if (string.IsNullOrWhiteSpace(projectDirectory) || string.IsNullOrWhiteSpace(tempDirectory)) return false;

        string project, temp;
        try
        {
            project = WithSeparator(toLongPath(Path.GetFullPath(projectDirectory)));
            temp = WithSeparator(toLongPath(Path.GetFullPath(tempDirectory)));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                       or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }

        // Strictly below TEMP — the TEMP root itself does not count.
        return project.Length > temp.Length && project.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
    }

    private static string WithSeparator(string path) => path.TrimEnd('\\', '/') + "\\";
}

/// <summary>Decorator: auto-confirms under <see cref="HmiAutoConfirmPolicy"/>, otherwise asks.</summary>
internal sealed class HmiTempAutoConfirmer(
    IHmiWriteConfirmer inner,
    Func<string?> readEnvironment,
    Func<string> tempDirectory,
    Func<string, string> toLongPath) : IHmiWriteConfirmer
{
    public HmiConfirmOutcome Confirm(HmiWriteRequest request, TimeSpan timeout) =>
        HmiAutoConfirmPolicy.Applies(readEnvironment(), request.ProjectDirectory, tempDirectory(), toLongPath)
            ? HmiConfirmOutcome.AutoConfirmed
            : inner.Confirm(request, timeout);
}

internal static class Win32LongPath
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint bufferLength);

    /// <summary>Expands 8.3 components of an EXISTING path; returns the input unchanged
    /// when the path does not exist or the call fails.</summary>
    public static string Resolve(string path)
    {
        var buffer = new char[1024];
        uint length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        return length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : path;
    }
}

/// <summary>
/// ADS naming convention of the HMI server, verified on all 203 PLC mappings of two
/// real machine projects (113 and 90 mappings, read from their TcHmiSrv.Config.default.json):
/// mapName <c>ADS.PLC1.MAIN.fbHmi.bStart</c> ↔ MAPPING <c>PLC1::MAIN::fbHmi::bStart</c>.
/// Anything outside that shape (arrays, other domains) must be passed explicitly.
/// </summary>
internal static partial class HmiSymbolNames
{
    public const string AdsDomain = "ADS";
    public const string DefaultRuntime = "PLC1";

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$")]
    private static partial Regex PlcPathRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex RuntimeRegex();

    /// <summary>Is this a legal ADS runtime name (the key under ADS.Config::RUNTIMES and the
    /// <c>rootNamespace</c> of AttachTmcFile)?</summary>
    public static bool IsValidRuntime(string? runtime) =>
        !string.IsNullOrWhiteSpace(runtime) && RuntimeRegex().IsMatch(runtime.Trim());

    /// <summary>A server-extension configuration symbol (<c>ADS.Config</c>, <c>ADS.Config::RUNTIMES::PLC1</c>,
    /// <c>TcHmiSrv.Config::…</c>): its root before the first <c>::</c> is exactly <c>&lt;Extension&gt;.Config</c>
    /// (one dot — <c>ADS.PLC1.GVL.Config</c> is a PLC variable). A write to one changes the HMI server's
    /// configuration, never a PLC variable.</summary>
    public static bool IsServerConfigSymbol(string? symbol)
    {
        string name = symbol?.Trim() ?? "";
        int sep = name.IndexOf("::", StringComparison.Ordinal);
        string[] parts = (sep < 0 ? name : name[..sep]).Split('.');
        return parts.Length == 2 && parts[0].Length > 0
               && parts[1].Equals("Config", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Does a write to <paramref name="symbol"/> go through the ADS extension to a PLC variable?
    /// Server configuration (<see cref="IsServerConfigSymbol"/>) never does, even under <c>ADS.</c>.</summary>
    public static bool WritesToPlc(string? symbol, string? listedDomain) =>
        !IsServerConfigSymbol(symbol)
        && ((symbol?.Trim() ?? "").StartsWith(AdsDomain + ".", StringComparison.OrdinalIgnoreCase)
            || string.Equals(listedDomain, AdsDomain, StringComparison.OrdinalIgnoreCase));

    public static bool TryFromPlcPath(string? plcPath, string? runtime,
        out string mapName, out string internalName, out string? error)
    {
        mapName = internalName = "";
        string path = plcPath?.Trim() ?? "";
        string rt = string.IsNullOrWhiteSpace(runtime) ? DefaultRuntime : runtime.Trim();

        if (!RuntimeRegex().IsMatch(rt))
        {
            error = $"runtime '{rt}' is not a valid ADS runtime name (e.g. PLC1).";
            return false;
        }
        if (path.StartsWith(AdsDomain + ".", StringComparison.OrdinalIgnoreCase))
        {
            error = $"plcPath '{path}' looks like a mapped name — pass the PLC path without 'ADS.{rt}.' " +
                    "(e.g. 'MAIN.fbHmi.bStart'), or use mapName + internalName.";
            return false;
        }
        if (!PlcPathRegex().IsMatch(path))
        {
            error = $"plcPath '{path}' is not a plain dotted PLC path like 'MAIN.fbHmi.bStart' " +
                    "(arrays and other shapes: pass mapName + internalName explicitly).";
            return false;
        }

        mapName = $"{AdsDomain}.{rt}.{path}";
        internalName = $"{rt}::{path.Replace(".", "::", StringComparison.Ordinal)}";
        error = null;
        return true;
    }
}
