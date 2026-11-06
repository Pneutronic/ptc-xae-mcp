namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>How far a tool may go with an XAE dialog.</summary>
internal enum XaeDialogClass
{
    /// <summary>Known and harmless to answer (a file changed outside the editor, Go To).</summary>
    KnownSafe,
    /// <summary>Confirms a target-system, delete, safety or license action: only a person answers it, in the XAE.</summary>
    KnownDestructive,
    /// <summary>Neither; may be answered, but not with a destructive button.</summary>
    Unknown,
}

/// <summary>
/// Sorts XAE dialogs by their title and texts, German and English. The destructive patterns are fixed here and
/// cannot be relaxed by any setting: a dialog that activates, restarts, downloads, logs in, deletes, touches safety
/// or asks for a license security code is never answered by a tool. Pure — unit-tested.
/// </summary>
internal static class XaeDialogClassifier
{
    // Matched case-insensitively against the title and every text of the dialog.
    private static readonly string[] DestructiveText =
    [
        "aktivier", "activate", "activation",
        "neustart", "neu starten", "restart",
        "run-modus", "run mode", "betriebsmodus", "config mode", "konfigurationsmodus",
        "download", "online change", "online-change", "onlinechange", "einloggen", "log in", "login",
        "boot project", "bootprojekt", "boot-projekt",
        "löschen", "delete", "entfernen", "remove",
        "safety", "twinsafe", "sicherheitscode", "security code",
        "lizenz", "license", "licence",
        "überschreiben", "overwrite", "ersetzen", "replace",
    ];

    // A button whose caption contains one of these is never pressed by a tool, whatever the dialog.
    private static readonly string[] DestructiveButton =
    [
        "aktivier", "activate", "neu starten", "restart", "download", "einloggen", "login", "log in",
        "löschen", "delete", "entfernen", "remove", "überschreiben", "overwrite", "ersetzen", "replace",
        "online change", "online-change",
    ];

    private static readonly (string Title, string Text)[] SafeShapes =
    [
        ("", "außerhalb"), ("", "ausserhalb"), ("", "outside the editor"), ("", "outside of the editor"),
        ("", "has been modified outside"), ("gehe zu", ""), ("go to", ""),
    ];

    public static XaeDialogClass Classify(string title, IEnumerable<string> texts)
    {
        var all = texts.Prepend(title).Select(Normalize).Where(t => t.Length > 0).ToList();
        if (all.Any(t => DestructiveText.Any(p => t.Contains(p, StringComparison.Ordinal))))
            return XaeDialogClass.KnownDestructive;

        string normalizedTitle = Normalize(title);
        if (SafeShapes.Any(s =>
                (s.Title.Length == 0 || normalizedTitle.Contains(s.Title, StringComparison.Ordinal))
                && (s.Text.Length == 0 || all.Any(t => t.Contains(s.Text, StringComparison.Ordinal)))))
            return XaeDialogClass.KnownSafe;

        return XaeDialogClass.Unknown;
    }

    public static bool IsDestructiveButton(string caption)
    {
        string c = Normalize(caption);
        return DestructiveButton.Any(p => c.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>Lower case, accelerator '&amp;' removed, whitespace collapsed.</summary>
    internal static string Normalize(string text) =>
        string.Join(' ', (text ?? string.Empty).Replace("&", "", StringComparison.Ordinal)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public static string Name(XaeDialogClass c) => c switch
    {
        XaeDialogClass.KnownSafe => "known-safe",
        XaeDialogClass.KnownDestructive => "known-destructive",
        _ => "unknown",
    };
}
