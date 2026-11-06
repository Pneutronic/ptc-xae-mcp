using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>Which XAE dialogs a tool may answer, and how they are listed.</summary>
public class XaeDialogInspectorTests
{
    [Theory]
    [InlineData("TwinCAT XAE", "Konfiguration aktivieren?")]
    [InlineData("Microsoft Visual Studio", "Restart TwinCAT System in Run Mode")]
    [InlineData("TwinCAT", "Die Anwendung existiert nicht auf der Steuerung. Möchten Sie einloggen und herunterladen (Download)?")]
    [InlineData("Online Change", "Do you want to perform an online change?")]
    [InlineData("Sicherheitscode eingeben", "")]
    [InlineData("TwinSAFE", "Ändern der Safety-Konfiguration")]
    [InlineData("Löschen", "Sollen die ausgewählten Objekte gelöscht werden?")]
    public void TargetDeleteSafetyAndLicenseDialogs_AreDestructive(string title, string text) =>
        Assert.Equal(XaeDialogClass.KnownDestructive, XaeDialogClassifier.Classify(title, [text]));

    [Theory]
    [InlineData("Microsoft Visual Studio", "Die Datei wurde außerhalb des Editors geändert. Möchten Sie sie neu laden?")]
    [InlineData("TcXaeShell", "This file has been modified outside of the source editor. Do you want to reload it?")]
    [InlineData("Gehe zu Zeile", "Zeilennummer (1 - 120):")]
    public void FileChangedOutsideAndGoTo_AreKnownSafe(string title, string text) =>
        Assert.Equal(XaeDialogClass.KnownSafe, XaeDialogClassifier.Classify(title, [text]));

    [Fact]
    public void AnythingElse_IsUnknown() =>
        Assert.Equal(XaeDialogClass.Unknown, XaeDialogClassifier.Classify("Hinweis", ["Der Vorgang ist abgeschlossen."]));

    [Theory]
    [InlineData("&Aktivieren", true)]
    [InlineData("Neu starten", true)]
    [InlineData("Löschen", true)]
    [InlineData("&Ja", false)]
    [InlineData("Neu laden", false)]
    [InlineData("OK", false)]
    public void DestructiveButtons_AreRecognisedWithMnemonics(string caption, bool destructive) =>
        Assert.Equal(destructive, XaeDialogClassifier.IsDestructiveButton(caption));

    private static XaeDialog Dialog(long hwnd, long owner, string cls, string title, params (string Cls, string Text)[] controls) =>
        new((IntPtr)hwnd, 42, (IntPtr)owner, cls, title,
            controls.Select((c, i) => new XaeControl((IntPtr)(hwnd * 100 + i), c.Cls, c.Text)).ToList());

    [Fact]
    public void View_ListsTextsAndButtons_ModalFirst_MainWindowLeftOut()
    {
        var windows = new[]
        {
            Dialog(1, 0, "HwndWrapper", "PtcXaeMcpFixture - TcXaeShell"),
            Dialog(2, 1, "WindowsForms10.Window", "Tool window", ("Button", "Close")),
            Dialog(3, 1, "#32770", "Microsoft Visual Studio",
                ("Static", "Die Datei wurde außerhalb\r\ndes Editors geändert."), ("Button", "&Ja"), ("Button", "&Nein")),
        };

        // Owner 1 (the main window) is disabled while the message box is up.
        var views = XaeDialogInspector.View(windows, owner => owner != (IntPtr)1);

        Assert.Equal(2, views.Count);
        var modal = views.Single(v => v.Id == "0x3");
        Assert.True(modal.Blocking);
        Assert.Equal(XaeDialogClass.KnownSafe, modal.Class);
        Assert.Equal(["Die Datei wurde außerhalb des Editors geändert."], modal.Texts);
        Assert.Equal(["Ja", "Nein"], modal.Buttons.Select(b => b.Caption));

        string text = XaeDialogInspector.Describe(views);
        Assert.StartsWith("2 XAE dialog(s) open, 2 blocking the XAE.", text);
        Assert.Contains("buttons: 'Ja', 'Nein'", text);
    }

    [Fact]
    public void Refusal_KeepsToolsAwayFromDestructiveDialogsAndButtons()
    {
        var destructive = new XaeDialogView("0x1", "Aktivieren", "#32770", true, XaeDialogClass.KnownDestructive, [], [("OK", true)]);
        Assert.Contains("A person answers it", XaeDialogInspector.Refusal(destructive, "OK"));

        var unknown = new XaeDialogView("0x2", "Frage", "#32770", true, XaeDialogClass.Unknown, [], [("Löschen", true), ("Abbrechen", true)]);
        Assert.Contains("destructive answer", XaeDialogInspector.Refusal(unknown, "Löschen"));
        Assert.Null(XaeDialogInspector.Refusal(unknown, "Abbrechen"));
    }

    [Fact]
    public void NoDialogs_SaysSo() => Assert.Equal("No XAE dialog is open.", XaeDialogInspector.Describe([]));
}
