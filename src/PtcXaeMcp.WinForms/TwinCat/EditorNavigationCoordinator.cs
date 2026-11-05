using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

// Orchestrates the editor-navigation tools (twincat_locate_step / twincat_goto_line /
// twincat_show_step). Same shape as PlcCodeCoordinator: cheap argument validation
// before any session touch, then SessionGuards.RequireOpenSolution, then one session
// call wrapped so every exception becomes an "[Error] …" summary.
//
// locate_step is headless (ReadPou + StepLocator). goto_line is the visible cursor
// move. show_step composes the two and refuses to touch the editor when the step
// cannot be located — no half-done navigation.
internal sealed class EditorNavigationCoordinator(ITwinCatSession session)
{
    public const string DefaultStepVariable = "Step";
    private static readonly Regex Identifier = new(@"^[A-Za-z_][\w.\[\]]*$", RegexOptions.Compiled);

    // ---- twincat_locate_step ---------------------------------------------------

    public (string Summary, StepLocation? Location) LocateStep(string treePath, int step, string? stepVariable)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            return ("[Error] treePath is required.", null);

        string selector = string.IsNullOrWhiteSpace(stepVariable) ? DefaultStepVariable : stepVariable.Trim();
        if (!Identifier.IsMatch(selector))
            return ($"[Error] stepVariable must be an IEC identifier (e.g. 'Step'), got '{selector}'.", null);

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, null);

        try
        {
            var body = session.ReadPou(treePath);
            if (!string.Equals(body.Language, "ST", StringComparison.OrdinalIgnoreCase))
                return ($"[Error] POU '{treePath}' is implemented in {body.Language}; step location works on ST implementations only.", null);

            var r = StepLocator.Locate(body.Implementation, step, selector);
            if (!r.Found)
                return ($"[Error] Step {step} not located in '{treePath}': {r.Reason}. " +
                        "Determine the implementation line yourself from twincat_plc_read and use twincat_goto_line.", null);

            var loc = new StepLocation(treePath, step, selector, r.Line, r.Label, r.EndLine, r.Snippet);
            return (FormatLocate(loc), loc);
        }
        catch (Exception ex)
        {
            return ($"[Error] {ex.Message}", null);
        }
    }

    private static string FormatLocate(StepLocation loc) =>
        $"Located step {loc.Step} in '{loc.TreePath}': implementation line {loc.Line} " +
        $"(label '{loc.Label}:'), branch ends at line {loc.EndLine}.";

    // ---- twincat_goto_line -----------------------------------------------------

    public (string Summary, GotoLineResult? Result) GotoLine(string treePath, int line, string? section, bool bringToFront = false)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            return ("[Error] treePath is required.", null);
        if (line < 1)
            return ($"[Error] line must be >= 1 (1-based pane line), got {line}.", null);
        if (!TryParseSection(section, out var sec))
            return ($"[Error] section must be 'implementation' or 'declaration', got '{section}'.", null);

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, null);

        try
        {
            var r = session.GotoLine(treePath, line, sec, bringToFront);
            return (FormatGoto(r), r);
        }
        catch (Exception ex)
        {
            return ($"[Error] {ex.Message}", null);
        }
    }

    internal static bool TryParseSection(string? section, out EditorSection result)
    {
        string s = (section ?? string.Empty).Trim();
        if (s.Length == 0 || s.Equals("implementation", StringComparison.OrdinalIgnoreCase)) { result = EditorSection.Implementation; return true; }
        if (s.Equals("declaration", StringComparison.OrdinalIgnoreCase)) { result = EditorSection.Declaration; return true; }
        result = EditorSection.Implementation;
        return false;
    }

    // Cursor confirmation is structural (dialog seen + closed on OK) and is the one
    // thing that must hold for a success; tab activation is reported as OpenPlcObject
    // does and the OS foreground stays best-effort (never an error on its own).
    private static string FormatGoto(GotoLineResult r)
    {
        string sec = r.Section.ToString().ToLowerInvariant();
        string notePart = string.IsNullOrWhiteSpace(r.Note) ? "" : $" — {r.Note}";
        string fgPart = !r.ForegroundRequested ? "XAE left where it is (brought to the foreground only with bringToFront=true)"
            : r.ForegroundConfirmed ? "XAE raised to foreground"
            : "OS foreground requested but not confirmed (Windows may block focus steal)";

        if (!r.CursorConfirmed)
        {
            string why = !r.PaneFocused ? $"{sec} pane could not be focused"
                : !r.DialogSeen ? "the Go-To dialog never appeared"
                : "the Go-To dialog did not close on OK";
            return $"[Error] Cursor NOT confirmed for {sec} line {r.Line} of '{r.TreePath}': {why}; {fgPart}.{notePart}";
        }

        string tabPart = r.AlreadyOpen ? "tab re-activated" : "tab opened and activated";
        return $"Cursor set to {sec} line {r.Line} of '{r.TreePath}': {tabPart}; {sec} pane focused; " +
               $"Go-To dialog seen and closed via {r.Trigger}; {fgPart}.{notePart}";
    }

    // ---- twincat_show_step -----------------------------------------------------

    public (string Summary, StepLocation? Location, GotoLineResult? Result) ShowStep(string treePath, int step, string? stepVariable, bool bringToFront = false)
    {
        var (locateSummary, loc) = LocateStep(treePath, step, stepVariable);
        if (loc is null) return (locateSummary, null, null);

        var (gotoSummary, r) = GotoLine(treePath, loc.Line, "implementation", bringToFront);
        if (r is null || McpErrors.IsError(gotoSummary))
            return ($"{gotoSummary} (step {step} was located at implementation line {loc.Line})", loc, r);

        return ($"{locateSummary}\n{gotoSummary}", loc, r);
    }
}
