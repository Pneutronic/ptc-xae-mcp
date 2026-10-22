using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.WinForms;

/// <summary>
/// Owns the TwinCAT session and everything the MCP tools and the GUI share (issue #268):
/// the single-operation <see cref="Dispatcher"/> lock, the IBN Live coordinator, the UMR
/// process manager, the HMI write confirmer, the run log and the last environment status.
/// <para>
/// Created by <c>Program</c> before the window. The MCP tools depend on this class only —
/// never on <c>Form1</c> — and the GUI subscribes to its events. Approval UI is plugged in
/// with <see cref="AttachHmiConfirmUi"/>; without it every HMI write fails closed.
/// </para>
/// </summary>
internal sealed class ServerHost : IDisposable
{
    private readonly SwitchableHmiConfirmer _hmiUi = new();
    private HostEnvironmentSnapshot _environment = HostEnvironmentSnapshot.NotChecked;
    private TaskCompletionSource? _pendingRefresh;
    private bool _disposed;

    /// <summary>How long <see cref="GetStatus"/> waits for a requested environment refresh.</summary>
    internal static readonly TimeSpan RefreshWait = TimeSpan.FromSeconds(10);

    public ServerHost() : this(new TwinCatComSession(), enableTimers: true) { }

    /// <param name="enableTimers">IBN Live heartbeat/watchdog timers; tests pass false.</param>
    internal ServerHost(ITwinCatSession session, bool enableTimers)
    {
        Session = session;
        Dispatcher = new McpDispatcher(session);
        LiveCoordinator = new IbnLiveCoordinator(session, enableTimers: enableTimers);
        SnapshotRestore = new SnapshotRestoreCoordinator(session);
        // Only a TEMP fixture with PTC_XAE_MCP_HMI_AUTOCONFIRM_TEMP=1 skips the engineer's popup.
        // The outermost decorator records every answer for the Approvals view (#272) and returns
        // it unchanged, so the chain stays fail-closed exactly as before.
        HmiConfirmer = new RecordingHmiConfirmer(
            new HmiTempAutoConfirmer(
                _hmiUi,
                () => Environment.GetEnvironmentVariable(HmiAutoConfirmPolicy.EnvVar),
                Path.GetTempPath,
                Win32LongPath.Resolve),
            HmiApprovals);
    }

    public ITwinCatSession Session { get; }

    /// <summary>The single-operation lock every COM-touching action — GUI or MCP — goes through (issue #154).</summary>
    public McpDispatcher Dispatcher { get; }

    /// <summary>ibn-live: one proposal registry, heartbeat and watchdog for MCP tools and GUI.</summary>
    public IbnLiveCoordinator LiveCoordinator { get; }

    /// <summary>persistent-snapshot: the restore-proposal registry shared by the MCP tools and the Approvals view.
    /// Its <c>ConfirmProposal</c> is host-only (popup OK), never bound to an MCP tool.</summary>
    public SnapshotRestoreCoordinator SnapshotRestore { get; }

    /// <summary>Standalone user-mode runtime process (TcSystemServiceUm.exe).</summary>
    public UmrProcessManager UmrManager { get; } = new();

    /// <summary>hmi-server: asks the engineer before EVERY HMI write (see <see cref="AttachHmiConfirmUi"/>).</summary>
    public IHmiWriteConfirmer HmiConfirmer { get; }

    /// <summary>Every HMI write approval and its outcome (bounded), for the Approvals view (#272).</summary>
    public HmiApprovalHistory HmiApprovals { get; } = new();

    /// <summary>The host's run log (apply-schema progress, environment checks) — <c>twincat_get_log</c>.</summary>
    public HostRunLog RunLog { get; } = new();

    /// <summary>Cancels the MCP-started <c>twincat_apply_schema</c> run on <c>twincat_stop</c> (issue #154, E-1).</summary>
    public McpRunCancellation RunCancellation { get; } = new();

    /// <summary>Last environment status reported by whoever probed (the GUI header today).</summary>
    public HostEnvironmentSnapshot EnvironmentSnapshot => Volatile.Read(ref _environment);

    /// <summary>A tool changed the open project; the GUI re-probes and reports back.</summary>
    public event Action<string>? EnvironmentRefreshRequested;

    /// <summary>An MCP apply-schema run started (true) or ended (false), with a status text.</summary>
    public event Action<bool, string>? ApplyRunStateChanged;

    public void AttachHmiConfirmUi(IHmiWriteConfirmer ui) => _hmiUi.Inner = ui;

    /// <summary>Stores the newest environment readout and completes a pending refresh.</summary>
    public void ReportEnvironment(HostEnvironmentSnapshot snapshot)
    {
        Volatile.Write(ref _environment, snapshot);
        Interlocked.Exchange(ref _pendingRefresh, null)?.TrySetResult();
    }

    /// <summary>
    /// Asks the GUI to re-probe (after open/close project). Until it reports back through
    /// <see cref="ReportEnvironment"/>, <see cref="GetStatus"/> waits for it, bounded by
    /// <see cref="RefreshWait"/> — so <c>twincat_get_status</c> right after
    /// <c>twincat_open_project</c> shows the new project, as it did when it read the labels.
    /// </summary>
    public void RequestEnvironmentRefresh(string reason)
    {
        if (EnvironmentRefreshRequested is not { } handler) return;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingRefresh, pending)?.TrySetResult();
        handler(reason);
    }

    /// <summary><c>twincat_stop</c> / the header's Stop button: cancels the running apply. Only
    /// <c>twincat_apply_schema</c> starts one — the GUI apply was removed with the Schema tab (#270).</summary>
    /// <returns>True when a run was there to cancel.</returns>
    public bool RequestStop()
    {
        bool hadRun = RunCancellation.CancelCurrent();
        if (hadRun)
            RunLog.Append("=== Cancellation requested ===");
        return hadRun;
    }

    /// <summary>
    /// <c>twincat_get_status</c>. Deliberately lock-free and bounded: its one COM call
    /// (<see cref="ITwinCatSession.MultiXaeBlockReason"/>, only with more than one
    /// TcXaeShell) runs with a 5 s cap, so get_status always answers.
    /// </summary>
    public string GetStatus()
    {
        // A refresh requested by open/close project must land first (bounded).
        Volatile.Read(ref _pendingRefresh)?.Task.Wait(RefreshWait);

        // multi-xae-robustness (issue #57): with >1 TcXaeShell, report the
        // deterministic-bind verdict or the precise ambiguity block.
        int? xaeProcs = XaeProcessProbe.Count();
        string xaeProcNote = xaeProcs is null ? " | ⚠ TcXaeShell process count unknown (probe failed)" : string.Empty;
        if (xaeProcs > 1)
        {
            // The ROT walk touches every DTE and hangs on a frozen instance (issue #149 C-5).
            string? block;
            var walk = Task.Run(() => Session.MultiXaeBlockReason());
            if (!walk.Wait(TimeSpan.FromSeconds(5)))
                block = "[Error] ROT walk did not return within 5 s — an XAE instance is hung or blocked by a dialog.";
            else
                block = walk.IsFaulted ? null : walk.Result;
            xaeProcNote = block is null
                ? $" | ⚠ {xaeProcs} TcXaeShell running — bound deterministically " +
                  "to the instance with the expected solution open"
                : " | ⚠ " + block.Replace("[Error] ", string.Empty);
        }
        string system = TwinCatSystemService.StatusText(TwinCatSystemService.Query());
        var env = EnvironmentSnapshot;
        return $"XAE: {env.Xae} | Project: {env.Project} | {system}{xaeProcNote}";
    }

    /// <summary>
    /// <c>twincat_apply_schema</c>: the full IO-schema pipeline. Takes the
    /// <see cref="Dispatcher"/> lock itself (TryAcquire/Release around the COM work).
    /// </summary>
    public async Task<IReadOnlyList<string>> RunApplySchemaAsync(
        string schemaPath,
        string? plcOverride,
        string? masterOverride,
        CancellationToken mcpToken,
        string? targetMasterPath = null,
        bool failClosed = false,
        bool autoBuild = true,
        bool overwriteExistingGvl = false)
    {
        if (!Dispatcher.TryAcquire())
            throw McpDispatcher.Busy();

        var collected = new List<string>();
        try
        {
            // The PLC/master come from an explicit tool parameter, else the schema's value
            // (issue #155).
            var options = new RunnerOptions
            {
                AutoConfirmBuild = true,
                PlcProjectNameOverride = plcOverride ?? string.Empty,
                MasterNameOverride = masterOverride ?? string.Empty,
                OverwriteExistingGvl = overwriteExistingGvl,
                // brownfield-guard: fail-safe knobs of the MCP tool.
                TargetMasterPath = targetMasterPath ?? string.Empty,
                FailClosed = failClosed,
                AutoBuild = autoBuild,
            };

            Action<string> log = line =>
            {
                RunLog.Append(line);
                lock (collected) collected.Add(line);
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(mcpToken);
            // twincat_stop is a separate MCP call with its own token; RequestStop cancels this one.
            using var mcpRunTrack = RunCancellation.Track(cts);

            ApplyRunStateChanged?.Invoke(true, "MCP: Running...");
            RunLog.Append("=== MCP Start ===");
            string endStatus = "MCP: Error.";
            try
            {
                await NativeTwinCatRunner.RunAsync(Session, schemaPath, options, log, cts.Token);
                RunLog.Append("=== MCP End ===");
                endStatus = "MCP: Finished.";
            }
            catch (OperationCanceledException)
            {
                RunLog.Append("=== MCP Cancelled ===");
                endStatus = "MCP: Cancelled.";
                throw;
            }
            catch (Exception ex)
            {
                RunLog.Append("[fatal] MCP: " + ex.Message);
                throw;
            }
            finally
            {
                ApplyRunStateChanged?.Invoke(false, endStatus);
            }
        }
        finally
        {
            Dispatcher.Release();
        }

        return collected;
    }

    /// <summary>
    /// Host teardown, after the window has closed and the MCP server was told to stop.
    /// Order: cancel the running apply; let in-flight tool calls drain (the dispatcher waits
    /// up to 3 s) so no COM call races the teardown; IBN Live homes the machine — that ADS
    /// write has no timeout of its own, so it runs capped at 3 s (issue #154, E-28); then the
    /// UMR process and the session.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RequestStop();
        Dispatcher.Dispose();
        try
        {
            Task.Run(() => { try { LiveCoordinator.Abort(); } catch { } })
                .Wait(TimeSpan.FromSeconds(3));
        }
        catch { }
        LiveCoordinator.Dispose();
        UmrManager.Dispose();
        Session.Dispose();
        Interlocked.Exchange(ref _pendingRefresh, null)?.TrySetResult();
    }

    /// <summary>Forwards to the GUI's confirmer once attached; fails closed before that.</summary>
    private sealed class SwitchableHmiConfirmer : IHmiWriteConfirmer
    {
        private IHmiWriteConfirmer? _inner;

        public IHmiWriteConfirmer? Inner
        {
            get => Volatile.Read(ref _inner);
            set => Volatile.Write(ref _inner, value);
        }

        public HmiConfirmOutcome Confirm(HmiWriteRequest request, TimeSpan timeout) =>
            Inner?.Confirm(request, timeout) ?? HmiConfirmOutcome.Unavailable;
    }
}

/// <summary>Last known environment readout, shown by <c>twincat_get_status</c>.</summary>
internal sealed record HostEnvironmentSnapshot(string Xae, string Project)
{
    public static readonly HostEnvironmentSnapshot NotChecked = new("Not checked yet", "Not checked yet");
}
