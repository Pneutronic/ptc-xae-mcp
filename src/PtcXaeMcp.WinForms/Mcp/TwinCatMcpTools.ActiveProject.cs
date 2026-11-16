using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_get_active_project", ReadOnly = true)]
    [Description("Get the open TwinCAT solution: { solutionOpen, solutionPath, solutionName }. " +
                 "Check it before changing anything.")]
    public string GetActiveProject(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_active_project", null, () =>
            bridge.Dispatcher.Run(() =>
                new ActiveProjectCoordinator(bridge.Dispatcher.Session).GetActiveProject()), cancellationToken);
}
