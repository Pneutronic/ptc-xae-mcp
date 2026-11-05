using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.TwinCat;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    // Like twincat_get_status it does not take the dispatcher lock: a diagnosis must answer while another call hangs.
    [McpServerTool(Name = "twincat_doctor", ReadOnly = true)]
    [Description("Check everything the tools depend on in one call, each with ok / warn / fail and the next step: MCP server version, " +
                 "TwinCAT System Service, local AMS NetId (router answer or fallback), TcXaeShell instances (none, one, several), a dialog " +
                 "blocking the XAE, the open solution, and the roots file-path parameters may use. Read-only and bounded: it never starts " +
                 "an XAE or a runtime, and an XAE that does not answer within 5 s is reported, not waited for. Use it first when tools fail " +
                 "for unclear reasons.")]
    public string Doctor(CancellationToken cancellationToken = default) =>
        bridge.Activity.Trace("twincat_doctor", null, () =>
            DoctorCoordinator.Format(new DoctorCoordinator(bridge.Dispatcher.Session, DoctorProbes.Live).Run()), cancellationToken);
}
