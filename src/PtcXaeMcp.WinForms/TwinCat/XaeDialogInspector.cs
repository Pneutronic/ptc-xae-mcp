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
/// Lists the open dialogs of the XAE and presses a button of one — Win32 only, no COM and no single-operation lock,
/// so it works exactly when a COM call hangs on a dialog. A destructive dialog (<see cref="XaeDialogClassifier"/>) is
/// listed but never answered, and no destructive button is pressed on any dialog.
/// </summary>
internal static class XaeDialogInspector
{
    private const int MaxTexts = 12;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The dialogs of every running TcXaeShell (not its main window), the blocking ones first.</summary>
    public static IReadOnlyList<XaeDialogView> List() =>
        View(XaeDialogs.Snapshot(XaeWindowScope.For(IntPtr.Zero)), XaeDialogs.IsEnabled);

    /// <summary>Pure part of <see cref="List"/>: <paramref name="ownerEnabled"/> tells a modal (owner disabled) apart.</summary>
    internal static IReadOnlyList<XaeDialogView> View(IEnumerable<XaeDialog> windows, Func<IntPtr, bool> ownerEnabled) =>
        windows
            // A dialog has an owner or is a message box; an ownerless window is an XAE main window.
            .Where(d => d.Owner != IntPtr.Zero || d.IsMessageBox)
            .Select(d =>
            {
                var texts = d.Controls.Where(c => !c.IsButton && !c.IsEdit)
                    .Select(c => OneLine(c.Text)).Where(t => t.Length > 0).Distinct().Take(MaxTexts).ToList();
                var buttons = d.Controls.Where(c => c.IsButton && c.Caption.Length > 0)
                    .Select(c => (c.Caption, c.Enabled)).ToList();
                bool blocking = d.Owner != IntPtr.Zero ? !ownerEnabled(d.Owner) : d.IsMessageBox;
                return new XaeDialogView(IdOf(d.Hwnd), d.Title.Trim(), d.ClassName, blocking,
                    XaeDialogClassifier.Classify(d.Title, texts), texts, buttons);
            })
            .OrderByDescending(v => v.Blocking)
            .ToList();

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
        return sb.ToString().TrimEnd();
    }

    /// <summary>Presses <paramref name="button"/> on the dialog <paramref name="id"/> and reports whether it closed.</summary>
    public static string Answer(string id, string button)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(button))
            return "[Error] id and button are required (see twincat_xae_dialogs).";

        var snapshot = XaeDialogs.Snapshot(XaeWindowScope.For(IntPtr.Zero));
        var views = View(snapshot, XaeDialogs.IsEnabled);
        var view = views.FirstOrDefault(v => string.Equals(v.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (view is null)
            return McpErrors.WithNext($"No open XAE dialog has the id '{id}'. Nothing was pressed.",
                "twincat_xae_dialogs lists the open dialogs with their ids.");

        string? refused = Refusal(view, button);
        if (refused is not null) return refused;

        var dialog = snapshot.First(d => string.Equals(IdOf(d.Hwnd), view.Id, StringComparison.Ordinal));
        var plan = XaeDialogs.PlanClick(dialog, 0, button.Trim());
        if (!plan.UsesButton)
            return $"[Error] The dialog '{view.Title}' has no button '{button}'. Its buttons: " +
                   $"{string.Join(", ", view.Buttons.Select(b => $"'{b.Caption}'"))}. Nothing was pressed.";

        XaeDialogs.Click(plan.Button);
        bool closed = XaeDialogs.WaitClosed(dialog.Hwnd, CloseTimeout);
        if (!closed)
            return $"Pressed '{button.Trim()}' on '{view.Title}', but the dialog is still open after " +
                   $"{CloseTimeout.TotalSeconds:F0} s. Check twincat_xae_dialogs; it may ask something else now.";

        var next = List().Where(d => d.Blocking).ToList();
        return next.Count == 0
            ? $"Pressed '{button.Trim()}' on '{view.Title}'; the dialog closed and no other XAE dialog blocks."
            : $"Pressed '{button.Trim()}' on '{view.Title}'; the dialog closed, but another one is open now:\n{Describe(next)}";
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
