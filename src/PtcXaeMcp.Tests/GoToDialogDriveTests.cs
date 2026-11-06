using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Issue #150 LIVE-5: one DTE.ExecuteCommand("Edit.GoTo") shows the Go-To dialog TWICE on
/// TcXaeShell 4026.22 (the second opens ~40 ms after the first closes, pre-filled with the new
/// line; the command returns only after it closes). The driver must answer every dialog while
/// the command runs, succeed only when none is left AND the command returned, and otherwise
/// cancel what is still open and fail — never report success while XAE is modal.
/// </summary>
public class GoToDialogDriveTests
{
    private static readonly IntPtr Main = (IntPtr)0x30236;

    private static readonly GoToDialogAutomation.Timings Command = new(
        Appear: TimeSpan.FromSeconds(2), Close: TimeSpan.FromMilliseconds(300),
        Reopen: TimeSpan.FromSeconds(2), CommandReturn: TimeSpan.FromMilliseconds(500));

    private static readonly GoToDialogAutomation.Timings Hotkey = Command with { Reopen = TimeSpan.FromMilliseconds(300) };

    /// <summary>
    /// Simulated XAE: shows <see cref="Dialogs"/> Go-To dialogs one after another (each opening
    /// <see cref="ReopenDelay"/> after the previous closed); the command "returns" once all are done.
    /// </summary>
    private sealed class FakeXae : IGoToDialogWin32
    {
        private readonly object _gate = new();
        private GoToDialogAutomation.DialogHandles? _open;
        private DateTime _nextOpenAt = DateTime.UtcNow;
        private int _nextHwnd = 0x5000;
        private string _editText = "";

        public int Dialogs { get; init; } = 1;
        public bool ClosesOnOk { get; init; } = true;
        public bool ClosesOnWmClose { get; init; } = true;
        public TimeSpan ReopenDelay { get; init; } = TimeSpan.FromMilliseconds(40);
        public bool CommandHangs { get; init; }
        /// <summary>Error boxes XAE raises (one after another) when OK is pressed — the out-of-range-line chain.</summary>
        public Queue<(string Text, bool Dismissable)> ErrorChain { get; init; } = new();
        public List<string> DismissedBoxes { get; } = new();
        private GoToDialogAutomation.ErrorBox? _box;

        public int Shown { get; private set; }
        public int OkClicks { get; private set; }
        public int WmCloses { get; private set; }
        public int CurrentLine { get; private set; } = 3;
        public List<string> Prefills { get; } = new();

        public bool CommandDone
        {
            get { lock (_gate) return !CommandHangs && _open is null && _box is null && ErrorChain.Count == 0 && StrayDialogs.Count == 0 && Shown >= Dialogs; }
        }

        public GoToDialogAutomation.ErrorBox? FindErrorBox(IntPtr xaeMain) { lock (_gate) return _box; }

        private void RaiseNextBox()
        {
            if (ErrorChain.Count == 0) { _box = null; return; }
            var (text, ok) = ErrorChain.Dequeue();
            int h = _nextHwnd += 0x10;
            _box = new GoToDialogAutomation.ErrorBox((IntPtr)h, text, ok ? (IntPtr)(h + 1) : IntPtr.Zero);
        }

        public GoToDialogAutomation.DialogHandles? FindDialog(IntPtr xaeMain)
        {
            Assert.Equal(Main, xaeMain);
            lock (_gate)
            {
                if (_open is null && Shown < Dialogs && DateTime.UtcNow >= _nextOpenAt)
                {
                    Shown++;
                    int h = _nextHwnd += 0x10;
                    _open = new GoToDialogAutomation.DialogHandles((IntPtr)h, "Gehe zu Zeile", (IntPtr)(h + 1), (IntPtr)(h + 2));
                    _editText = CurrentLine.ToString(System.Globalization.CultureInfo.InvariantCulture); // XAE pre-fills the current line
                    Prefills.Add(_editText);
                }
                return _open;
            }
        }

        public bool IsOpen(IntPtr hwnd) { lock (_gate) return _open?.Hwnd == hwnd || _box?.Hwnd == hwnd; }
        public bool IsReady(GoToDialogAutomation.DialogHandles dialog) => true;
        public void SetText(IntPtr edit, string text) { lock (_gate) _editText = text; }
        public string ReadText(IntPtr edit) { lock (_gate) return _editText; }

        public void Click(IntPtr button)
        {
            lock (_gate)
            {
                if (_box is not null && button == _box.DismissButton)
                {
                    DismissedBoxes.Add(_box.Text);
                    RaiseNextBox();
                    return;
                }
                OkClicks++;
                if (_open is not null && ErrorChain.Count > 0) { RaiseNextBox(); return; } // the dialog stays, disabled
                if (_open is null || !ClosesOnOk) return;
                CurrentLine = int.Parse(_editText, System.Globalization.CultureInfo.InvariantCulture);
                CloseOpen();
            }
        }

        public void CommandOk(IntPtr dialog, IntPtr okButton) => Click(okButton);

        /// <summary>Other dialogs that block the XAE (e.g. "Variable deklarieren"); the command waits while any is open.</summary>
        public Queue<string> StrayDialogs { get; init; } = new();
        public List<string> CancelledStrays { get; } = new();

        public string? CancelStrayDialog(IntPtr xaeMain)
        {
            lock (_gate)
            {
                if (StrayDialogs.Count == 0) return null;
                string title = StrayDialogs.Dequeue();
                CancelledStrays.Add(title);
                return title;
            }
        }

        public void Close(IntPtr dialog)
        {
            lock (_gate)
            {
                WmCloses++;
                if (_open is not null && ClosesOnWmClose) { CloseOpen(); Shown = Dialogs; } // cancel ends the command
            }
        }

        private void CloseOpen() { _open = null; _nextOpenAt = DateTime.UtcNow + ReopenDelay; }
    }

    // Seen live: the first Go-To closed on OK, then "Variable deklarieren" blocked the command; the second Go-To
    // dialog it re-opened later and the declare dialog were left behind and blocked every later call.
    [Fact]
    public void AFailedDrive_CancelsTheStrayDialogAndTheReopenedGoTo_SoTheXaeIsFree()
    {
        var xae = new FakeXae { Dialogs = 2, ReopenDelay = TimeSpan.FromMilliseconds(400), StrayDialogs = new(["Variable deklarieren"]) };

        var r = GoToDialogAutomation.Drive(xae, Main, 5, Command with { Reopen = TimeSpan.FromMilliseconds(100) }, () => xae.CommandDone);

        Assert.False(r.Closed);
        Assert.Equal(["Variable deklarieren"], xae.CancelledStrays);
        Assert.Null(xae.FindDialog(Main));
        Assert.True(xae.CommandDone, "the XAE is still modal after the failed drive");
        Assert.Contains("afterwards cancelled to leave the XAE free: 'Variable deklarieren'", r.Note);
    }

    [Fact]
    public void ASuccessfulDrive_SweepsNothing()
    {
        var xae = new FakeXae { Dialogs = 2 };
        var r = GoToDialogAutomation.Drive(xae, Main, 5, Command, () => xae.CommandDone);
        Assert.True(r.Closed);
        Assert.Equal(0, xae.WmCloses);
        Assert.DoesNotContain("afterwards cancelled", r.Note);
    }

    [Fact]
    public void SingleDialog_ClosedOnOk_CommandReturned_Succeeds()
    {
        var xae = new FakeXae { Dialogs = 1 };
        var r = GoToDialogAutomation.Drive(xae, Main, 12, Command, () => xae.CommandDone);
        Assert.True(r.DialogSeen);
        Assert.True(r.Closed, r.Note);
        Assert.Equal(1, r.DialogsAnswered);
        Assert.Equal(12, xae.CurrentLine);
        Assert.Equal(0, xae.WmCloses);
    }

    [Fact]
    public void CommandReopensTheDialog_BothAnswered_NoneLeftOpen_Succeeds()
    {
        // The live LIVE-5 shape: the old driver returned after the first close.
        var xae = new FakeXae { Dialogs = 2 };
        var r = GoToDialogAutomation.Drive(xae, Main, 5, Command, () => xae.CommandDone);
        Assert.True(r.Closed, r.Note);
        Assert.Equal(2, r.DialogsAnswered);
        Assert.Equal(2, xae.Shown);
        Assert.Null(xae.FindDialog(Main));
        Assert.Equal(5, xae.CurrentLine);
        Assert.Equal("5", xae.Prefills[1]); // the second dialog proves the cursor moved
        Assert.Contains("re-opened by XAE pre-filled with line 5", r.Note);
    }

    [Fact]
    public void CommandReopensSlowly_StillWaitsForTheCommand()
    {
        var xae = new FakeXae { Dialogs = 2, ReopenDelay = TimeSpan.FromMilliseconds(400) };
        var r = GoToDialogAutomation.Drive(xae, Main, 7, Command, () => xae.CommandDone);
        Assert.True(r.Closed, r.Note);
        Assert.Equal(2, r.DialogsAnswered);
        Assert.Null(xae.FindDialog(Main));
    }

    [Fact]
    public void OkNeverCloses_DialogIsCancelled_AndFailureReported()
    {
        var xae = new FakeXae { Dialogs = 1, ClosesOnOk = false };
        var r = GoToDialogAutomation.Drive(xae, Main, 4, Command, () => xae.CommandDone);
        Assert.True(r.DialogSeen);
        Assert.False(r.Closed);
        Assert.Equal(1, xae.WmCloses);
        Assert.Null(xae.FindDialog(Main));
        Assert.Contains("cancelled (WM_CLOSE)", r.Note);
    }

    [Fact]
    public void DialogKeepsReopening_IsCappedCancelledAndFails()
    {
        var xae = new FakeXae { Dialogs = 50 };
        var r = GoToDialogAutomation.Drive(xae, Main, 9, Command, () => xae.CommandDone);
        Assert.False(r.Closed);
        Assert.Equal(GoToDialogAutomation.MaxDialogs, r.DialogsAnswered);
        Assert.True(xae.WmCloses >= 1);
        Assert.Contains("kept re-opening", r.Note);
    }

    [Fact]
    public void CommandNeverReturns_IsNotASuccess()
    {
        var xae = new FakeXae { Dialogs = 1, CommandHangs = true };
        var r = GoToDialogAutomation.Drive(xae, Main, 2,
            Command with { Reopen = TimeSpan.FromMilliseconds(300) }, () => xae.CommandDone);
        Assert.False(r.Closed);
        Assert.Contains("did not return", r.Note);
    }

    [Fact]
    public void XaeErrorChain_SafeBoxesDismissed_GoToCancelled_FailureNamesTheError()
    {
        // Live 4026.22 shape for a line beyond the pane: error box (OK), .NET assertion
        // (Abbrechen/Wiederholen/Ignorieren → only "Ignorieren" is safe), then another OK box.
        var xae = new FakeXae
        {
            Dialogs = 1,
            ErrorChain = new Queue<(string, bool)>(new[]
            {
                ("Fehler beim Ausführen des Batch-Befehls \"text gotoline\"", true),
                ("Assertionsfehler", true),
                ("Der Wert darf nicht NULL sein. Parametername: synEdControl", true),
            }),
        };
        var r = GoToDialogAutomation.Drive(xae, Main, 99, Command, () => xae.CommandDone);
        Assert.False(r.Closed);
        Assert.Equal(3, xae.DismissedBoxes.Count);
        Assert.Null(xae.FindErrorBox(Main));
        Assert.Null(xae.FindDialog(Main));
        Assert.Contains("XAE reported an error", r.Note);
        Assert.Contains("text gotoline", r.Note);
    }

    [Fact]
    public void XaeQuestionBox_IsNeverAnswered_ButReported()
    {
        var xae = new FakeXae { Dialogs = 1, ErrorChain = new Queue<(string, bool)>(new[] { ("Speichern? Ja/Nein", false) }) };
        var r = GoToDialogAutomation.Drive(xae, Main, 4, Command, () => xae.CommandDone);
        Assert.False(r.Closed);
        Assert.Empty(xae.DismissedBoxes);
        Assert.Contains("Speichern?", r.Note);
    }

    [Fact]
    public void PickErrorBoxButton_OnlyOkOrIgnore()
    {
        static XaeDialog Box(params string[] buttons) => new((IntPtr)1, 8768, (IntPtr)2, "#32770", "TcXaeShell",
            buttons.Select((b, i) => new XaeControl((IntPtr)(10 + i), "Button", b)).Prepend(new XaeControl((IntPtr)9, "Static", "Text")).ToList());

        Assert.Equal((IntPtr)10, GoToDialogAutomation.PickErrorBoxButton(Box("OK")));
        Assert.Equal((IntPtr)12, GoToDialogAutomation.PickErrorBoxButton(Box("A&bbrechen", "Wieder&holen", "&Ignorieren")));
        Assert.Equal(IntPtr.Zero, GoToDialogAutomation.PickErrorBoxButton(Box("&Ja", "&Nein")));
        Assert.Equal(IntPtr.Zero, GoToDialogAutomation.PickErrorBoxButton(Box("OK", "Abbrechen")));
    }

    [Theory]
    [InlineData("a", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\r\nb\r\n", 3)]
    [InlineData("", 1)]
    public void PaneLineCount(string text, int expected) => Assert.Equal(expected, EditorPaneAutomation.LineCount(text));

    [Fact]
    public void Hotkey_SingleDialog_Succeeds()
    {
        var xae = new FakeXae { Dialogs = 1 };
        var r = GoToDialogAutomation.Drive(xae, Main, 8, Hotkey, commandDone: null);
        Assert.True(r.Closed, r.Note);
        Assert.Equal(1, r.DialogsAnswered);
    }

    [Fact]
    public void NoDialog_IsReportedAsNotSeen()
    {
        var xae = new FakeXae { Dialogs = 0 };
        var r = GoToDialogAutomation.Drive(xae, Main, 8,
            Command with { Appear = TimeSpan.FromMilliseconds(200) }, () => xae.CommandDone);
        Assert.False(r.DialogSeen);
        Assert.False(r.Closed);
    }
}
