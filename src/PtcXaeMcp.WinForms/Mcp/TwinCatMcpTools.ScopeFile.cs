using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    // Scope recordings on disk (.svdx) — file-only, no XAE and no open solution: both tools run the official
    // TC3ScopeExportTool.exe headless and never touch the COM session (allow-listed in McpToolResultContractTests).

    [McpServerTool(Name = "twincat_read_scope_file", ReadOnly = true)]
    [Description("EXPERIMENTAL — read a saved TwinCAT Scope recording (.svdx, e.g. from twincat_save_scope_data) without the XAE. " +
                 "Runs TC3ScopeExportTool.exe (installed with TE130x Scope View or TF3300 Scope Server) headless into a temporary CSV " +
                 "and returns, per channel: name, symbol, comment, data type, sample time, NetId/port, scale/offset/unit, sample count, " +
                 "first/last time, min/max/mean. includeValues=true adds the values of the selected channels in a time window, " +
                 "decimated min/max per bucket (spikes survive) to at most maxPoints per channel. Times are ms since the recording start; " +
                 "start/end are UTC. Values are as the export tool writes them. The path must lie in an allowed directory " +
                 "(%TEMP%, %LOCALAPPDATA%\\ptc-xae-mcp, the TwinCAT installation or a root in PTC_XAE_MCP_ALLOWED_PATHS). " +
                 "Result: an [Experimental] line, a summary line, then JSON.")]
    public string ReadScopeFile(
        [Description("Path of the .svdx file.")] string path,
        [Description("Channels whose values to return (includeValues): channel names or symbol names, comma-separated. Empty = all channels.")] string? channels = null,
        [Description("Start of the value window in ms since the recording start. Empty = from the start.")] double? fromMs = null,
        [Description("End of the value window in ms since the recording start. Empty = to the end.")] double? toMs = null,
        [Description("Maximum points per channel (default 500, at most 5000); more samples are decimated to min/max per bucket.")] int maxPoints = ScopeExportCsv.DefaultMaxPoints,
        [Description("Return the (decimated) values, not only the per-channel statistics. Default false.")] bool includeValues = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_scope_file",
            new { path, channels, fromMs, toMs, maxPoints, includeValues }, () =>
                McpPathGuard.CheckRequiredFile(path, solutionDirectory: null, "path")
                ?? new ScopeFileReader().Read(path, channels, fromMs, toMs, maxPoints, includeValues), cancellationToken);

    [McpServerTool(Name = "twincat_convert_scope_file", ReadOnly = false, Destructive = false)]
    [Description("EXPERIMENTAL — convert a saved TwinCAT Scope recording (.svdx) into an export file without the XAE, using " +
                 "TC3ScopeExportTool.exe (TE130x Scope View / TF3300 Scope Server) headless. The target extension selects the format " +
                 "(.csv, .tdms, … as the export tool supports them; .tdms/.dat may need a full Scope View/Server licence). " +
                 "An existing target is refused unless overwrite=true. Both paths must lie in an allowed directory " +
                 "(%TEMP%, %LOCALAPPDATA%\\ptc-xae-mcp, the TwinCAT installation or a root in PTC_XAE_MCP_ALLOWED_PATHS).")]
    public string ConvertScopeFile(
        [Description("Path of the .svdx file.")] string path,
        [Description("Output file; its extension selects the format, e.g. C:\\temp\\run1.csv.")] string targetPath,
        [Description("Replace an existing target file. Default false.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_convert_scope_file", new { path, targetPath, overwrite }, () =>
            McpPathGuard.CheckRequiredFile(path, solutionDirectory: null, "path")
            ?? McpPathGuard.CheckRequiredFile(targetPath, solutionDirectory: null, "targetPath")
            ?? new ScopeFileReader().Convert(path, targetPath, overwrite), cancellationToken);
}
