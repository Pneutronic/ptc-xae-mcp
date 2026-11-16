using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.TwinCat;
using System.Globalization;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_locate_step", ReadOnly = true)]
    [Description(
        "HEADLESS: find the CASE branch of a step in an ST POU's step chain. Looks for 'CASE <stepVariable> OF … 1010: …'; " +
        "only numeric literal labels match ('1010:' or '100, 1110:'). Ranges, constants and enum labels are listed in the error " +
        "but never matched — then read the POU and call twincat_goto_line yourself. Returns the 1-based implementation line " +
        "(the pane's gutter number, not the .TcPOU file line), the label, the branch end line and the branch source. Does not " +
        "touch the editor.")]
    public string LocateStep(
        [Description("Caret-separated tree path of the POU, as for twincat_plc_read.")] string treePath,
        [Description("Numeric step value, e.g. read via ADS from MAIN.fbMachine.Step.")] int step,
        [Description("CASE selector variable. Default 'Step'.")] string? stepVariable = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_locate_step", new { treePath, step, stepVariable }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, loc) = new EditorNavigationCoordinator(bridge.Dispatcher.Session)
                    .LocateStep(treePath, step, stepVariable);
                return loc is null ? summary : summary + "\n" + FormatBranch(loc);
            }), cancellationToken);

    [McpServerTool(Name = "twincat_goto_line", ReadOnly = true)]
    [Description(
        "VISIBLE: put the XAE editor cursor on a line of a POU. Opens the object's tab (as twincat_open_object) and " +
        "answers the 'Go To Line' dialog. Lines count as in the twincat_plc_read text of that section. Works while Online " +
        "(no logout), but Online the declaration pane is the monitoring grid — use the implementation then. Confirmed " +
        "structurally (dialog seen and closed), no caret read-back. Needs the XAE in the foreground: without bringToFront=true " +
        "it is an [Error] unless the XAE already is in front.")]
    public string GotoLine(
        [Description("Caret-separated tree path of the POU, as for twincat_plc_read.")] string treePath,
        [Description("1-based line within the section's pane.")] int line,
        [Description("'implementation' (default) or 'declaration'.")] string? section = null,
        [Description(BringToFrontHelp)] bool bringToFront = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_goto_line", new { treePath, line, section, bringToFront }, () =>
            bridge.Dispatcher.Run(() =>
                new EditorNavigationCoordinator(bridge.Dispatcher.Session).GotoLine(treePath, line, section, bringToFront).Summary), cancellationToken);

    // twincat_show_step moved to TwinCatMcpToolsDiagnostics (Mcp/TwinCatMcpTools.Diagnostics.cs) —
    // it is a demo/showcase tool, registered only with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

    /// <summary>Shared by twincat_locate_step here and twincat_show_step in the diagnostics tool type.</summary>
    internal static string FormatBranch(StepLocation loc)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"--- Branch (implementation lines {loc.Line}-{loc.EndLine}) ---");
        sb.Append(loc.Snippet);
        return sb.ToString().TrimEnd();
    }
}
