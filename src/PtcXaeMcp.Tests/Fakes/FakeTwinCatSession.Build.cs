using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- Build / activate surface -------------------------------------------

    public BuildResult FakeBuildPlcResult { get; set; } =
        new BuildResult(true, 0, Array.Empty<BuildError>());
    public BuildResult FakeBuildSolutionResult { get; set; } =
        new BuildResult(true, 0, Array.Empty<BuildError>());
    public ActivateResult FakeActivateResult { get; set; } =
        new ActivateResult(true, "Activated successfully.");
    public Exception? BuildThrows { get; set; }
    public Exception? ActivateThrows { get; set; }

    public BuildResult BuildPlc(string? plcProjectName)
    {
        CallLog.Add($"BuildPlc({plcProjectName ?? "<null>"})");
        if (BuildThrows is not null) throw BuildThrows;
        return FakeBuildPlcResult;
    }

    /// <summary>Runs inside <see cref="BuildSolution"/> before it returns (e.g. to block like a modal would).</summary>
    public Action? BuildSolutionHook { get; set; }

    public BuildResult BuildSolution()
    {
        CallLog.Add("BuildSolution()");
        BuildSolutionHook?.Invoke();
        if (BuildThrows is not null) throw BuildThrows;
        return FakeBuildSolutionResult;
    }

    public ActivateResult ActivateConfig(
        bool autoConfirmRestart = false,
        bool autoGenerateLicenses = false,
        bool autoConfirmLogin = false,
        LoginMode loginMode = LoginMode.OnlineChange,
        bool updateBootProject = true,
        bool allowDownloadFallback = false,
        TimeSpan? runTimeout = null)
    {
        CallLog.Add($"ActivateConfig(restart={autoConfirmRestart}, licenses={autoGenerateLicenses}, " +
                    $"login={autoConfirmLogin}, mode={loginMode}, updateBoot={updateBootProject}, " +
                    $"downloadFallback={allowDownloadFallback}, " +
                    $"runTimeout={(runTimeout is null ? "default" : runTimeout.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))})");
        if (ActivateThrows is not null) throw ActivateThrows;
        return FakeActivateResult;
    }

    // ---- xae-error-list ----
    public ErrorListClearResult FakeClearErrorListResult { get; set; } =
        new ErrorListClearResult(true, 3, "Error list cleared.");
    public Exception? ClearErrorListThrows { get; set; }

    public ErrorListClearResult ClearErrorList()
    {
        CallLog.Add("ClearErrorList()");
        if (ClearErrorListThrows is not null) throw ClearErrorListThrows;
        return FakeClearErrorListResult;
    }

    // ---- ethercat-device-descriptions ----
    public DeviceDescriptionReloadResult FakeReloadDeviceDescriptionsResult { get; set; } =
        new DeviceDescriptionReloadResult(true, "Device descriptions reloaded.", TimeSpan.FromSeconds(1.5));
    public Exception? ReloadDeviceDescriptionsThrows { get; set; }

    public DeviceDescriptionReloadResult ReloadDeviceDescriptions()
    {
        CallLog.Add("ReloadDeviceDescriptions()");
        if (ReloadDeviceDescriptionsThrows is not null) throw ReloadDeviceDescriptionsThrows;
        return FakeReloadDeviceDescriptionsResult;
    }

    public List<BuildError> FakeErrorList { get; } = new();
    public Exception? GetErrorListThrows { get; set; }

    public IReadOnlyList<BuildError> GetErrorList()
    {
        CallLog.Add("GetErrorList()");
        if (GetErrorListThrows is not null) throw GetErrorListThrows;
        return FakeErrorList;
    }

    // ---- build-recovery ----
    public LogoutPlcResult FakeLogoutPlcResult { get; set; } =
        new LogoutPlcResult(false, false, "Was already offline (fake).");
    public Exception? LogoutPlcThrows { get; set; }
    /// <summary>When non-empty, LogoutPlc answers from this queue first (issue #190: state per call).</summary>
    public Queue<LogoutPlcResult> LogoutPlcResults { get; } = new();
    public CleanResult FakeCleanResult { get; set; } =
        new CleanResult(true, "Solution cleaned (fake).");
    public Exception? CleanThrows { get; set; }
    public RebuildResult FakeRebuildResult { get; set; } =
        new RebuildResult(true, 0, Array.Empty<BuildError>());
    public Exception? RebuildThrows { get; set; }

    public LogoutPlcResult LogoutPlc()
    {
        CallLog.Add("LogoutPlc()");
        if (LogoutPlcThrows is not null) throw LogoutPlcThrows;
        return LogoutPlcResults.Count > 0 ? LogoutPlcResults.Dequeue() : FakeLogoutPlcResult;
    }

    public CleanResult CleanSolution()
    {
        CallLog.Add("CleanSolution()");
        if (CleanThrows is not null) throw CleanThrows;
        return FakeCleanResult;
    }

    public RebuildResult RebuildSolution()
    {
        CallLog.Add("RebuildSolution()");
        if (RebuildThrows is not null) throw RebuildThrows;
        return FakeRebuildResult;
    }

    public RebuildResult RebuildPlc(string? plcProjectName)
    {
        CallLog.Add($"RebuildPlc({plcProjectName ?? "<null>"})");
        if (RebuildThrows is not null) throw RebuildThrows;
        return FakeRebuildResult;
    }
}
