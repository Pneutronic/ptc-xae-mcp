using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    internal const string BringToFrontHelp =
        "Bring the XAE to the foreground. Only when a person asked to see it (e.g. 'where is that?'). Default false.";

    [McpServerTool(Name = "twincat_open_object", ReadOnly = true)]
    [Description(
        "VISIBLE: show any Solution-Explorer node in the XAE editor or tree. A PLC source object " +
        "(POU/GVL/DUT/INTERFACE) opens its own editor tab, behind the user's work unless bringToFront=true. A system-manager " +
        "node (EtherCAT/IO, Motion/NC, TcCOM) or a Scope node is activated in the tree like a double-click (sysman nodes share " +
        "one editor); that needs the foreground, so without bringToFront=true it is an [Error] and nothing is selected. " +
        "Write tools do not open the editor; call this before a write to show it. Returns one line (tab opened or node " +
        "selected) and, with bringToFront, whether the XAE reached the foreground (Windows may refuse; not an error).")]
    public string OpenObject(
        [Description("Caret-separated tree path, e.g. 'TIPC^PLC^PLC Projekt^POUs^MAIN' or 'TIID^Device 1 (EtherCAT)'; for Scope the Solution-Explorer names, '<scope project>^<scope node>'.")] string treePath,
        [Description(BringToFrontHelp)] bool bringToFront = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_open_object", new { treePath, bringToFront }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, _) = new PlcCodeCoordinator(bridge.Dispatcher.Session).OpenObject(treePath, bringToFront);
                return summary;
            }), cancellationToken);

    [McpServerTool(Name = "twincat_close_object", ReadOnly = false, Destructive = false)]
    [Description(
        "Save and close a PLC object's editor tab (counterpart of twincat_open_object). A system-manager node has no " +
        "own tab: reported no-op, not an error. Returns one line.")]
    public string CloseObject(
        [Description("Caret-separated tree path, as for twincat_open_object.")] string treePath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_close_object", new { treePath }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcCodeCoordinator(bridge.Dispatcher.Session).CloseObject(treePath)), cancellationToken);

    // twincat_open_plc_object / twincat_close_plc_object (deprecated back-compat aliases)
    // moved to TwinCatMcpToolsDiagnostics (Mcp/TwinCatMcpTools.Diagnostics.cs) — registered
    // only with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1. Prefer twincat_open_object /
    // twincat_close_object in new callers.
}
