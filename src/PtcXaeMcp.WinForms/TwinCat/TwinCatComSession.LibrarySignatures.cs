using PtcXaeMcp.WinForms.TwinCat.HostSelection;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    private const int HrMemberNotFound = unchecked((int)0x80020003); // DISP_E_MEMBERNOTFOUND
    private const int HrUnknownName = unchecked((int)0x80020006);    // DISP_E_UNKNOWNNAME
    private const int HrNoInterface = unchecked((int)0x80004002);    // E_NOINTERFACE

    // ITcPlcLibraryManager2.ProduceAllLibrarySignatures (Infosys TE1000 "Exporting PLC library signatures"). The C# sample
    // casts the References item, the PowerShell sample calls it on the PLC project item; the References item reached by
    // LookupTreeItem is the one that dispatches the library manager's mutating calls here (GetLibraryManager), so it is
    // asked first. Only "this node does not offer the method" moves on to the project node — an error the XAE raises from
    // the method itself ("Couldn't resolve libraries!") is the answer and is passed on.
    public LibrarySignatureExport ProduceLibrarySignatures(string? plcProjectName = null) =>
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            var notOffered = new List<string>();
            string? plcInstance = ResolvePlcInstanceName(plcProjectName);

            dynamic? refsNode = FindLibraryManagerNode(dte, plcInstance);
            if (refsNode is not null)
            {
                try { return new LibrarySignatureExport((string)refsNode.ProduceAllLibrarySignatures(), "References", HostName()); }
                catch (Exception ex) when (ComCall.NotBusy(ex) && NotOffered(ex)) { notOffered.Add($"References node: {ex.Message}"); }
            }

            dynamic? plcProj = FindPlcSourceProjectNode(dte, plcInstance);
            if (plcProj is not null)
            {
                try { return new LibrarySignatureExport((string)plcProj.ProduceAllLibrarySignatures(), "PLC project", HostName()); }
                catch (Exception ex) when (ComCall.NotBusy(ex) && NotOffered(ex)) { notOffered.Add($"PLC project node: {ex.Message}"); }
            }

            throw new InvalidOperationException(notOffered.Count == 0
                ? "No PLC project with a References node found — open a solution with a PLC project first."
                : "This XAE offers no ProduceAllLibrarySignatures (ITcPlcLibraryManager2) — " + string.Join("; ", notOffered));
        }, OpenTimeout);

    private static bool NotOffered(Exception ex) =>
        ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
        || ex.HResult is HrMemberNotFound or HrUnknownName or HrNoInterface;

    private static string HostName()
    {
        var hosts = XaeHostReport.RunningHosts().Select(XaeHostReport.Name).ToList();
        return hosts.Count == 0 ? "unknown" : string.Join(", ", hosts);
    }
}
