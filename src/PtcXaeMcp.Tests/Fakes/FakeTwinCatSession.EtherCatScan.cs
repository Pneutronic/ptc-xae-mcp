using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- ethercat-scan ----
    public EtherCatScanData FakeScanData { get; set; } = new("TIID^Device 1 (EtherCAT)", [], []);
    public Exception? ScanEtherCatMasterThrows { get; set; }

    public EtherCatScanData ScanEtherCatMaster(string masterTreePath)
    {
        CallLog.Add($"ScanEtherCatMaster({masterTreePath})");
        if (ScanEtherCatMasterThrows is not null) throw ScanEtherCatMasterThrows;
        return FakeScanData with { MasterTreePath = masterTreePath };
    }
}
