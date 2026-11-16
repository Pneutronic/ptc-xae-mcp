using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

// ibn-live: the safe, AI-decoupled commissioning control surface.
//
// ASYMMETRIC SURFACE — the AI may read, propose and stop, but NO tool here
// arms the control window or triggers a motion. Arming happens only in the host
// window (Approvals view, issue #272). There is deliberately NO confirm/execute
// tool: a proposal is executed only by the host, after a human confirms the modal popup
// (IbnLiveCoordinator.ConfirmProposal, host-only, no MCP binding). This is a
// gate-relevant invariant guarded by IbnLiveMcpTests.
//
// All tools delegate to the singleton bridge.LiveCoordinator so state (proposal
// registry, arm token, heartbeat) lives in the host process, never in the AI
// context.
//
// STOP PATHS ARE NOT DISPATCHED (issue #1): disarm / abort / release_all bypass the
// McpDispatcher lock and the test gate. While a build or an activation holds the
// dispatcher, a dispatched ABORT could only answer "busy". The coordinator's own I/O
// gate serialises them against the other IBN writes instead, and the NetId was
// resolved at arm time, so they never touch COM. Pinned by IbnArmSurfaceTests.
internal sealed partial class TwinCatMcpTools
{
    // twincat_ibn_arm is NOT part of this surface (decision 2026-10-19, issue #272): IBN Live
    // is armed only by a human in the host window (Approvals view). The tool moved to
    // TwinCatMcpToolsDiagnostics (Mcp/TwinCatMcpTools.Diagnostics.cs) as test support for the
    // live suite and is registered only with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

    [McpServerTool(Name = "twincat_ibn_disarm", ReadOnly = false, Destructive = true)]
    [Description("Close the IBN control window and command the test PRG home. " +
                 "Stops the heartbeat; disarm is a watchdog trigger, so the machine returns to its home position. Always " +
                 "allowed. Arming happens only in the host window (Approvals view); no MCP tool arms. " +
                 "Returns JSON { kind, armed:false, abortSymbol, result }.")]
    public string IbnDisarm(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ibn_disarm", new { },
            () => bridge.LiveCoordinator.Disarm(), cancellationToken, bypassGate: true);

    [McpServerTool(Name = "twincat_ibn_propose", ReadOnly = false, Destructive = true)]
    [Description("Propose a step trigger for a human to confirm; moves NOTHING by itself. " +
                 "Creates a PENDING proposal, opens the host's modal confirm popup and returns JSON { proposalId, status: " +
                 "\"pending\" }. Executed ONLY if a human confirms the popup; no AI path executes it. Needs an active arm " +
                 "window, opened only by a human in the host window (Approvals view; no MCP tool arms), else [Error]. " +
                 "Poll twincat_ibn_get_proposal_status for the outcome.")]
    public string IbnPropose(
        [Description("Step action: 'start_chain' | 'next_step' | 'hold'.")] string action,
        [Description("Id of the test chain this trigger targets.")] string? chainId = null,
        [Description("Actuator id; must be on the allowlist the operator armed.")] string? actuatorId = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ibn_propose", new { action, chainId, actuatorId }, () =>
            bridge.Dispatcher.Run(() => bridge.LiveCoordinator.Propose(action, chainId, actuatorId)), cancellationToken);

    [McpServerTool(Name = "twincat_ibn_get_proposal_status", ReadOnly = true)]
    [Description("Poll the status of an IBN proposal; read-only, moves nothing. " +
                 "Returns JSON { proposalId, status, action, chainId, actuatorId, createdUtc, expiresUtc, result }; " +
                 "status: pending | confirmed | executed | rejected | expired.")]
    public string IbnGetProposalStatus(
        [Description("The proposalId returned by twincat_ibn_propose.")] string proposalId,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ibn_get_proposal_status", new { proposalId }, () =>
            bridge.Dispatcher.Run(() => bridge.LiveCoordinator.GetProposalStatus(proposalId)), cancellationToken);

    [McpServerTool(Name = "twincat_list_active_chains", ReadOnly = true)]
    [Description("Read a snapshot of the running IBN test chains; moves nothing. " +
                 "Reads the test PRG status struct at the gvlPath/target armed in the host window unless overridden. " +
                 "Returns JSON { success, gvlPath, chain: { running, activeChain, currentStep, fault }, raw: [{ symbol, " +
                 "value, ok, message }] }.")]
    public string ListActiveChains(
        [Description("Base symbol path of the test PRG status struct. Omit for the armed gvlPath (default 'GVL_IBN').")] string? gvlPath = null,
        [Description("AMS Net ID. Omit for the armed/local target.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_active_chains", new { gvlPath, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() => bridge.LiveCoordinator.ListActiveChains(gvlPath, amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_measure_signal_transition", ReadOnly = true)]
    [Description("Measure the time between two symbol conditions (edge A → edge B) by polling. " +
                 "Read-only, moves nothing; a cross-check for the test PRG's own PLC-timer measurement, which is more " +
                 "deterministic. A condition compares the symbol's rendered value to the *Equals string. " +
                 "Returns JSON { success, elapsedMs, timedOut, message } or an [Error].")]
    public string MeasureSignalTransition(
        [Description("Symbol whose condition starts the measurement, e.g. 'GVL_IO.bCyl1Cmd'.")] string startSymbol,
        [Description("Symbol whose condition ends the measurement, e.g. 'GVL_IO.bCyl1Extended'.")] string endSymbol,
        [Description("Value the start symbol must equal to begin timing. Default 'true'.")] string? startEquals = null,
        [Description("Value the end symbol must equal to stop timing. Default 'true'.")] string? endEquals = null,
        [Description("Overall timeout in ms (default 5000, max 60000).")] int? timeoutMs = null,
        [Description("AMS Net ID. Omit for the local runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_measure_signal_transition",
            new { startSymbol, endSymbol, startEquals, endEquals, timeoutMs, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new TwinCat.AdsVariableCoordinator(bridge.Dispatcher.Session)
                    .MeasureSignalTransition(startSymbol, startEquals, endSymbol, endEquals, timeoutMs, amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_ibn_abort", ReadOnly = false, Destructive = true)]
    [Description("Panic abort: command the test PRG to home/abort IMMEDIATELY and disarm. " +
                 "Not gated by the arm token; always works. Re-arming only in the host window (Approvals view). " +
                 "Returns JSON { kind, armed:false, abortSymbol, result }.")]
    public string IbnAbort(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ibn_abort", new { },
            () => bridge.LiveCoordinator.Abort(), cancellationToken, bypassGate: true);

    [McpServerTool(Name = "twincat_ibn_release_all", ReadOnly = false, Destructive = true)]
    [Description("Panic abort AND reject every pending IBN proposal. " +
                 "Homes the test PRG, rejects all pending proposals and clears the registry. Not gated by the arm token. " +
                 "Re-arming only in the host window (Approvals view). Returns JSON { kind, armed:false, abortSymbol, result }.")]
    public string IbnReleaseAll(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ibn_release_all", new { },
            () => bridge.LiveCoordinator.ReleaseAll(), cancellationToken, bypassGate: true);
}
