using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- visible-for-video --------------------------------------------------

    public Exception? OpenPlcObjectThrows { get; set; }
    public OpenPlcObjectResult FakeOpenPlcObjectResult { get; set; } =
        new("^POUs^MAIN", Opened: true, AlreadyOpen: false,
            ForegroundRequested: true, ForegroundConfirmed: true, Note: null);

    public OpenPlcObjectResult OpenPlcObject(string treePath, bool bringToFront = false)
    {
        CallLog.Add($"OpenPlcObject({treePath}{(bringToFront ? ",front" : "")})");
        if (OpenPlcObjectThrows is not null) throw OpenPlcObjectThrows;
        return FakeOpenPlcObjectResult;
    }

    public Exception? ClosePlcObjectThrows { get; set; }
    public ClosePlcObjectResult FakeClosePlcObjectResult { get; set; } =
        new("^POUs^MAIN", Closed: true, WasOpen: true, Saved: true, Note: null);

    public ClosePlcObjectResult ClosePlcObject(string treePath)
    {
        CallLog.Add($"ClosePlcObject({treePath})");
        if (ClosePlcObjectThrows is not null) throw ClosePlcObjectThrows;
        return FakeClosePlcObjectResult;
    }
}
