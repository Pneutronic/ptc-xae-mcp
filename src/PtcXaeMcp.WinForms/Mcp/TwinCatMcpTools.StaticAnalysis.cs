using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_plc_static_analysis", ReadOnly = false, Destructive = false)]
    [Description("Run the PLC Static Analysis (TE1200: SA rules, naming conventions, metrics) on the open PLC project and return the findings " +
                 "compactly — level, rule, file, line, message — so a generate-check-correct loop needs one call. checkAll=true (default) also " +
                 "checks objects no task reaches. The results are read from the SARIF export (TwinCAT >= 3.1.4026.20, written to " +
                 "%LOCALAPPDATA%\\ptc-xae-mcp\\static-analysis, outside the project); an XAE that cannot export SARIF is answered from the Error " +
                 "List's SA entries and the result says why. settingsFile loads a .csa rule set first. Without a TE1200 license TwinCAT runs only " +
                 "its free subset of rules. The analysis compiles like a build; it saves nothing. Counts and the most frequent rules always cover " +
                 "every finding; the listed findings are filtered by minLevel (default warning) and paged with limit/offset " +
                 "(the last line gives total, has_more and next_offset).")]
    public string PlcStaticAnalysis(
        [Description("True (default): check all objects, also code no task calls. False: only the objects in use.")] bool checkAll = true,
        [Description("Optional .csa static-analysis settings file to load before the run.")] string? settingsFile = null,
        [Description("Lowest level listed: error, warning (default) or note.")] string minLevel = "warning",
        [Description("Findings per page: default 100, at most 1000 (0 = 1000).")] int? limit = null,
        [Description("Findings to skip, for the next page (next_offset of the previous call). Default 0.")] int offset = 0,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_static_analysis", new { checkAll, settingsFile, minLevel, limit, offset }, () =>
            Mcp.McpPaging.Validate(limit, offset) ?? bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckOptionalFile(settingsFile, bridge.Dispatcher.Session, "settingsFile")
                ?? new StaticAnalysisCoordinator(bridge.Dispatcher.Session).Run(checkAll, settingsFile, minLevel, limit, offset)), cancellationToken);
}
