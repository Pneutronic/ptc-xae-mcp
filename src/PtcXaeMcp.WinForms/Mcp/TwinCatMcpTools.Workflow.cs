using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

// workflow tools: the two chains that agents otherwise assemble from four to six single tools. The steps call the
// coordinators of the single tools directly, so a workflow asks the human once (the call-tool filter, for the workflow
// itself) and carries one request key; no step asks again.
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_deploy", ReadOnly = false, Destructive = true)]
    [Description("Build, activate and check in one call. Fixed order: build (PLC project, or the whole solution with " +
                 "buildSolution), expectSymbols, target check, twincat_activate_config with a restart of TwinCAT into the new configuration, an " +
                 "ADS check that the target's system reads Run, the PLC runtime reads Run (put into Run over ADS when it does not and " +
                 "startPlc is true), then optionally lifeSign conditions (as twincat_ads_wait_until). The first step that fails ends " +
                 "the run and nothing after it starts: build errors are never activated, and an activation that cannot be shown to " +
                 "have taken effect is never followed by a PLC start. A human is asked once for the whole run, not per step; " +
                 "requestKey makes a retry after a timeout safe. Returns JSON { success, amsNetId, steps: [{ step, ok, durationMs, " +
                 "message }], warnings }; a failure answers '[Error] twincat_deploy stopped at step …' followed by the same JSON with " +
                 "notRun. With restart=false only the registry is written (a warning says the PLC keeps its old code). The target is " +
                 "the open solution's. Needs an open solution.")]
    public string Deploy(
        [Description("PLC project name (e.g. 'PLC') for the PLC build. Empty = first .plcproj. Not together with buildSolution.")] string? plcProjectName = null,
        [Description("Build the whole solution (PLC code and IO mapping) instead of the PLC project only. Default false.")] bool buildSolution = false,
        [Description("Build despite PLC files changed on disk outside the XAE (as in twincat_build_plc). Default false refuses.")] bool ignoreStaleFiles = false,
        [Description("Names that must exist in the compiled PLC (as in twincat_build_plc), e.g. ['MAIN.fbAxis']. A missing name ends the run before the activation.")] string[]? expectSymbols = null,
        [Description("Restart TwinCAT into the new configuration (forcibly restarts the PLC; the activation's dialog watcher confirms the restart prompt). Default true. False: registry only, no deploy.")] bool restart = true,
        [Description("Answer the trial-license prompt with 'Ja' (as in twincat_activate_config). Default false.")] bool autoGenerateLicenses = false,
        [Description("Confirm the 'application changed since the last download' login modal (as in twincat_activate_config). Default false.")] bool autoConfirmLogin = false,
        [Description("Answer for that modal: 'online_change' (default) | 'download' | 'login_without_change'.")] string loginMode = "online_change",
        [Description("On download, tick 'Bootapplikation aktualisieren'. Default true.")] bool updateBootProject = true,
        [Description("loginMode='online_change' only: download instead if online change is impossible (stops the PLC). Default false.")] bool allowDownloadFallback = false,
        [Description("Seconds to wait for the system to read Run after the restart. Default 60, clamped to 5..300.")] int? runTimeoutSeconds = null,
        [Description("Put the PLC runtime into Run over ADS when it does not read Run after the activation. Default true; false makes that a failed step.")] bool startPlc = true,
        [Description("ADS port of the PLC runtime for the state check and the life sign. Default 851.")] int adsPort = 851,
        [Description("Conditions that prove the PLC program runs, as twincat_ads_wait_until takes them, e.g. ['MAIN.nCycle changed']. Up to 8. Omit to skip.")] string[]? lifeSign = null,
        [Description("How long the lifeSign conditions may take, in ms. Default 10000, at most 300000.")] int lifeSignTimeoutMs = 10000,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_deploy",
            new { plcProjectName, buildSolution, ignoreStaleFiles, expectSymbols, restart, autoGenerateLicenses, autoConfirmLogin, loginMode, updateBootProject,
                  allowDownloadFallback, runTimeoutSeconds, startPlc, adsPort, lifeSign, lifeSignTimeoutMs }, () =>
        {
            LoginMode mode = loginMode switch
            {
                "online_change"        => LoginMode.OnlineChange,
                "download"             => LoginMode.Download,
                "login_without_change" => LoginMode.LoginWithoutChange,
                _ => (LoginMode)(-1),
            };
            if (!Enum.IsDefined(mode))
                return $"[Error] Unknown loginMode '{loginMode}'. Valid values: online_change, download, login_without_change. Nothing was done.";

            var request = new DeployRequest
            {
                PlcProjectName = plcProjectName,
                BuildSolution = buildSolution,
                IgnoreStaleFiles = ignoreStaleFiles,
                ExpectSymbols = expectSymbols,
                Restart = restart,
                AutoGenerateLicenses = autoGenerateLicenses,
                AutoConfirmLogin = autoConfirmLogin,
                LoginMode = mode,
                UpdateBootProject = updateBootProject,
                AllowDownloadFallback = allowDownloadFallback,
                RunTimeoutSeconds = runTimeoutSeconds,
                StartPlc = startPlc,
                PlcPort = adsPort,
                LifeSign = lifeSign,
                LifeSignTimeoutMs = lifeSignTimeoutMs,
            };
            var ops = new DeployOps(bridge.Dispatcher.Session, run => bridge.Dispatcher.Run(run));
            return new DeployCoordinator(ops, locked =>
            {
                DeployCoordinator.DeployRun? result = null;
                bridge.Dispatcher.Run(() => { result = locked(); return "OK"; });
                return result!;
            }).Deploy(request, cancellationToken);
        }, cancellationToken);

    [McpServerTool(Name = "twincat_release_check", ReadOnly = true)]
    [Description("Read-only release checklist: one traffic light per technical finding. " +
                 "Items: build state (Error List against the newest PLC source), Check all objects, static analysis (last report), " +
                 "libraries installed and pinned, required licenses against the target, boot project (files), PLC runtime state, " +
                 "open XAE dialogs. It never builds, saves or " +
                 "starts anything. What cannot be read without doing so is 'unknown' (never green) and names the tool that would " +
                 "show it: Check all objects and the static analysis compile and save, so only a report left by an earlier run is " +
                 "read. Returns JSON { success, overall, green, yellow, red, unknown, solution, items: [{ id, title, status, " +
                 "summary, details }] }; overall is the worst item (red, then unknown, then yellow). It lists findings, it does not " +
                 "judge the hand-over. Needs an open solution; with an XAE dialog blocking, the reads that need the XAE are unknown.")]
    public string ReleaseCheck(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_release_check", null, () =>
            new ReleaseCheckCoordinator(bridge.Dispatcher.Session, locked =>
            {
                ReleaseCheckCoordinator.ComFacts? facts = null;
                bridge.Dispatcher.Run(() => { facts = locked(); return "OK"; });
                return facts!;
            }).Check(), cancellationToken);
}
