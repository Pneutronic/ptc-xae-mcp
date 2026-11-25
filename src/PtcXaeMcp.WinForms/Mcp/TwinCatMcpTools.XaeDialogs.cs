using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_xae_dialogs", ReadOnly = true)]
    [Description("List the XAE's open dialogs with id, texts, buttons and a safety class. " +
                 "Also: title, whether it blocks the XAE; class 'known-safe', 'unknown' or 'known-destructive' (confirms a " +
                 "target-system, delete, safety or license action; a person answers those in the XAE). Use it when a call " +
                 "reports the XAE busy or blocked by a dialog; when several block, answer the one with enabled buttons first. " +
                 "Answers while another call holds the XAE; changes nothing.")]
    public string XaeDialogsList(CancellationToken cancellationToken = default)
        // Dispatcher and gate bypass: the dialog it shows is what a running call waits for.
        => bridge.Activity.Trace("twincat_xae_dialogs", null,
            () => XaeDialogInspector.Describe(XaeDialogInspector.List()) + WaitingCall(bridge.Dispatcher.Detached, DateTime.UtcNow),
            cancellationToken, bypassGate: true);

    /// <summary>The line naming the call a dialog holds after its [Blocked] answer; empty when none. Pure — unit-tested.</summary>
    internal static string WaitingCall(DetachedCall? call, DateTime nowUtc) => call switch
    {
        null => "",
        { Dialog: null } =>
            $"\nHung call: {call.Tool} (reported as hung {Math.Max(0, (int)(nowUtc - call.SinceUtc).TotalSeconds)} s ago, " +
            "no dialog). Until it returns no other call starts; twincat_force_close_xae ends the XAE and frees the lock.",
        _ =>
            $"\nWaiting for a dialog: {call.Tool} (answered [Blocked] {Math.Max(0, (int)(nowUtc - call.SinceUtc).TotalSeconds)} s ago for " +
            $"'{call.Dialog.Title}', id {call.Dialog.Id}). It returns once the dialog is answered; until then no other call starts.",
    };

    [McpServerTool(Name = "twincat_xae_dialog_answer", ReadOnly = false, Destructive = false)]
    [Description("Press a button of an open XAE dialog listed by twincat_xae_dialogs. " +
                 "Refused for a known-destructive dialog and for a destructive button (activate, restart, download, login, " +
                 "delete, overwrite, …) on any dialog: a person answers those. Reports whether the dialog closed and whether " +
                 "another followed. Works while another call is blocked by the dialog.")]
    public string XaeDialogAnswer(
        [Description("The dialog's id from twincat_xae_dialogs, e.g. '0x1A2B3C'.")] string id,
        [Description("Button caption as listed, e.g. 'Neu laden'.")] string button,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_xae_dialog_answer", new { id, button },
            () => XaeDialogInspector.Answer(id, button), cancellationToken, bypassGate: true);
}
