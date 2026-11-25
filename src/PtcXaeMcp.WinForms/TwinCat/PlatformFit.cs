namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// What the open project says about where it runs: the active solution platform (the XAE's
/// ActiveTargetPlatform, e.g. 'TwinCAT OS (x64)') and the target NetId, plus the NetIds that are known to be
/// an x64 PC: the User Mode Runtime instances and the local system.
/// </summary>
internal sealed record ProjectTargetInfo(
    string? Platform,
    string? TargetNetId,
    IReadOnlyList<string> UmrNetIds,
    IReadOnlyList<string> LocalNetIds)
{
    public static ProjectTargetInfo Unknown { get; } = new(null, null, [], []);

    /// <summary>One sentence for tool results, e.g. "Platform: TwinCAT OS (x64), target 192.168.250.20.1.1."</summary>
    public string Describe() =>
        $"Platform: {(string.IsNullOrWhiteSpace(Platform) ? "unknown" : Platform)}, " +
        $"target: {(string.IsNullOrWhiteSpace(TargetNetId) ? "unknown" : TargetNetId)}.";
}

internal enum PlatformFitKind { Ok, Warn, Refuse }

/// <summary>Verdict of <see cref="PlatformFit.Check"/>; <see cref="Message"/> is empty for <see cref="PlatformFitKind.Ok"/>.</summary>
internal sealed record PlatformFitResult(PlatformFitKind Kind, string Message)
{
    public static PlatformFitResult Ok { get; } = new(PlatformFitKind.Ok, string.Empty);
}

/// <summary>
/// Issue #58: whether the project's platform fits its target. Only what is certain is judged: a User Mode Runtime
/// (and the local PC) is an x64 system, so an ARM platform cannot fit it, and a User Mode Runtime runs 'TwinCAT OS (x64)'.
/// Any other target (a remote device) is not judged: the platform may be anything there.
/// </summary>
internal static class PlatformFit
{
    public const string OsX64 = "TwinCAT OS (x64)";

    public static PlatformFitResult Check(ProjectTargetInfo info)
    {
        string? platform = info.Platform?.Trim();
        if (string.IsNullOrEmpty(platform)) return PlatformFitResult.Ok;

        bool arm  = platform.Contains("ARM", StringComparison.OrdinalIgnoreCase);
        bool os64 = string.Equals(platform, OsX64, StringComparison.OrdinalIgnoreCase);
        bool rt   = platform.StartsWith("TwinCAT RT", StringComparison.OrdinalIgnoreCase);

        string netId = info.TargetNetId?.Trim() ?? string.Empty;
        bool umr = netId.Length > 0 && info.UmrNetIds.Contains(netId, StringComparer.OrdinalIgnoreCase);
        if (umr)
        {
            if (os64) return PlatformFitResult.Ok;
            if (rt)
                return new(PlatformFitKind.Warn,
                    $"The platform is '{platform}', but target {netId} is a User Mode Runtime, which runs '{OsX64}'. " +
                    $"Its boot project is written for '{platform}'; set '{OsX64}' with twincat_set_target if the runtime does not load it.");
            return new(PlatformFitKind.Refuse,
                $"The platform is '{platform}', but target {netId} is a User Mode Runtime, which runs '{OsX64}'. " +
                $"A boot project written for '{platform}' does not load there (AdsError 1804, 'Error loading boot project'). " +
                $"Set the platform with twincat_set_target(amsNetId='{netId}', platform='{OsX64}'), rebuild, then activate again.");
        }

        bool local = netId.Length == 0 || netId == "0.0.0.0.0.0" ||
                     string.Equals(netId, "local", StringComparison.OrdinalIgnoreCase) ||
                     info.LocalNetIds.Contains(netId, StringComparer.OrdinalIgnoreCase);
        if (local && arm)
            return new(PlatformFitKind.Warn,
                $"The platform is '{platform}' (ARM), but the target is the local PC, which is an x64 system. " +
                "Set a TwinCAT RT (x64) or OS (x64) platform with twincat_set_target.");

        return PlatformFitResult.Ok;
    }
}
