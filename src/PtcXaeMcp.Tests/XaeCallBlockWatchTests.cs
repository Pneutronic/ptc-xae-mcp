using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>When a running call counts as held by an XAE dialog: grace time, a changing dialog, progress windows.</summary>
public class XaeCallBlockWatchTests
{
    private static readonly BlockingXaeDialog Reload =
        new("0x1A2B", "Microsoft Visual Studio", XaeDialogClass.KnownSafe, ["Ja", "Nein", "Ja, alle", "Nein, alle"]);

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void ADialog_IsReportedOnlyAfterTheGraceTime()
    {
        int details = 0;
        var watch = new XaeCallBlockWatch(() => "Microsoft Visual Studio", () => { details++; return Reload; }, Ms(3000));

        Assert.Null(watch.Tick(Ms(500)));   // first seen
        Assert.Null(watch.Tick(Ms(2000)));
        Assert.Null(watch.Tick(Ms(3400)));  // 2.9 s
        Assert.Equal(0, details);           // the detailed read waits for the grace time
        Assert.Same(Reload, watch.Tick(Ms(3500)));
        Assert.Equal(1, details);
    }

    [Fact]
    public void NoDialog_NeverReports()
    {
        var watch = new XaeCallBlockWatch(() => null, () => Reload, Ms(100));
        for (int t = 0; t < 10_000; t += 500) Assert.Null(watch.Tick(Ms(t)));
    }

    [Fact]
    public void ADialogThatGoesAway_RestartsTheGraceTime()
    {
        string? seen = "A";
        var watch = new XaeCallBlockWatch(() => seen, () => Reload, Ms(3000));
        Assert.Null(watch.Tick(Ms(0)));
        seen = null;
        Assert.Null(watch.Tick(Ms(2500)));
        seen = "A";
        Assert.Null(watch.Tick(Ms(3000)));  // seen again: a new dialog
        Assert.Null(watch.Tick(Ms(5500)));
        Assert.Same(Reload, watch.Tick(Ms(6000)));
    }

    [Fact]
    public void ADifferentDialog_RestartsTheGraceTime()
    {
        string seen = "first";
        var watch = new XaeCallBlockWatch(() => seen, () => Reload, Ms(1000));
        Assert.Null(watch.Tick(Ms(0)));
        seen = "second";
        Assert.Null(watch.Tick(Ms(900)));
        Assert.Null(watch.Tick(Ms(1500)));
        Assert.Same(Reload, watch.Tick(Ms(1900)));
    }

    [Fact]
    public void AProgressWindow_IsNoBlock_AndIsReadAgainOnlyNowAndThen()
    {
        int details = 0;
        BlockingXaeDialog? question = null;
        var watch = new XaeCallBlockWatch(() => "Bitte warten", () => { details++; return question; }, Ms(1000));
        Assert.Null(watch.Tick(Ms(0)));
        Assert.Null(watch.Tick(Ms(1000)));                // read in detail: no question
        Assert.Equal(1, details);
        for (int t = 1500; t < 6000; t += 500) Assert.Null(watch.Tick(Ms(t)));
        Assert.Equal(1, details);                         // not re-read before DetailRecheck
        question = Reload;
        Assert.Same(Reload, watch.Tick(Ms(6000)));        // 5 s later it is read again
        Assert.Equal(2, details);
    }

    [Fact]
    public void ProbesThatThrow_CountAsNoDialog()
    {
        var watch = new XaeCallBlockWatch(() => throw new InvalidOperationException("enum"), () => Reload, Ms(0));
        Assert.Null(watch.Tick(Ms(0)));
        Assert.Null(watch.Tick(Ms(1000)));
        var detailThrows = new XaeCallBlockWatch(() => "A", () => throw new InvalidOperationException("uia"), Ms(0));
        Assert.Null(detailThrows.Tick(Ms(0)));
        Assert.Null(detailThrows.Tick(Ms(10)));
    }

    // ---- which listed dialog is the block -----------------------------------------------------------------

    private static XaeDialogView View(string id, bool blocking, params (string Caption, bool Enabled)[] buttons) =>
        new(id, "T" + id, "#32770", blocking, XaeDialogClass.Unknown, [], buttons);

    [Fact]
    public void Pick_TakesABlockingDialogWithAQuestion()
    {
        var picked = XaeCallBlockWatch.Pick([
            View("0x1", blocking: false, ("OK", true)),
            View("0x2", blocking: true, ("Abbrechen", true)),           // a wait window
            View("0x3", blocking: true, ("Ja", true), ("Nein", true)),
        ]);
        Assert.NotNull(picked);
        Assert.Equal("0x3", picked!.Id);
        Assert.Equal(["Ja", "Nein"], picked.Buttons);
    }

    [Fact]
    public void Pick_PrefersTheDialogOnTop_WhoseButtonsAreEnabled()
    {
        var picked = XaeCallBlockWatch.Pick([
            View("0x1", blocking: true, ("OK", false)),
            View("0x2", blocking: true, ("Ja", true), ("Nein", true)),
        ]);
        Assert.Equal("0x2", picked!.Id);
    }

    [Fact]
    public void Pick_WaitWindowsAndNoButtons_AreNoQuestion()
    {
        Assert.Null(XaeCallBlockWatch.Pick([
            View("0x1", blocking: true),
            View("0x2", blocking: true, ("Cancel", true)),
            View("0x3", blocking: true, ("Abbrechen", true)),
        ]));
        Assert.NotNull(XaeCallBlockWatch.Pick([View("0x4", blocking: true, ("Schließen", true))]));
    }

    [Fact]
    public void Describe_NamesTitleIdClassAndButtons()
    {
        Assert.Equal("'Microsoft Visual Studio' (id 0x1A2B, known-safe; buttons: 'Ja', 'Nein', 'Ja, alle', 'Nein, alle')",
            Reload.Describe());
        Assert.Contains("twincat_xae_dialog_answer", Reload.HowToAnswer());
        var destructive = Reload with { Class = XaeDialogClass.KnownDestructive };
        Assert.StartsWith("a person answers it in the XAE", destructive.HowToAnswer());
    }
}
