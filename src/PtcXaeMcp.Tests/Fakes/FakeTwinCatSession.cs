using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession : ITwinCatSession
{
    public bool IsConnected { get; set; }
    public bool SolutionOpen { get; set; }
    public string? ActiveSlnPath { get; set; }
    public string ProjectName { get; set; } = "FakeProject";

    public List<string> CallLog { get; } = new();
    public Exception? OpenThrows { get; set; }
    public Exception? CloseThrows { get; set; }
    public Exception? ConnectThrows { get; set; }
    public Func<bool>? ConnectCallback { get; set; }

    /// <summary>Thrown by <see cref="HasOpenSolution"/> when set (busy/blocked XAE simulation).</summary>
    public Exception? HasOpenSolutionThrows { get; set; }
    /// <summary>What <see cref="DescribeUnreachable"/> reports; null keeps the interface default.</summary>
    public string? UnreachableText { get; set; }

    bool ITwinCatSession.IsConnected => IsConnected;
    public string DescribeUnreachable() => UnreachableText ?? "No TwinCAT XAE instance is running.";
    public bool HasOpenSolution() => HasOpenSolutionThrows is null ? SolutionOpen : throw HasOpenSolutionThrows;
    public string? ActiveSolutionPath() => ActiveSlnPath;

    /// <summary>Simulates "no System Manager visible (yet)" — Probe throws like the COM probe.</summary>
    public Exception? ProbeThrows { get; set; }

    public TwinCatEnvironmentStatus Probe() => ProbeThrows is not null ? throw ProbeThrows : new(
        ProjectName, "FakePlc",
        ["FakePlc"], ["FakeMaster"]);

    public Task ConnectOrLaunchAsync(string? launchWithSlnPath, TimeSpan timeout, CancellationToken ct,
        PtcXaeMcp.WinForms.TwinCat.HostSelection.XaeHostClass? hostOverride = null)
    {
        CallLog.Add($"ConnectOrLaunch(launchWith={launchWithSlnPath ?? "<null>"}{(hostOverride is null ? "" : $", host={hostOverride}")})");
        if (ConnectThrows is not null) throw ConnectThrows;
        if (ConnectCallback is not null) IsConnected = ConnectCallback();
        return Task.CompletedTask;
    }

    /// <summary>What <see cref="CloseSolution"/> returns (prompt outcome etc.).</summary>
    public CloseSolutionResult CloseResult { get; set; } = new();

    /// <summary>When true, <see cref="CloseSolution"/> leaves the solution open (e.g. a cancelled prompt).</summary>
    public bool CloseLeavesSolutionOpen { get; set; }

    public CloseSolutionResult CloseSolution(bool saveFirst)
    {
        CallLog.Add($"CloseSolution(saveFirst={saveFirst})");
        if (CloseThrows is not null) throw CloseThrows;
        if (!CloseLeavesSolutionOpen)
        {
            SolutionOpen = false;
            ActiveSlnPath = null;
        }
        return CloseResult;
    }

    public void OpenSolution(string slnPath)
    {
        CallLog.Add($"OpenSolution({slnPath})");
        if (OpenThrows is not null) throw OpenThrows;
        SolutionOpen = true;
        ActiveSlnPath = slnPath;
    }

    public Exception? SaveThrows { get; set; }
    public void SaveAll()
    {
        CallLog.Add("SaveAll()");
        if (SaveThrows is not null) throw SaveThrows;
    }

    public SaveProjectResult? FakeSaveProjectResult { get; set; }

    public SaveProjectResult SaveProjectVerified()
    {
        CallLog.Add("SaveProjectVerified()");
        if (SaveThrows is not null) throw SaveThrows;
        return FakeSaveProjectResult
            ?? new SaveProjectResult(true, ActiveSlnPath, Array.Empty<SavedDocument>(),
                ActiveSlnPath is null ? "Saved." : $"Saved: {ActiveSlnPath}. No dirty editor documents were open.");
    }

    public string TipcDump { get; set; } = "(fake)";
    public string DumpTipcTree()
    {
        CallLog.Add("DumpTipcTree()");
        return TipcDump;
    }

    public string TincDump { get; set; } = "(fake)";
    public string DumpTincTree()
    {
        CallLog.Add("DumpTincTree()");
        return TincDump;
    }

    public void Dispose() { /* no-op: no unmanaged resources */ }
}
