namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // PLC Static Analysis through the Automation Interface (Infosys TE1200 "Automation Interface support"), on the PLC
    // project item ("TIPC^<plc>^<plc> Project", the node FindPlcSourceProjectNode returns):
    //   ITcPlcIECProject4.LoadStaticAnalysisSettings(file)  >= 4026
    //   ITcPlcIECProject3.RunStaticAnalysis(bCheckAll)      >= 4024
    //   ITcPlcIECProject7.ExportToSarif(path)               >= 4026.20
    // Late-bound like SaveAsLibrary. An XAE that does not offer ExportToSarif still runs the analysis; the caller then
    // reads the Error List and the note says why.
    public StaticAnalysisRun RunStaticAnalysis(bool checkAll, string? settingsFile, string sarifPath) =>
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            dynamic plcProj = FindPlcSourceProjectNode(dte)
                ?? throw new InvalidOperationException("No PLC source-project node found under TIPC — open a solution first.");

            if (!string.IsNullOrWhiteSpace(settingsFile))
            {
                try { plcProj.LoadStaticAnalysisSettings(settingsFile); }
                catch (Exception ex) when (ComCall.NotBusy(ex))
                {
                    throw new InvalidOperationException($"LoadStaticAnalysisSettings('{settingsFile}') failed: {ex.Message}", ex);
                }
            }

            string node = "PLC project";
            try { plcProj.RunStaticAnalysis(checkAll); }
            catch (Exception ex) when (ComCall.NotBusy(ex) && IsMissingDispatchMember(ex))
            {
                // An XAE without the bCheckAll overload offers only "Run static analysis" (objects in use).
                plcProj.RunStaticAnalysis();
                node = "PLC project, RunStaticAnalysis() without checkAll";
            }

            string? note = null;
            try { plcProj.ExportToSarif(sarifPath); }
            catch (Exception ex) when (ComCall.NotBusy(ex) && IsMissingDispatchMember(ex))
            {
                note = "ExportToSarif is not offered by this XAE (needs TwinCAT 3.1.4026.20)";
            }
            catch (Exception ex) when (ComCall.NotBusy(ex)) { note = $"ExportToSarif failed: {ex.Message}"; }

            bool written = note is null && File.Exists(sarifPath);
            if (note is null && !written) note = "ExportToSarif returned but wrote no file";
            return new StaticAnalysisRun(written, note, node);
        }, OpenTimeout);

    // The member is not on this object's IDispatch (an older XAE): RuntimeBinder, DISP_E_UNKNOWNNAME,
    // DISP_E_MEMBERNOTFOUND, DISP_E_BADPARAMCOUNT.
    private static bool IsMissingDispatchMember(Exception ex) =>
        ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
        || ex.HResult is unchecked((int)0x80020006) or unchecked((int)0x80020003) or unchecked((int)0x8002000E);
}
