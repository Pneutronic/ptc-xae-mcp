using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// twincat_umr_start must not start an instance that already runs, also when another process started it: the
/// instance's NetId is probed on the system service, any answer refuses the start.
/// </summary>
public class UmrStartAlreadyRunningTests
{
    private sealed class Probe(TargetStateReading reading) : ISystemControl
    {
        public List<string> Writes { get; } = [];
        public bool Disposed { get; private set; }
        public TargetStateReading ReadState() => reading;
        public string? WriteControl(string adsState) { Writes.Add(adsState); return null; }
        public void Dispose() => Disposed = true;
    }

    [Theory]
    [InlineData("Config")]
    [InlineData("Run")]
    [InlineData("Stop")]
    public void NetIdAnswers_StartIsRefused(string state)
    {
        var probe = new Probe(TargetStateReading.Of(state));
        string? opened = null;

        string? refusal = UmrProcessManager.AlreadyRunningRefusal("UmRT_Test", "5.10.20.30.1.1", id => { opened = id; return probe; });

        Assert.NotNull(refusal);
        Assert.Contains("'UmRT_Test' is already running", refusal);
        Assert.Contains("5.10.20.30.1.1", refusal);
        Assert.Contains(state, refusal);
        Assert.Equal("5.10.20.30.1.1", opened);
        Assert.Empty(probe.Writes); // only read, never switched
        Assert.True(probe.Disposed);
    }

    [Fact]
    public void NetIdDoesNotAnswer_StartGoesAhead()
    {
        var probe = new Probe(TargetStateReading.Unreachable("ADS error 7 (target machine not found)"));
        Assert.Null(UmrProcessManager.AlreadyRunningRefusal("UmRT_Test", "5.10.20.30.1.1", _ => probe));
        Assert.True(probe.Disposed);
    }

    [Fact]
    public void NetIdUnknown_NothingIsProbed()
    {
        bool opened = false;
        Assert.Null(UmrProcessManager.AlreadyRunningRefusal("UmRT_Default", null, _ => { opened = true; return new Probe(TargetStateReading.Of("Run")); }));
        Assert.False(opened);
    }

    [Fact]
    public void ProbeThrows_StartGoesAhead() =>
        Assert.Null(UmrProcessManager.AlreadyRunningRefusal("UmRT_Test", "5.10.20.30.1.1", _ => throw new InvalidOperationException("router down")));
}
