using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.TwinCat;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    // Like twincat_get_status it does not take the dispatcher lock: a diagnosis must answer while another call hangs.
    [McpServerTool(Name = "twincat_doctor", ReadOnly = true)]
    [Description("Diagnose all tool prerequisites in one call: ok / warn / fail plus next step. " +
                 "Covers server version, TwinCAT System Service, local AMS NetId, TcXaeShell instances, a dialog blocking the " +
                 "XAE, the open solution and the roots file-path parameters may use. Never starts an XAE or runtime; an XAE " +
                 "silent for 5 s is reported, not awaited. Use it first when tools fail for unclear reasons.")]
    public string Doctor(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken = default) =>
        bridge.Activity.Trace("twincat_doctor", null, () =>
            DoctorCoordinator.Format(new DoctorCoordinator(bridge.Dispatcher.Session, DoctorProbes.Live).Run()) + "\n" +
            HumanConfirmation.StatusLine(bridge.Policy.Current, context.Server?.ClientCapabilities), cancellationToken);
}
