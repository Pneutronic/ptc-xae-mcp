using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- open-object (generalization) ---------------------------------------

    public Exception? OpenObjectThrows { get; set; }
    public OpenObjectResult FakeOpenObjectResult { get; set; } =
        new("^POUs^MAIN", OpenObjectKind.PlcObject, Opened: true, AlreadyOpen: false,
            ForegroundRequested: true, ForegroundConfirmed: true, Note: null);

    public OpenObjectResult OpenObject(string treePath, bool bringToFront = false)
    {
        CallLog.Add($"OpenObject({treePath}{(bringToFront ? ",front" : "")})");
        if (OpenObjectThrows is not null) throw OpenObjectThrows;
        return FakeOpenObjectResult with { TreePath = treePath };
    }

    public Exception? CloseObjectThrows { get; set; }
    public CloseObjectResult FakeCloseObjectResult { get; set; } =
        new("^POUs^MAIN", OpenObjectKind.PlcObject, Closed: true, WasOpen: true, Saved: true, Note: null);

    public CloseObjectResult CloseObject(string treePath)
    {
        CallLog.Add($"CloseObject({treePath})");
        if (CloseObjectThrows is not null) throw CloseObjectThrows;
        return FakeCloseObjectResult with { TreePath = treePath };
    }
}
