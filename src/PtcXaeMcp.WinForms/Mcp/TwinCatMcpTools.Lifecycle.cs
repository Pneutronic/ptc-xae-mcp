using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_open_project", ReadOnly = false, Destructive = true)]
    [Description("Open a TwinCAT solution in the running XAE instance, launching XAE if needed. Takes a .sln or directly a project file (.tsproj / .tspproj / .hmiproj; a temporary .sln is generated for it). Optionally closes the currently-open solution first. " +
                 "Before the XAE loads anything, every independent project file a .tsproj references (File=\"….xti\": devices, boxes, axes, PLC projects) must exist: " +
                 "a missing one (e.g. after IO nodes were renamed outside the XAE) refuses the open with [Error] listing them, because the XAE would load a broken configuration and saving it damages the .tsproj. " +
                 "allowMissingReferences=true opens anyway with [Warn]; save_project and close_project(saveFirst=true) then repeat the warning.")]
    public async Task<string> OpenProject(
        [Description("Absolute path to the .sln, .tsproj, .tspproj or .hmiproj file to open")] string slnPath,
        [Description("Close the currently-open solution first (no save). Default false.")] bool closeCurrentFirst = false,
        [Description("Open even when referenced .xti files are missing (the result starts with [Warn]). Default false refuses.")] bool allowMissingReferences = false,
        CancellationToken cancellationToken = default)
        => await bridge.Activity.TraceAsync("twincat_open_project",
            new { slnPath, closeCurrentFirst, allowMissingReferences }, async () =>
        {
            try
            {
                return await bridge.Dispatcher.RunAsync(
                    () => new LifecycleCoordinator(bridge.Dispatcher.Session)
                        .OpenProjectAsync(slnPath, closeCurrentFirst, cancellationToken, allowMissingReferences),
                    afterSuccess: () => host.RequestEnvironmentRefresh("After OpenProject"));
            }
            catch (OperationCanceledException)
            {
                return McpErrors.Cancelled("open project cancelled");
            }
            catch (Exception ex)
            {
                return $"[Error] {ex.Message}";
            }
        }, cancellationToken);

    [McpServerTool(Name = "twincat_save_project", ReadOnly = false, Destructive = false)]
    [Description("Save all dirty documents and the solution in the running TwinCAT XAE instance (equivalent to File > Save All).")]
    public string SaveProject(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_save_project", null, () =>
            bridge.Dispatcher.Run(() => new LifecycleCoordinator(bridge.Dispatcher.Session).SaveProject()), cancellationToken);

    [McpServerTool(Name = "twincat_shutdown", ReadOnly = false, Destructive = true)]
    [Description("Shut down the Pneutronic XAE MCP server and exit the host application. Returns immediately; the process terminates ~200 ms later so this response can flush. " +
        "Side effects: closing the host form kills a running Usermode Runtime (UMR) instance and panic-aborts an armed IbnLive session (writes the abort bit and drops bEnable). " +
        "Refuses while a UMR instance is running unless force=true is passed, since that stops the runtime out from under whatever is using it.")]
    public string Shutdown(
        [Description("Shut down even while a UMR instance is running (see side effects above). Default false refuses in that case.")] bool force = false,
        CancellationToken cancellationToken = default) =>
        // Reentrancy: shutdown must return immediately and never block on the
        // gate or a log flush in the critical path (spec). Recording is
        // synchronous and best-effort, so it stays off the shutdown timer.
        bridge.Activity.Trace("twincat_shutdown", new { force }, () =>
        {
            // issue #154, A-8: shutting down the host closes the form, which
            // kills a running UMR instance (ServerHost.Dispose → UmrManager.Dispose())
            // and panic-aborts an armed IbnLive session — previously silent,
            // undocumented side effects. Refuse by default while a UMR is
            // running; force=true accepts the consequences explicitly.
            if (!force && host.UmrManager.IsRunning)
            {
                return "[Error] Refusing to shut down: a UMR instance is running " +
                    $"(state: {host.UmrManager.State}) and shutdown would kill it. " +
                    "Stop it first (twincat_umr_stop or the GUI), or pass force=true to shut down anyway.";
            }

            bridge.RequestShutdown(TimeSpan.FromMilliseconds(200));
            return $"Shutting down {ProductInfo.DisplayName} host...";
        }, cancellationToken, bypassGate: true);

    [McpServerTool(Name = "twincat_close_project", ReadOnly = false, Destructive = true)]
    [Description("Close the currently open solution in the TwinCAT XAE instance. By default saves first; set saveFirst=false to discard unsaved changes.")]
    public string CloseProject(
        [Description("Save dirty documents before closing. Default true.")] bool saveFirst = true,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_close_project", new { saveFirst },
            () => bridge.Dispatcher.Run(
                () => new LifecycleCoordinator(bridge.Dispatcher.Session).CloseProject(saveFirst),
                afterSuccess: () => host.RequestEnvironmentRefresh("After CloseProject")), cancellationToken);
}
