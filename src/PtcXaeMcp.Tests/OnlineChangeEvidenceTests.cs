using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// A login answered with "online change" is only proven by the PLC's own OnlineChangeCnt moving by one;
/// these pin the verdicts the login reports from the two readings.
/// </summary>
public class OnlineChangeEvidenceTests
{
    private const string OnlineChangeAnswered = "app-changed-modal (online_change)";

    [Fact]
    public void CounterUpByOne_IsApplied()
    {
        var r = OnlineChangeEvidence.Evaluate(OnlineChangeAnswered, 4, 5, cycling: true);

        Assert.Equal("applied", r.Verdict);
        Assert.Contains("OnlineChangeCnt 4 → 5", r.Message);
        Assert.Contains("cycling", r.Message);
    }

    [Fact]
    public void ThreeWayLoginDialogAnswer_CountsAsOnlineChangeToo()
    {
        var r = OnlineChangeEvidence.Evaluate("login-dialog (OnlineChange)", 2, 2, cycling: null);

        Assert.Equal("not_applied", r.Verdict);
    }

    [Fact]
    public void CounterUnchangedAfterOnlineChangeAnswer_IsNotApplied()
    {
        var r = OnlineChangeEvidence.Evaluate(OnlineChangeAnswered, 7, 7, cycling: true);

        Assert.Equal("not_applied", r.Verdict);
        Assert.StartsWith("[Error]", r.Message);
    }

    [Fact]
    public void CounterUnchangedWithoutModal_IsNotNeeded()
    {
        var r = OnlineChangeEvidence.Evaluate("none", 7, 7, cycling: true);

        Assert.Equal("not_needed", r.Verdict);
    }

    [Fact]
    public void DownloadFallback_IsDownloadWhateverTheCounter()
    {
        var r = OnlineChangeEvidence.Evaluate("app-changed-modal (download)", 7, 0, cycling: false);

        Assert.Equal("download", r.Verdict);
        Assert.Contains("[Warn]", r.Message);
    }

    [Theory]
    [InlineData(null, 3UL)]
    [InlineData(3UL, null)]
    public void UnreadableCounter_IsUnknown(ulong? before, ulong? after)
    {
        var r = OnlineChangeEvidence.Evaluate(OnlineChangeAnswered, before, after, cycling: null);

        Assert.Equal("unknown", r.Verdict);
        Assert.Contains(OnlineChangeEvidence.CounterSymbol, r.Message);
    }

    [Fact]
    public void CounterJumpingByMoreThanOne_IsUnknownWithWarning()
    {
        var r = OnlineChangeEvidence.Evaluate(OnlineChangeAnswered, 3, 6, cycling: true);

        Assert.Equal("unknown", r.Verdict);
        Assert.Contains("[Warn] Unexpected counter step", r.Message);
    }

    [Fact]
    public void Capture_WaitsForTheCounterAndSamplesTheCycle()
    {
        int counterReads = 0, cycleReads = 0;
        var fake = new FakeTwinCatSession
        {
            ReadAdsVariableBySymbol = sym => sym switch
            {
                OnlineChangeEvidence.CounterSymbol => Ok(sym, "UDINT", ++counterReads < 3 ? 10u : 11u),
                OnlineChangeEvidence.CycleSymbol => Ok(sym, "UDINT", (uint)(1000 + ++cycleReads)),
                _ => null,
            },
        };

        var r = OnlineChangeEvidence.Capture(fake, 851, OnlineChangeAnswered, before: 10);

        Assert.Equal("applied", r.Verdict);
        Assert.Equal(11UL, r.CountAfter);
        Assert.True(r.PlcCycling);
        Assert.Contains($"ReadAdsVariable(sym={OnlineChangeEvidence.CounterSymbol}, net=project, port=851)", fake.CallLog);
    }

    [Fact]
    public void Capture_FailedRead_IsUnknownNotAnException()
    {
        var fake = new FakeTwinCatSession
        {
            FakeReadAdsVariableResult = new AdsVariableResult(false, "x", null, null, "1.2.3.4.1.1", 851, "[Error] no route"),
        };

        var r = OnlineChangeEvidence.Capture(fake, 851, "none", before: null);

        Assert.Equal("unknown", r.Verdict);
        Assert.Null(r.PlcCycling);
    }

    private static AdsVariableResult Ok(string sym, string type, object value) =>
        new(true, sym, type, value, "1.2.3.4.1.1", 851, "OK");
}
