using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>An XAE dialog a running call waits for, as the [Blocked] answer names it.</summary>
internal sealed record BlockingXaeDialog(string Id, string Title, XaeDialogClass Class, IReadOnlyList<string> Buttons)
{
    /// <summary>"'Title' (id 0x1A2B, known-safe; buttons: 'Ja', 'Nein')".</summary>
    public string Describe()
    {
        string title = Title.Length == 0 ? "<untitled>" : Title;
        string buttons = Buttons.Count == 0 ? "none" : string.Join(", ", Buttons.Select(b => $"'{b}'"));
        return string.Create(CultureInfo.InvariantCulture,
            $"'{title}' (id {Id}, {XaeDialogClassifier.Name(Class)}; buttons: {buttons})");
    }

    /// <summary>What to do about it: a destructive dialog is answered by a person, any other one by the tool too.</summary>
    public string HowToAnswer() => Class == XaeDialogClass.KnownDestructive
        ? "a person answers it in the XAE (it confirms a target-system, delete, safety or license action; twincat_xae_dialog_answer refuses it)"
        : "answer it with twincat_xae_dialog_answer or in the XAE";
}

/// <summary>
/// Decides, poll by poll, whether a call that has not returned is held by an XAE dialog. Two probes, both Win32/UI
/// Automation only (no COM): a cheap one (<see cref="XaeModalProbe.FindBlockingDialog"/>) every poll, and a detailed
/// one (<see cref="CurrentQuestion"/>, buttons and class) only once the same blocking window has stayed for the grace
/// time. A window without a question — no button other than Cancel, like a progress or wait window of a long
/// build — is not a block; it is looked at again every <see cref="DetailRecheck"/>. Not thread-safe: one instance per
/// call, polled by one thread.
/// </summary>
internal sealed class XaeCallBlockWatch
{
    /// <summary>How long a dialog must stay before the call is reported as blocked.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(3);

    /// <summary>How often the dispatcher polls.</summary>
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(500);

    /// <summary>How often a blocking window without a question (progress) is read in detail again.</summary>
    public static readonly TimeSpan DetailRecheck = TimeSpan.FromSeconds(5);

    // A wait window offers only these. "Schließen"/"Close" is not among them: an error box with only Close blocks too.
    private static readonly string[] CancelCaptions = ["Abbrechen", "Cancel"];

    private readonly Func<string?> _quick;
    private readonly Func<BlockingXaeDialog?> _detail;
    private readonly TimeSpan _grace;
    private string? _seen;
    private TimeSpan _since;
    private TimeSpan? _lastDetail;

    public XaeCallBlockWatch(Func<string?> quick, Func<BlockingXaeDialog?> detail, TimeSpan grace)
    {
        _quick = quick;
        _detail = detail;
        _grace = grace;
    }

    /// <summary>The watch every MCP call gets: the real XAE probes and <see cref="DefaultGrace"/>.</summary>
    public static XaeCallBlockWatch ForXae() =>
        new(XaeModalProbe.FindBlockingDialog, CurrentQuestion, DefaultGrace);

    /// <summary>One poll at <paramref name="now"/> (time since the call started). The dialog once it has blocked for
    /// the grace time and asks something; null otherwise. Never throws.</summary>
    public BlockingXaeDialog? Tick(TimeSpan now)
    {
        string? seen = Safe(_quick);
        if (seen is null)
        {
            _seen = null;
            return null;
        }
        if (!string.Equals(seen, _seen, StringComparison.Ordinal))
        {
            _seen = seen;
            _since = now;
            _lastDetail = null;
            return null;
        }
        if (now - _since < _grace) return null;
        if (_lastDetail is { } last && now - last < DetailRecheck) return null;
        _lastDetail = now;
        return Safe(_detail);
    }

    /// <summary>The blocking XAE dialog that asks something right now, or null. Never throws.</summary>
    public static BlockingXaeDialog? CurrentQuestion()
    {
        try { return Pick(XaeDialogInspector.List()); }
        catch (Exception) { return null; } // Win32/UIA hiccup: "no dialog known" this poll
    }

    /// <summary>
    /// Pure choice among listed dialogs: blocking ones with a question; the one whose buttons can be pressed (the top
    /// of a modal chain) first. Unit-tested.
    /// </summary>
    internal static BlockingXaeDialog? Pick(IReadOnlyList<XaeDialogView> dialogs) =>
        dialogs.Where(d => d.Blocking && AsksSomething(d))
            .OrderByDescending(d => d.Buttons.Any(b => b.Enabled))
            .Select(d => new BlockingXaeDialog(d.Id, d.Title, d.Class, d.Buttons.Select(b => b.Caption).ToList()))
            .FirstOrDefault();

    /// <summary>True when the dialog has a button other than Cancel: a question, not a progress window.</summary>
    internal static bool AsksSomething(XaeDialogView d) =>
        d.Buttons.Any(b => !CancelCaptions.Contains(b.Caption.Trim(), StringComparer.OrdinalIgnoreCase));

    private static T? Safe<T>(Func<T?> probe) where T : class
    {
        try { return probe(); }
        catch (Exception) { return null; }
    }
}
