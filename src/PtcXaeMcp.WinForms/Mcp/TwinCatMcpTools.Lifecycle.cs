using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    // twincat_open_project has a parameter named host (the XAE bitness), which hides the constructor's host here.
    private ServerHost ServerHost => host;

    [McpServerTool(Name = "twincat_open_project", ReadOnly = false, Destructive = true)]
    [Description("Open a TwinCAT solution or project in the XAE, launching the XAE if needed. " +
                 "A project file (.tsproj / .tspproj / .hmiproj) gets a temporary .sln; a bare .plcproj (e.g. a library " +
                 "repository) also a temporary stand-alone PLC project around it, its files stay and are saved in place. " +
                 "File paths of other tools may then lie in the project's directory and git repository. If a .tsproj references .xti files " +
                 "(devices, boxes, axes, PLC projects) that are missing, the open is refused with [Error] listing them, since " +
                 "loading and saving would damage the .tsproj; allowMissingReferences opens anyway with [Warn], and " +
                 "twincat_save_project / twincat_close_project(saveFirst=true) repeat it. The result names the XAE host the " +
                 "TcVersion selects (32-bit below 4026, else 64-bit) and warns when it picks 32-bit although the 64-bit XAE " +
                 "is installed, or when the running XAE is not the selected one. It also names the active solution platform and " +
                 "the target, with [Warn] when they do not fit (e.g. ARM for a User Mode Runtime).")]
    public async Task<string> OpenProject(
        [Description("Absolute path to the .sln, .tsproj, .tspproj, .hmiproj or .plcproj.")] string slnPath,
        [Description("Close the open solution first, without saving. Default false.")] bool closeCurrentFirst = false,
        [Description("Open despite missing .xti files (result starts with [Warn]). Default false refuses.")] bool allowMissingReferences = false,
        [Description("XAE host: 'auto' (default, selected by TcVersion), '64' or '32'. '64' opens an older project in the 64-bit XAE; the .tsproj TcVersion is never rewritten. Refused if that XAE is not installed or one of the other bitness is running.")] string host = "auto",
        CancellationToken cancellationToken = default)
        => await bridge.Activity.TraceAsync("twincat_open_project",
            new { slnPath, closeCurrentFirst, allowMissingReferences, host }, async () =>
        {
            try
            {
                return await bridge.Dispatcher.RunAsync(
                    () => new LifecycleCoordinator(bridge.Dispatcher.Session)
                        .OpenProjectAsync(slnPath, closeCurrentFirst, cancellationToken, allowMissingReferences, host),
                    afterSuccess: () => ServerHost.RequestEnvironmentRefresh("After OpenProject"));
            }
            catch (OperationCanceledException)
            {
                return McpErrors.Cancelled("open project cancelled");
            }
            catch (XaeCallDetachedException)
            {
                throw; // the [Blocked] answer, returned unchanged by the trace
            }
            catch (Exception ex)
            {
                return $"[Error] {ex.Message}" + UnexpectedXaeHint(ex);
            }
        }, cancellationToken);

    /// <summary>
    /// E_UNEXPECTED (0x8000FFFF) from an open: seen live after a close that hung — the XAE stayed in a broken state and
    /// every open failed until the XAE was ended. The hint names the way out. Pure — unit-tested.
    /// </summary>
    internal static string UnexpectedXaeHint(Exception ex) =>
        ex.HResult == unchecked((int)0x8000FFFF) || ex.InnerException?.HResult == unchecked((int)0x8000FFFF)
            ? $"\n{McpErrors.NextPrefix}if this repeats for every solution, the XAE is in a broken state (e.g. after a " +
              "close that hung): twincat_force_close_xae ends it (unsaved work is lost), then twincat_open_project again."
            : "";

    /// <summary>
    /// How long twincat_close_project may run without returning and without an XAE dialog before it answers [Error]
    /// (seen live: a close that never returned held the operation lock for good). Below the client's usual 300 s.
    /// </summary>
    internal static readonly TimeSpan CloseHangLimit = TimeSpan.FromSeconds(180);

    [McpServerTool(Name = "twincat_save_project", ReadOnly = false, Destructive = false)]
    [Description("Save all modified documents and the solution (File > Save All).")]
    public string SaveProject(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_save_project", null, () =>
            bridge.Dispatcher.Run(() => new LifecycleCoordinator(bridge.Dispatcher.Session).SaveProject()), cancellationToken);

    [McpServerTool(Name = "twincat_shutdown", ReadOnly = false, Destructive = true)]
    [Description("Shut down this MCP server and exit its host application. " +
        "Returns at once; the process exits ~200 ms later. Exiting kills a running Usermode Runtime (UMR) and aborts an " +
        "armed IbnLive session (writes the abort bit, drops bEnable). Refuses while a UMR is running unless force=true.")]
    public string Shutdown(
        [Description("Shut down even while a UMR instance is running. Default false.")] bool force = false,
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
    [Description("Close the open solution in the XAE; saveFirst=false discards unsaved changes. A close that does not " +
                 "return within 180 s while no XAE dialog is open answers [Error] (the XAE looks hung) and names " +
                 "twincat_force_close_xae as the way out.")]
    public string CloseProject(
        [Description("Save modified documents first. Default true.")] bool saveFirst = true,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_close_project", new { saveFirst },
            () => bridge.Dispatcher.Run(
                () => new LifecycleCoordinator(bridge.Dispatcher.Session).CloseProject(saveFirst),
                afterSuccess: () => host.RequestEnvironmentRefresh("After CloseProject"),
                hangLimit: CloseHangLimit), cancellationToken);
}
