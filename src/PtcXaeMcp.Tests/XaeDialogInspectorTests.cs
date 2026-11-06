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

    private static PromptWindow Window(long hwnd, long owner, string cls, string title, string[] texts, params string[] buttons) =>
        new((IntPtr)hwnd, 42, (IntPtr)owner, cls, title, texts, buttons.Select(b => new PromptButton(b)).ToList());

    [Fact]
    public void View_ListsTextsAndButtons_MainWindowLeftOut()
    {
        var windows = new[]
        {
            Window(1, 0, "HwndWrapper[DefaultDomain;;x]", "PtcXaeMcpFixture - TcXaeShell", []),
            Window(3, 1, "#32770", "Microsoft Visual Studio", ["Die Datei wurde außerhalb\r\ndes Editors geändert."], "&Ja", "&Nein"),
        };

        // Owner 1 (the main window) is disabled while the message box is up.
        var views = XaeDialogInspector.View(windows, hwnd => hwnd != (IntPtr)1);

        var modal = Assert.Single(views);
        Assert.Equal("0x3", modal.Id);
        Assert.True(modal.Blocking);
        Assert.Equal(XaeDialogClass.KnownSafe, modal.Class);
        Assert.Equal(["Die Datei wurde außerhalb des Editors geändert."], modal.Texts);
        Assert.Equal(["Ja", "Nein"], modal.Buttons.Select(b => b.Caption));

        string text = XaeDialogInspector.Describe(views);
        Assert.StartsWith("1 XAE dialog(s) open, 1 blocking the XAE.", text);
        Assert.Contains("buttons: 'Ja', 'Nein'", text);
    }

    [Fact]
    public void View_AWpfSavePrompt_ShowsItsQuestionAndButtons_NotTheButtonTexts()
    {
        // As read live through UI Automation (shell "save changes?" prompt over a stuck Go To dialog).
        var wpf = Window(0x320CEA, 1, "HwndWrapper[DefaultDomain;;x]", "TcXaeShell",
            ["Sollen die Änderungen an den folgenden Elementen gespeichert werden?", "Zu speichernde Dateien", "FB_StepChain*",
             "_Speichern", "_Nicht speichern", "Abbrechen"],
            "Speichern", "Nicht speichern", "Abbrechen");

        var view = Assert.Single(XaeDialogInspector.View([wpf], hwnd => hwnd != (IntPtr)1));

        Assert.True(view.Blocking);
        Assert.All(view.Buttons, b => Assert.True(b.Enabled));
        Assert.Equal(XaeDialogClass.Unknown, view.Class);
        Assert.Equal(["Sollen die Änderungen an den folgenden Elementen gespeichert werden?", "Zu speichernde Dateien", "FB_StepChain*"], view.Texts);
        Assert.Equal(["Speichern", "Nicht speichern", "Abbrechen"], view.Buttons.Select(b => b.Caption));
        Assert.Null(XaeDialogInspector.Refusal(view, "Nicht speichern"));
    }

    [Fact]
    public void ADialogUnderAnother_ShowsItsButtonsDisabled()
    {
        // The Go To dialog (0x7A) is disabled while the save prompt sits on top of it, as seen live.
        var goTo = Window(0x7A, 1, "WindowsForms10.Window.8.app.0", "Gehe zu Zeile", ["&Zeilennummer"], "Abbrechen", "OK");
        var view = Assert.Single(XaeDialogInspector.View([goTo], hwnd => hwnd != (IntPtr)1 && hwnd != (IntPtr)0x7A));
        Assert.All(view.Buttons, b => Assert.False(b.Enabled));
        Assert.Contains("'OK' (disabled)", XaeDialogInspector.Describe([view]));
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
