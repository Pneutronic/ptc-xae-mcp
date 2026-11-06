using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_xae_dialogs", ReadOnly = true)]
    [Description("List the dialogs the XAE has open: id, title, texts, buttons, whether it blocks the XAE, and a class — " +
                 "'known-safe', 'unknown', or 'known-destructive' (confirms a target-system, delete, safety or license action; " +
                 "a person answers those in the XAE). Use it when a call reports that the XAE is busy or blocked by a dialog. " +
                 "When several block, answer the one whose buttons are enabled first. " +
                 "Win32 and UI Automation only (also WPF prompts), no COM: answers while another call holds the XAE, changes nothing.")]
    public string XaeDialogsList(CancellationToken cancellationToken = default)
        // Dispatcher and gate bypass: the dialog it shows is what a running call waits for.
        => bridge.Activity.Trace("twincat_xae_dialogs", null,
            () => XaeDialogInspector.Describe(XaeDialogInspector.List()), cancellationToken, bypassGate: true);

    [McpServerTool(Name = "twincat_xae_dialog_answer", ReadOnly = false, Destructive = false)]
    [Description("Press a button of an open XAE dialog listed by twincat_xae_dialogs (id and the button's caption, as listed). " +
                 "Refused for a known-destructive dialog and for a destructive button (activate, restart, download, login, delete, " +
                 "overwrite, …) on any dialog: a person answers those in the XAE. Reports whether the dialog closed and whether " +
                 "another one followed. Win32 and UI Automation only, no COM: works while another call is blocked by the dialog.")]
    public string XaeDialogAnswer(
        [Description("The dialog's id from twincat_xae_dialogs, e.g. '0x1A2B3C'.")] string id,
        [Description("The caption of the button to press, as listed (e.g. 'Ja', 'Neu laden', 'OK').")] string button,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_xae_dialog_answer", new { id, button },
            () => XaeDialogInspector.Answer(id, button), cancellationToken, bypassGate: true);
}
