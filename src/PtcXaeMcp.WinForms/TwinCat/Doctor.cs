using System.Globalization;
using System.Text;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>One check of twincat_doctor: Status is ok, warn or fail; Next is the concrete next step, if any.</summary>
internal sealed record DoctorCheck(string Name, string Status, string Detail, string? Next = null);

/// <summary>The sources the doctor asks; <see cref="Live"/> are the real ones.</summary>
internal sealed record DoctorProbes(
    Func<string> McpVersion,
    Func<SystemServiceStatus> SystemService,
    Func<LocalAmsNetId> LocalNetId,
    Func<int?> XaeProcessCount,
    Func<string?> BlockingDialog,
    Func<string?, IReadOnlyList<string>> AllowedRoots)
{
    internal static DoctorProbes Live => new(
        McpBridge.ThisAssemblyVersion,
        TwinCatSystemService.Query,
        LocalAmsNetIdResolver.Shared.Resolve,
        XaeProcessProbe.Count,
        XaeModalProbe.FindBlockingDialog,
        Security.PathPolicy.AllowedRootsFor);
}

// twincat_doctor: one look at everything the tools depend on, each with a verdict and the next step — instead of a
// failing tool call explaining one cause at a time. It only reads: it never starts an XAE (one XAE at a time) or the
// runtime, and every call that could hang on a blocked XAE is bounded.
internal sealed class DoctorCoordinator(ITwinCatSession session, DoctorProbes probes, TimeSpan? xaeTimeout = null)
{
    private readonly TimeSpan _xaeTimeout = xaeTimeout ?? TimeSpan.FromSeconds(5);

    public IReadOnlyList<DoctorCheck> Run()
    {
        var checks = new List<DoctorCheck> { new("MCP server", "ok", $"ptc-xae-mcp {Safe(probes.McpVersion) ?? "?"}") };

        var service = probes.SystemService();
        checks.Add(service.State switch
        {
            SystemServiceState.Running => new("TwinCAT System Service", "ok", "running"),
            SystemServiceState.NotRunning => new("TwinCAT System Service", "fail", service.Detail,
                "Start the TwinCAT System Service (TcSysSrv); ADS tools, activation and login need it. If it does not start, reboot."),
            _ => new("TwinCAT System Service", "warn", $"state unknown: {service.Detail}"),
        });

        var netId = probes.LocalNetId();
        checks.Add(netId.Source == LocalNetIdSource.Router
            ? new("Local AMS NetId", "ok", $"{netId.NetId} (from the AMS router)")
            : new("Local AMS NetId", "warn", $"{netId.NetId} ({netId.Source}; router: {netId.RouterError ?? "no answer"})",
                "The AMS router did not answer — check that the TwinCAT System Service runs."));

        int? xae = probes.XaeProcessCount();
        string? dialog = xae > 0 ? Safe(probes.BlockingDialog) : null;
        checks.Add(xae switch
        {
            null => new("XAE", "warn", "TcXaeShell process count unknown (probe failed)"),
            0 => new("XAE", "warn", "no TcXaeShell running", "twincat_open_project starts the XAE with a solution."),
            1 => new("XAE", "ok", "1 TcXaeShell running"),
            _ => MultiXae(xae.Value),
        });
        if (dialog is not null)
            checks.Add(new("XAE dialog", "fail", $"the XAE is blocked by '{dialog}'",
                "Answer or close the dialog in the XAE; until then every XAE tool is refused."));

        if (xae > 0 && dialog is null) checks.Add(Solution());

        var roots = Safe(() => probes.AllowedRoots(Safe(session.ActiveSolutionPath) is string sln ? Path.GetDirectoryName(sln) : null));
        checks.Add(new("File paths", "ok", roots is null ? "allowed roots unknown" : "allowed roots: " + string.Join("; ", roots)));
        return checks;
    }

    private DoctorCheck MultiXae(int count)
    {
        var walk = Task.Run(session.MultiXaeBlockReason);
        if (!walk.Wait(_xaeTimeout))
            return new("XAE", "fail", $"{count} TcXaeShell running and one did not answer within {_xaeTimeout.TotalSeconds:0} s",
                "An XAE instance is hung or blocked by a dialog — close the one you do not use.");
        string? block = walk.IsFaulted ? null : walk.Result;
        return block is null
            ? new("XAE", "warn", $"{count} TcXaeShell running — bound to the one with the expected solution", "Close the XAE you do not use.")
            : new("XAE", "fail", block.Replace("[Error] ", ""), "Close the XAE you do not use.");
    }

    private DoctorCheck Solution()
    {
        var probe = Task.Run(() => (Connected: session.IsConnected, Open: session.IsConnected && session.HasOpenSolution(), Path: session.ActiveSolutionPath()));
        if (!probe.Wait(_xaeTimeout))
            return new("Solution", "fail", $"the XAE did not answer within {_xaeTimeout.TotalSeconds:0} s", "Check the XAE for a dialog or a long-running operation.");
        if (probe.IsFaulted)
            return new("Solution", "warn", $"could not be read: {probe.Exception?.GetBaseException().Message}");
        var (connected, open, path) = probe.Result;
        if (!connected) return new("Solution", "warn", "the MCP is not attached to the XAE", "twincat_open_project attaches and opens a solution.");
        return open
            ? new("Solution", "ok", path ?? "open")
            : new("Solution", "warn", "no solution open", "twincat_open_project opens one.");
    }

    internal static string Format(IReadOnlyList<DoctorCheck> checks)
    {
        int Count(string s) => checks.Count(c => c.Status == s);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Doctor: {Count("ok")} ok, {Count("warn")} warning{(Count("warn") == 1 ? "" : "s")}, {Count("fail")} failure{(Count("fail") == 1 ? "" : "s")}.");
        foreach (var c in checks)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n  [{c.Status}] {c.Name} — {c.Detail}");
            if (c.Next is not null) sb.Append("\n      Next: ").Append(c.Next);
        }
        return sb.ToString();
    }

    private static T? Safe<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch (Exception) { return null; } // a diagnosis must report, never throw
    }
}
