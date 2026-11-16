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
    [Description("Read a saved TwinCAT Scope recording (.svdx) without the XAE. EXPERIMENTAL. " +
                 "Needs TE130x Scope View or TF3300 Scope Server installed. Per channel returns name, symbol, comment, data type, " +
                 "sample time, NetId/port, scale/offset/unit, sample count, first/last time, min/max/mean; includeValues=true adds " +
                 "values in a time window, decimated to min/max per bucket (spikes survive). Times are ms since the recording start; " +
                 "start/end are UTC. path must lie in an allowed directory (%TEMP%, %LOCALAPPDATA%\\ptc-xae-mcp, the TwinCAT " +
                 "installation or PTC_XAE_MCP_ALLOWED_PATHS). Result: an [Experimental] line, a summary line, then JSON.")]
    public string ReadScopeFile(
        [Description("Path of the .svdx file, e.g. from twincat_save_scope_data.")] string path,
        [Description("Channel or symbol names for includeValues, comma-separated. Empty = all.")] string? channels = null,
        [Description("Window start in ms since the recording start. Empty = from the start.")] double? fromMs = null,
        [Description("Window end in ms since the recording start. Empty = to the end.")] double? toMs = null,
        [Description("Max points per channel (default 500, at most 5000).")] int maxPoints = ScopeExportCsv.DefaultMaxPoints,
        [Description("Also return the values, not only the statistics. Default false.")] bool includeValues = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_scope_file",
            new { path, channels, fromMs, toMs, maxPoints, includeValues }, () =>
                McpPathGuard.CheckRequiredFile(path, solutionDirectory: null, "path")
                ?? new ScopeFileReader().Read(path, channels, fromMs, toMs, maxPoints, includeValues), cancellationToken);

    [McpServerTool(Name = "twincat_convert_scope_file", ReadOnly = false, Destructive = false)]
    [Description("Convert a saved TwinCAT Scope recording (.svdx) to CSV, TDMS or similar. EXPERIMENTAL; runs " +
                 "without the XAE, needs TE130x Scope View or TF3300 Scope Server installed (.tdms/.dat may need a full licence). " +
                 "An existing target is refused unless overwrite=true. Both paths must lie in an allowed directory " +
                 "(%TEMP%, %LOCALAPPDATA%\\ptc-xae-mcp, the TwinCAT installation or PTC_XAE_MCP_ALLOWED_PATHS).")]
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
