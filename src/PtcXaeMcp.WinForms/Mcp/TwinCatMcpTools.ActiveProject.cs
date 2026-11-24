using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_get_active_project", ReadOnly = true)]
    [Description("Get the open TwinCAT solution and its target platform. " +
                 "Returns { solutionOpen, solutionPath, solutionName, targetPlatform, targetNetId, platformWarning }; " +
                 "targetPlatform is the active solution platform (e.g. 'TwinCAT OS (x64)'); platformWarning is set when it does not fit " +
                 "the target (e.g. ARM for a User Mode Runtime). Check it before changing anything.")]
    public string GetActiveProject(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_active_project", null, () =>
            bridge.Dispatcher.Run(() =>
                new ActiveProjectCoordinator(bridge.Dispatcher.Session).GetActiveProject()), cancellationToken);
}
