using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    internal const string BringToFrontHelp =
        "Bring the XAE to the foreground. Only when a person asked to see it (e.g. 'where is that?'); default false — " +
        "the XAE is never pulled in front of the user's work otherwise.";

    [McpServerTool(Name = "twincat_open_object", ReadOnly = true)]
    [Description(
        "Surface ANY Solution-Explorer node in the VISIBLE TwinCAT XAE so a live MCP-driven " +
        "demo/video always shows which object is being worked on. treePath is the same caret-" +
        "separated path as twincat_read_pou (e.g. 'TIPC^PLC^PLC Projekt^POUs^01_Application^MAIN' " +
        "for a PLC object, or 'TIID^Device 1 (EtherCAT)' for an EtherCAT device). Two behaviours, " +
        "because XAE treats the node families differently: a PLC source object (POU/GVL/DUT/" +
        "INTERFACE) opens its OWN editor tab; a system-manager node (EtherCAT/IO, Motion/NC, " +
        "TcCOM) and a Scope node are ACTIVATED in the Solution-Explorer tree (selected + Enter, like a " +
        "double-click) — sysman nodes share ONE sysman editor (no per-node tab), and a Scope view opens " +
        "on activation. Both bring the XAE to the foreground, so a system-manager or Scope node is shown only with " +
        "bringToFront=true (otherwise an [Error], nothing selected); a PLC object opens its tab behind the user's work " +
        "without it. For a Scope node pass the Solution-Explorer names, e.g. " +
        "'<scope project>^<scope node>'. The read tools (read_pou, read_gvl, read_pou_member) auto-open. The headless write " +
        "tools do NOT auto-open — call this explicitly before a write for full video coverage. Returns one line describing " +
        "what happened (opened tab vs. selected node) and, with bringToFront, whether the XAE reached the " +
        "foreground (Windows may refuse — reported, not an error).")]
    public string OpenObject(
        [Description("Caret-separated tree path of the node to surface, e.g. 'TIPC^PLC^PLC Projekt^POUs^01_Application^MAIN' for a PLC object, or 'TIID^Device 1 (EtherCAT)' for an EtherCAT device.")] string treePath,
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
        "Counterpart to twincat_open_object: tidy up a live demo. For a PLC source object, saves " +
        "and closes its editor tab. For a system-manager node there is no per-node tab (one shared " +
        "sysman editor), so this is a reported no-op, never an error. treePath is the same caret-" +
        "separated path as twincat_open_object. Returns one line describing what happened.")]
    public string CloseObject(
        [Description("Caret-separated tree path of the node to close, same as passed to twincat_open_object.")] string treePath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_close_object", new { treePath }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcCodeCoordinator(bridge.Dispatcher.Session).CloseObject(treePath)), cancellationToken);

    // twincat_open_plc_object / twincat_close_plc_object (deprecated back-compat aliases)
    // moved to TwinCatMcpToolsDiagnostics (Mcp/TwinCatMcpTools.Diagnostics.cs) — registered
    // only with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1. Prefer twincat_open_object /
    // twincat_close_object in new callers.
}
