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
        "HEADLESS: find the CASE branch of a step in an ST POU's step chain (FB_Base style " +
        "'CASE Step OF … 1010: …'). treePath is the caret-separated POU path (as twincat_plc_read); " +
        "step is the numeric step value you read via ADS (e.g. MAIN.fbMachine.Step); stepVariable " +
        "is the CASE selector (default 'Step'). Only NUMERIC LITERAL labels are matched ('1010:' " +
        "or a comma list '100, 1110:'); ranges, constants and enum labels are listed in the error " +
        "but never matched — on a miss, read the POU yourself and call twincat_goto_line with the " +
        "line you determined. Returns the 1-based IMPLEMENTATION line (= the implementation pane's " +
        "gutter number, NOT the .TcPOU file line), the label, the branch end line and the branch " +
        "source so you can quote it. Does not touch the editor.")]
    public string LocateStep(
        [Description("Caret-separated tree path of the POU to search, as used by twincat_plc_read.")] string treePath,
        [Description("Numeric step value to locate (as read live via ADS, e.g. MAIN.fbMachine.Step).")] int step,
        [Description("Name of the CASE selector variable. Defaults to 'Step' when omitted.")] string? stepVariable = null,
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
        "VISIBLE: put the editor cursor on a line of a POU in the running TwinCAT XAE so a live " +
        "demo shows exactly the code being discussed. Opens the object's tab (as twincat_open_object), " +
        "focuses the requested pane via UIAutomation and runs the VS Edit.GoTo command, answering its " +
        "'Go To Line' dialog. line is 1-based within the pane: section='implementation' (default) " +
        "counts lines of the implementation pane (= twincat_plc_read implementation text split on " +
        "newlines), section='declaration' counts the declaration pane. Works while Online (no logout; " +
        "monitoring values stay visible) — but the declaration pane is the monitoring grid while " +
        "Online, so use the implementation section then. Confirmation is structural (the Go-To dialog " +
        "was seen and closed on OK); there is no caret read-back. Placing the cursor needs the XAE in the " +
        "foreground: pass bringToFront=true (only when a person asked where it is); without it the call is an " +
        "[Error] unless the XAE already is in front.")]
    public string GotoLine(
        [Description("Caret-separated tree path of the POU to open, as used by twincat_plc_read.")] string treePath,
        [Description("1-based line number within the pane selected by 'section'.")] int line,
        [Description("Which pane 'line' counts within: 'implementation' (default) or 'declaration'.")] string? section = null,
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
