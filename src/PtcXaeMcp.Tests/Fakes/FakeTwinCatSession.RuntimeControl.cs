using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- Runtime control surface -------------------------------------------

    public List<BroadcastTarget> FakeBroadcastTargets { get; } = new();
    public Exception? BroadcastSearchThrows { get; set; }
    public Exception? AddRouteThrows { get; set; }
    public Exception? SetTargetThrows { get; set; }
    public Exception? RestartRuntimeThrows { get; set; }
    public string FakeAddRouteResult { get; set; } = "Added project route to 1.2.3.4.1.1.";
    public string FakeSetTargetResult { get; set; } = "Target set to 1.2.3.4.1.1.";
    public string FakeRestartRuntimeResult { get; set; } = "Runtime restart initiated on target.";

    public IReadOnlyList<BroadcastTarget> BroadcastSearch(string? searchTarget)
    {
        CallLog.Add($"BroadcastSearch(target={searchTarget ?? "<null>"})");
        if (BroadcastSearchThrows is not null) throw BroadcastSearchThrows;
        return FakeBroadcastTargets;
    }

    public string AddRoute(string netId, string? ipAddr, string? hostName, string? userName, string? password, bool projectRoute)
    {
        CallLog.Add($"AddRoute(netId={netId}, ip={ipAddr ?? "<null>"}, host={hostName ?? "<null>"}, projectRoute={projectRoute})");
        if (AddRouteThrows is not null) throw AddRouteThrows;
        return FakeAddRouteResult;
    }

    public string SetTarget(string netId, string? platform)
    {
        CallLog.Add($"SetTarget(netId={netId}, platform={platform ?? "<null>"})");
        if (SetTargetThrows is not null) throw SetTargetThrows;
        return FakeSetTargetResult;
    }

    public ProjectTargetInfo FakeProjectTarget { get; set; } = ProjectTargetInfo.Unknown;
    public Exception? ReadProjectTargetThrows { get; set; }

    public ProjectTargetInfo ReadProjectTarget()
    {
        CallLog.Add("ReadProjectTarget()");
        if (ReadProjectTargetThrows is not null) throw ReadProjectTargetThrows;
        return FakeProjectTarget;
    }

    public string RestartRuntime()
    {
        CallLog.Add("RestartRuntime()");
        if (RestartRuntimeThrows is not null) throw RestartRuntimeThrows;
        return FakeRestartRuntimeResult;
    }

    public string ScopeProbe(string action, string? nodePath, string? name, int? elementType,
        string? key, string? value, string? projectName)
    {
        CallLog.Add($"ScopeProbe(action={action}, node={nodePath ?? "<null>"})");
        return "(fake scope probe)";
    }
}
