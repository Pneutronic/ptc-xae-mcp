using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_plc_static_analysis", ReadOnly = false, Destructive = false)]
    [Description("Run PLC Static Analysis (TE1200) on the open PLC project and return findings. Covers rules, naming " +
                 "conventions and metrics; each finding: level, rule, file, line, message. Read from the SARIF export (TwinCAT >= " +
                 "3.1.4026.20, written to %LOCALAPPDATA%\\ptc-xae-mcp\\static-analysis, outside the project), else from the Error List's " +
                 "SA entries (the result says why). Without a TE1200 license only the free rule subset runs. Compiles like a build, saves " +
                 "nothing. Counts and top rules cover every finding; listed findings are filtered by minLevel and paged with limit/offset " +
                 "(last line: total, has_more, next_offset).")]
    public string PlcStaticAnalysis(
        [Description("True (default): also check objects no task calls. False: only objects in use.")] bool checkAll = true,
        [Description("Optional .csa rule-set file loaded before the run.")] string? settingsFile = null,
        [Description("Lowest level listed: error, warning (default) or note.")] string minLevel = "warning",
        [Description("Findings per page: default 100, max 1000 (0 = 1000).")] int? limit = null,
        [Description("Findings to skip (next_offset of the previous page). Default 0.")] int offset = 0,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_static_analysis", new { checkAll, settingsFile, minLevel, limit, offset }, () =>
            Mcp.McpPaging.Validate(limit, offset) ?? bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckOptionalFile(settingsFile, bridge.Dispatcher.Session, "settingsFile")
                ?? new StaticAnalysisCoordinator(bridge.Dispatcher.Session).Run(checkAll, settingsFile, minLevel, limit, offset)), cancellationToken);
}
