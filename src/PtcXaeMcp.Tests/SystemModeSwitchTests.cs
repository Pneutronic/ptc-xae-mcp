using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>Issue #203: switching a target's TwinCAT system to Config/Run over ADS, decided by ReadState only.</summary>
public class SystemModeSwitchTests
{
    private sealed class FakeControl(params TargetStateReading[] states) : ISystemControl
    {
        private readonly Queue<TargetStateReading> _states = new(states);
        private TargetStateReading _last = states.Length > 0 ? states[^1] : TargetStateReading.Unreachable("none");
        public List<string> Writes { get; } = [];
        public string? WriteError { get; set; }

        /// <summary>The PLC port's answers in order (the last one repeats); null leaves the control unable to ask.</summary>
        public Queue<TargetStateReading>? PlcStates { get; set; }
        private TargetStateReading? _lastPlc;
        public List<int> PlcPortsAsked { get; } = [];

        public TargetStateReading? ReadPlcState(int amsPort)
        {
            if (PlcStates is null) return null;
            PlcPortsAsked.Add(amsPort);
            return PlcStates.Count > 0 ? (_lastPlc = PlcStates.Dequeue()) : _lastPlc;
        }

        public TargetStateReading ReadState() => _states.Count > 0 ? (_last = _states.Dequeue()) : _last;

        public string? WriteControl(string adsState)
        {
            Writes.Add(adsState);
            return WriteError;
        }

        public void Dispose() { }
    }

    private static TimeSpan _t;
    private static string Run(FakeControl c, SystemMode mode, int timeoutS = 10)
    {
        _t = TimeSpan.Zero;
        return SystemModeSwitch.Switch(c, "192.168.250.20.1.1", mode, TimeSpan.FromSeconds(timeoutS),
            elapsed: () => _t, sleep: d => _t += d);
    }

    [Fact]
    public void RunToConfig_SendsReconfig_AndWaitsForConfig()
    {
        var c = new FakeControl(TargetStateReading.Of("Run"), TargetStateReading.Of("Run"),
            TargetStateReading.Unreachable("restarting"), TargetStateReading.Of("Config"));

        var r = Run(c, SystemMode.Config);

        Assert.Equal(["Reconfig"], c.Writes);
        Assert.Contains("switched from Run to Config", r, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigToRun_SendsReset()
    {
        var c = new FakeControl(TargetStateReading.Of("Config"), TargetStateReading.Of("Run"));

        var r = Run(c, SystemMode.Run);

        Assert.Equal(["Reset"], c.Writes);
        Assert.False(r.StartsWith("[Error]", StringComparison.Ordinal), r);
    }

    [Fact]
    public void Run_WhenThePlcReadsRun_SaysSo()
    {
        var c = new FakeControl(TargetStateReading.Of("Config"), TargetStateReading.Of("Run"))
        { PlcStates = new([TargetStateReading.Of("Run")]) };

        var r = Run(c, SystemMode.Run);

        Assert.Equal([851], c.PlcPortsAsked);
        Assert.EndsWith("The PLC runtime on port 851 reads Run.", r, StringComparison.Ordinal);
        Assert.DoesNotContain("[Warn]", r, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_WhenThePlcStartsWithinTheGrace_DoesNotWarn()
    {
        var c = new FakeControl(TargetStateReading.Of("Config"), TargetStateReading.Of("Run"))
        { PlcStates = new([TargetStateReading.Of("Invalid"), TargetStateReading.Of("Invalid"), TargetStateReading.Of("Run")]) };

        var r = Run(c, SystemMode.Run);

        Assert.Equal(3, c.PlcPortsAsked.Count);
        Assert.Contains("reads Run.", r, StringComparison.Ordinal);
        Assert.DoesNotContain("[Warn]", r, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_WhenThePlcStaysStopped_StillSucceedsButWarns()
    {
        var c = new FakeControl(TargetStateReading.Of("Config"), TargetStateReading.Of("Run"))
        { PlcStates = new([TargetStateReading.Unreachable("ADS error DeviceSymbolNotFound")]) };

        var r = Run(c, SystemMode.Run);

        Assert.False(r.StartsWith("[Error]", StringComparison.Ordinal), r);
        Assert.Contains("switched from Config to Run", r, StringComparison.Ordinal);
        Assert.Contains("[Warn] The system reads Run, but the PLC runtime on port 851", r, StringComparison.Ordinal);
        Assert.Contains("DeviceSymbolNotFound", r, StringComparison.Ordinal);
        Assert.Contains("Autostart Boot Project", r, StringComparison.Ordinal);
        // The grace is bounded by the injected clock: it polled, it did not spin forever.
        Assert.InRange(c.PlcPortsAsked.Count, 2, 100);
    }

    [Fact]
    public void Config_NeverAsksThePlc()
    {
        var c = new FakeControl(TargetStateReading.Of("Run"), TargetStateReading.Of("Config"))
        { PlcStates = new([TargetStateReading.Of("Run")]) };

        Run(c, SystemMode.Config);

        Assert.Empty(c.PlcPortsAsked);
    }

    [Fact]
    public void Run_WithAControlThatCannotAskThePlc_AddsNothing()
    {
        var c = new FakeControl(TargetStateReading.Of("Config"), TargetStateReading.Of("Run"));

        var r = Run(c, SystemMode.Run);

        Assert.DoesNotContain("PLC", r, StringComparison.Ordinal);
    }

    [Fact]
    public void AlreadyInMode_SendsNothing()
    {
        var c = new FakeControl(TargetStateReading.Of("Run"));

        var r = Run(c, SystemMode.Run);

        Assert.Empty(c.Writes);
        Assert.Contains("already in Run", r, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreachableTarget_SendsNothing_AndIsAnError()
    {
        var c = new FakeControl(TargetStateReading.Unreachable("ClientSyncTimeOut"));

        var r = Run(c, SystemMode.Config);

        Assert.Empty(c.Writes);
        Assert.StartsWith("[Error]", r);
        Assert.Contains("Nothing was sent", r, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusedWrite_IsAnError()
    {
        var c = new FakeControl(TargetStateReading.Of("Run")) { WriteError = "ADS error DeviceAccessDenied" };

        var r = Run(c, SystemMode.Config);

        Assert.StartsWith("[Error]", r);
        Assert.Contains("refused", r, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedButIgnored_TimesOut_WithTheLastState()
    {
        var c = new FakeControl(TargetStateReading.Of("Run"));

        var r = Run(c, SystemMode.Config, timeoutS: 5);

        Assert.Equal(["Reconfig"], c.Writes);
        Assert.StartsWith("[Error]", r);
        Assert.Contains("last: state Run", r, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("config", true)]
    [InlineData(" RUN ", true)]
    [InlineData("stop", false)]
    [InlineData(null, false)]
    public void ParsesMode(string? s, bool ok) => Assert.Equal(ok, SystemModeSwitch.TryParseMode(s, out _));
}
