using System.Diagnostics;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    /// <summary>
    /// Opens Tools > Options in the XAE. <c>ExecuteCommand</c> runs the dialog's modal loop inside the call, so it
    /// returns only when the dialog is closed (verified live 2026-11-06, TC3 4026 German XAE: blocked until WM_CLOSE).
    /// No busy retry: a rejected call must not open the dialog a second time.
    /// </summary>
    public string OpenOptionsDialogAndWait()
    {
        dynamic dte = GetActiveTcDte();
        var sw = Stopwatch.StartNew();
        dte.ExecuteCommand("Tools.Options");
        return $"The XAE Options dialog was closed after {sw.Elapsed.TotalSeconds:F1} s.";
    }
}
