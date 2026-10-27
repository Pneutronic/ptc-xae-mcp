namespace PtcXaeMcp.WinForms.TwinCat.HostSelection;

/// <summary>What <c>twincat_open_project</c> tells about the XAE host: one info sentence plus any warnings.</summary>
internal sealed record HostReport(string Info, IReadOnlyList<string> Warnings);

/// <summary>
/// Plain-language reporting of the XAE host (32-bit vs 64-bit TcXaeShell) — which one a project's
/// TcVersion selects, which one is running, and what a mismatch usually means. Pure: the running
/// hosts and the installed exes are passed in, so tests need neither COM nor the registry.
/// </summary>
internal static class XaeHostReport
{
    internal static string Name(string progId) => progId switch
    {
        XaeHostResolver.ProgId64 => $"64-bit TcXaeShell ({XaeHostResolver.ProgId64})",
        XaeHostResolver.ProgId32 => $"32-bit TcXaeShell ({XaeHostResolver.ProgId32})",
        _ => progId,
    };

    /// <summary>The host the solution's .tsproj TcVersion selects, or null when no TcVersion can be read.</summary>
    internal static HostReport? ForSolution(
        string slnPath,
        XaeHostResolver resolver,
        Func<string, bool> exeExists,
        Func<IReadOnlyList<string>> runningHosts)
    {
        string? tsproj = TsprojLocator.FindForSolution(slnPath);
        if (tsproj is null || !resolver.TryResolveFromTsprojFile(tsproj, out var host, out _) || host is null)
            return null;

        var warnings = new List<string>();
        IReadOnlyList<string> runningProgIds = runningHosts();
        string selected = Name(host.ProgId);

        if (runningProgIds.Count > 0 && !runningProgIds.Contains(host.ProgId, StringComparer.OrdinalIgnoreCase))
            warnings.Add(
                $"[Warn] TcVersion {host.TcVersion} selects the {selected}, but the running XAE is " +
                $"{string.Join(" and ", runningProgIds.Select(Name))}. Tools can fail in a host that does not match the project.");

        if (host.HostClass == XaeHostClass.Shell32 && exeExists(resolver.Resolve(XaeHostClass.Shell64).ExePath))
            warnings.Add(
                $"[Warn] TcVersion {host.TcVersion} selects the 32-bit XAE although the 64-bit XAE is installed. " +
                "Tools can fail in the 32-bit host; for a project meant for TwinCAT 4026 or later, " +
                "update its TcVersion and reopen it in the 64-bit XAE.");

        return new HostReport($"Host: {selected}, selected by TcVersion {host.TcVersion}.", warnings);
    }

    /// <summary>
    /// Context appended to a tool's binding failure (a RuntimeBinderException on the DTE): such an error
    /// often means the solution is open in an XAE whose bitness does not fit its TcVersion.
    /// </summary>
    internal static string BindingFailureContext(IReadOnlyList<string> runningProgIds, bool installed64) =>
        "Host context: attached XAE " +
        (runningProgIds.Count == 0 ? "not detected" : string.Join(" and ", runningProgIds.Select(Name))) +
        $"; 64-bit XAE {(installed64 ? "installed" : "not installed")}. " +
        "A missing-member error on the DTE often means the solution is open in an XAE of the wrong bitness for its " +
        "TcVersion (e.g. the 32-bit XAE for a 4026 project): check twincat_resolve_host, then close that XAE and " +
        "reopen the solution.";

    /// <summary>The XAE hosts currently registered in the Running Object Table.</summary>
    internal static IReadOnlyList<string> RunningHosts() =>
        new[] { XaeHostResolver.ProgId64, XaeHostResolver.ProgId32 }
            .Where(ComInterop.IsProgIdRunning)
            .ToList();

    /// <summary><see cref="BindingFailureContext"/> for this box; null when it cannot be determined.</summary>
    internal static string? CurrentBindingContext()
    {
        try
        {
            string exe64 = new XaeHostResolver().Resolve(XaeHostClass.Shell64).ExePath;
            return BindingFailureContext(RunningHosts(), File.Exists(exe64));
        }
        catch (Exception) { return null; } // diagnostics must never mask the error being reported
    }
}
