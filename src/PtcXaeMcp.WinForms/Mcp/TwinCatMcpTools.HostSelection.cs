using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_resolve_host", ReadOnly = true)]
    [Description(
        "Resolve which XAE host (32- or 64-bit) a project's TcVersion selects. " +
        "3.1.4026 and later select the 64-bit XAE, older ones the 32-bit. Reports TcVersion, host class, ProgID, exe " +
        "path, whether it is installed and running. Opens and changes nothing.")]
    public string ResolveHost(
        [Description("Absolute path to a .tsproj or .sln. Default: the open solution.")]
        string? tsprojOrSlnPath = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_resolve_host", new { tsprojOrSlnPath }, () =>
        {
            // ActiveSolutionPath() touches the live COM session, so it must go
            // through the dispatcher lock like every other tool that reads
            // session state — never call bridge.Dispatcher.Session directly.
            var path = string.IsNullOrWhiteSpace(tsprojOrSlnPath)
                ? bridge.Dispatcher.Run(() => bridge.Dispatcher.Session.ActiveSolutionPath() ?? string.Empty)
                : tsprojOrSlnPath;

            if (string.IsNullOrWhiteSpace(path))
                return "[Error] No .tsproj/.sln path supplied and no solution is open.";

            var tsproj = TwinCat.HostSelection.TsprojLocator.FindForSolution(path);
            if (tsproj is null)
                return $"[Error] No .tsproj could be located for: {path}";

            var resolver = new TwinCat.HostSelection.XaeHostResolver();
            if (!resolver.TryResolveFromTsprojFile(tsproj, out var host, out var err))
                return $"[Error] {err}";

            bool exeInstalled = File.Exists(host!.ExePath);
            bool running = ComInterop.IsProgIdRunning(host.ProgId);

            return
                $"TcVersion: {host.TcVersion}\n" +
                $"Host class: {host.HostClass}\n" +
                $"ProgID: {host.ProgId}\n" +
                $"Exe: {host.ExePath} ({(exeInstalled ? "installed" : "MISSING")}, " +
                $"{(host.ExeFromRegistry ? "from registry" : "well-known fallback")})\n" +
                $"Host running: {(running ? "yes" : "no")}\n" +
                $"Resolved from: {tsproj}";
        }, cancellationToken);
}
