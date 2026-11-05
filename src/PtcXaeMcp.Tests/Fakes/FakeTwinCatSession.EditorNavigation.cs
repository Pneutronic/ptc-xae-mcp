using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- editor-navigation ---------------------------------------------------

    public Exception? GotoLineThrows { get; set; }
    public GotoLineResult FakeGotoLineResult { get; set; } =
        new("^POUs^MAIN", EditorSection.Implementation, 1, Opened: true, AlreadyOpen: false,
            PaneFocused: true, DialogSeen: true, CursorConfirmed: true,
            ForegroundRequested: true, ForegroundConfirmed: true, Trigger: "Edit.GoTo command", Note: null);

    public GotoLineResult GotoLine(string treePath, int line, EditorSection section, bool bringToFront = false)
    {
        CallLog.Add($"GotoLine({treePath}, {line}, {section}{(bringToFront ? ",front" : "")})");
        if (GotoLineThrows is not null) throw GotoLineThrows;
        return FakeGotoLineResult with { TreePath = treePath, Line = line, Section = section };
    }
}
