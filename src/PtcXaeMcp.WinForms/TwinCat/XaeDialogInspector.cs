using System.Globalization;
using System.Text;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>One open XAE dialog as the tools report it.</summary>
internal sealed record XaeDialogView(
    string Id,
    string Title,
    string ClassName,
    bool Blocking,
    XaeDialogClass Class,
    IReadOnlyList<string> Texts,
    IReadOnlyList<(string Caption, bool Enabled)> Buttons);

/// <summary>
/// Lists the open dialogs of the XAE and presses a button of one — Win32 and UI Automation only (WPF dialogs such
/// as the shell's "save changes?" prompt have no child windows), no COM and no single-operation lock, so it works
/// exactly when a COM call hangs on a dialog. A destructive dialog (<see cref="XaeDialogClassifier"/>) is listed but
/// never answered, and no destructive button is pressed on any dialog.
/// </summary>
internal static class XaeDialogInspector
{
    private const int MaxTexts = 12;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The dialogs of every running TcXaeShell (not its main window), the blocking ones first.</summary>
    public static IReadOnlyList<XaeDialogView> List() =>
        View(PromptWindowReader.Snapshot(XaeWindowScope.For(IntPtr.Zero)), XaeDialogs.IsEnabled);

    /// <summary>
    /// Pure part of <see cref="List"/>: <paramref name="isEnabled"/> tells a modal (owner disabled) apart, and a dialog
    /// that is itself disabled because another one is open on top of it (its buttons cannot be pressed then).
    /// </summary>
    internal static IReadOnlyList<XaeDialogView> View(IEnumerable<PromptWindow> windows, Func<IntPtr, bool> isEnabled) =>
        windows
            // A dialog has an owner or is a message box; an ownerless window is an XAE main window.
            .Where(w => w.Owner != IntPtr.Zero || IsMessageBox(w))
            .Select(w =>
            {
                var captions = w.Buttons.Select(b => b.Caption).Where(c => c.Length > 0).ToList();
                // WPF repeats every button caption as an inner text ("_Speichern"); those are not texts of the dialog.
                var texts = w.Texts.Select(OneLine)
                    .Where(t => t.Length > 0 && !captions.Contains(new PromptButton(t).Caption, StringComparer.OrdinalIgnoreCase))
                    .Distinct().Take(MaxTexts).ToList();
                bool blocking = w.Owner != IntPtr.Zero ? !isEnabled(w.Owner) : IsMessageBox(w);
                bool windowEnabled = isEnabled(w.Hwnd);
                var buttons = w.Buttons.Where(b => b.Caption.Length > 0)
                    .Select(b => (b.Caption, windowEnabled && (b.Hwnd == IntPtr.Zero || isEnabled(b.Hwnd)))).ToList();
                return new XaeDialogView(IdOf(w.Hwnd), w.Title.Trim(), w.ClassName, blocking,
                    XaeDialogClassifier.Classify(w.Title, texts), texts, buttons);
            })
            .OrderByDescending(v => v.Blocking)
            .ToList();

    private static bool IsMessageBox(PromptWindow w) => string.Equals(w.ClassName, "#32770", StringComparison.Ordinal);

    internal static string IdOf(IntPtr hwnd) => "0x" + hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture);

    public static string Describe(IReadOnlyList<XaeDialogView> dialogs)
    {
        if (dialogs.Count == 0) return "No XAE dialog is open.";
        var sb = new StringBuilder();
        int blocking = dialogs.Count(d => d.Blocking);
        sb.AppendLine(CultureInfo.InvariantCulture, $"{dialogs.Count} XAE dialog(s) open, {blocking} blocking the XAE.");
        foreach (var d in dialogs)
        {
            string title = d.Title.Length == 0 ? $"<untitled {d.ClassName}>" : d.Title;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- id {d.Id}: '{title}' — {XaeDialogClassifier.Name(d.Class)}{(d.Blocking ? ", blocking" : "")}");
            foreach (var t in d.Texts) sb.AppendLine(CultureInfo.InvariantCulture, $"    text: {t}");
            if (d.Buttons.Count > 0)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    buttons: {string.Join(", ", d.Buttons.Select(b => b.Enabled ? $"'{b.Caption}'" : $"'{b.Caption}' (disabled)"))}");
        }
        if (dialogs.Any(d => d.Class == XaeDialogClass.KnownDestructive))
            sb.AppendLine("A known-destructive dialog confirms a target-system, delete, safety or license action: a person answers it in the XAE.");
        if (dialogs.Count(d => d.Blocking) > 1)
            sb.AppendLine("Several dialogs block: answer the one on top first — a dialog whose buttons are all disabled waits for another.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Presses <paramref name="button"/> on the dialog <paramref name="id"/> and reports whether it closed.</summary>
    public static string Answer(string id, string button)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(button))
            return "[Error] id and button are required (see twincat_xae_dialogs).";

        var windows = PromptWindowReader.Snapshot(XaeWindowScope.For(IntPtr.Zero));
        var view = View(windows, XaeDialogs.IsEnabled)
            .FirstOrDefault(v => string.Equals(v.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (view is null)
            return McpErrors.WithNext($"No open XAE dialog has the id '{id}'. Nothing was pressed.",
                "twincat_xae_dialogs lists the open dialogs with their ids.");

        string? refused = Refusal(view, button);
        if (refused is not null) return refused;

        var window = windows.First(w => string.Equals(IdOf(w.Hwnd), view.Id, StringComparison.Ordinal));
        string wanted = new PromptButton(button.Trim()).Caption;
        var target = window.Buttons.FirstOrDefault(b => string.Equals(b.Caption, wanted, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return $"[Error] The dialog '{view.Title}' has no button '{button}'. Its buttons: " +
                   $"{string.Join(", ", view.Buttons.Select(b => $"'{b.Caption}'"))}. Nothing was pressed.";
        if (!XaeDialogs.IsEnabled(window.Hwnd) || (target.Hwnd != IntPtr.Zero && !XaeDialogs.IsEnabled(target.Hwnd)))
            return McpErrors.WithNext($"'{target.Caption}' on '{view.Title}' is disabled: the dialog waits for another one on top of it. Nothing was pressed.",
                "twincat_xae_dialogs; answer the dialog whose buttons are enabled first.");

        try { PromptWindowReader.Press(window, target); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Windows.Automation.ElementNotAvailableException)
        {
            return $"[Error] Could not press '{target.Caption}' on '{view.Title}': {ex.Message}";
        }

        bool closed = XaeDialogs.WaitClosed(window.Hwnd, CloseTimeout);
        if (!closed)
            return $"Pressed '{target.Caption}' on '{view.Title}', but the dialog is still open after " +
                   $"{CloseTimeout.TotalSeconds:F0} s. Check twincat_xae_dialogs; it may ask something else now.";

        var next = List().Where(d => d.Blocking).ToList();
        return next.Count == 0
            ? $"Pressed '{target.Caption}' on '{view.Title}'; the dialog closed and no other XAE dialog blocks."
            : $"Pressed '{target.Caption}' on '{view.Title}'; the dialog closed, but another one is open now:\n{Describe(next)}";
    }

    /// <summary>Why a tool may not press <paramref name="button"/> on <paramref name="view"/>, or null. Pure — unit-tested.</summary>
    internal static string? Refusal(XaeDialogView view, string button)
    {
        if (view.Class == XaeDialogClass.KnownDestructive)
            return $"[Error] '{view.Title}' confirms a target-system, delete, safety or license action. A person answers it " +
                   "in the XAE; no tool does. Nothing was pressed.";
        if (XaeDialogClassifier.IsDestructiveButton(button))
            return $"[Error] '{button.Trim()}' is a destructive answer. A person presses it in the XAE; no tool does. Nothing was pressed.";
        return null;
    }

    private static string OneLine(string text) =>
        string.Join(' ', (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())).Trim();
}
