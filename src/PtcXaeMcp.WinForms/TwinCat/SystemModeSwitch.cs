using System.Diagnostics;
#if TWINCAT
using TwinCAT.Ads;
#endif

namespace PtcXaeMcp.WinForms.TwinCat;

internal enum SystemMode { Config, Run }

/// <summary>ADS access to a target's TwinCAT system service (port 10000). Never throws.</summary>
internal interface ISystemControl : IDisposable
{
    TargetStateReading ReadState();

    /// <summary>Sends WriteControl with the given ADS state name ("Reconfig" or "Reset"); null on success, else the error.</summary>
    string? WriteControl(string adsState);

    /// <summary>ReadState of the PLC runtime on <paramref name="amsPort"/>; null when this control cannot ask.</summary>
    TargetStateReading? ReadPlcState(int amsPort) => null;
}

/// <summary>
/// Issue #203: on a runtime that already runs, StartRestartTwinCAT() is ignored, and twincat_umr_set_mode only
/// reaches a UMR the host started itself. The XAE's own "Restart TwinCAT (Config Mode)" / "Restart TwinCAT System"
/// are ADS WriteControl calls on port 10000: Reconfig → Config, Reset → Run. Success is decided by ReadState only.
/// </summary>
internal static class SystemModeSwitch
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>First TwinCAT 3 PLC runtime (AMS port 851).</summary>
    internal const int FirstPlcPort = 851;

    /// <summary>How long the PLC runtime gets after the system reads Run to report Run too.</summary>
    internal static readonly TimeSpan PlcStartGrace = TimeSpan.FromSeconds(15);

    public static string ControlFor(SystemMode mode) => mode == SystemMode.Config ? "Reconfig" : "Reset";

    public static bool TryParseMode(string? s, out SystemMode mode)
    {
        mode = SystemMode.Run;
        switch (s?.Trim().ToLowerInvariant())
        {
            case "config": mode = SystemMode.Config; return true;
            case "run": mode = SystemMode.Run; return true;
            default: return false;
        }
    }

    public static string Switch(ISystemControl control, string netId, SystemMode mode, TimeSpan timeout,
        Func<TimeSpan>? elapsed = null, Action<TimeSpan>? sleep = null)
    {
        var sw = Stopwatch.StartNew();
        elapsed ??= () => sw.Elapsed;
        sleep ??= Thread.Sleep;
        string want = mode.ToString();

        var before = control.ReadState();
        if (before.Error is not null)
            return $"[Error] The TwinCAT system of {netId} does not answer over ADS (port 10000): {before.Error}. Nothing was sent.";
        if (string.Equals(before.State, want, StringComparison.OrdinalIgnoreCase))
            return $"The TwinCAT system of {netId} is already in {want} mode. Nothing was sent.";

        string control_ = ControlFor(mode);
        string? writeError = control.WriteControl(control_);
        if (writeError is not null)
            return $"[Error] WriteControl({control_}) to {netId}:10000 was refused: {writeError}. State stays {before.State}.";

        TargetStateReading last = before;
        while (elapsed() < timeout)
        {
            sleep(PollInterval);
            last = control.ReadState();
            if (last.Error is null && string.Equals(last.State, want, StringComparison.OrdinalIgnoreCase))
                return $"The TwinCAT system of {netId} switched from {before.State} to {want} " +
                       $"(WriteControl {control_}, confirmed by ReadState after {elapsed().TotalSeconds:F1} s)." +
                       (mode == SystemMode.Run ? PlcNote(control, netId, elapsed, sleep) : "");
        }
        string lastText = last.Error is null ? $"state {last.State}" : $"unreachable ({last.Error})";
        return $"[Error] WriteControl({control_}) was accepted by {netId}, but the system did not read {want} " +
               $"within {timeout.TotalSeconds:F0} s (last: {lastText}).";
    }

    /// <summary>
    /// A system in Run does not mean the PLC runs: with "Autostart Boot Project" off, or a boot project the runtime
    /// cannot start, the system service reads Run while the PLC on port 851 stays stopped (seen on a UMR). Gives the
    /// PLC <see cref="PlcStartGrace"/> to read Run and says so when it does not. Empty when the control cannot ask;
    /// a PLC that never answers is noted too, since a target without a PLC project looks the same.
    /// </summary>
    private static string PlcNote(ISystemControl control, string netId, Func<TimeSpan> elapsed, Action<TimeSpan> sleep)
    {
        TargetStateReading? plc = control.ReadPlcState(FirstPlcPort);
        if (plc is null) return "";

        TimeSpan until = elapsed() + PlcStartGrace;
        while (!plc.IsRun && elapsed() < until)
        {
            sleep(PollInterval);
            plc = control.ReadPlcState(FirstPlcPort) ?? plc;
        }
        if (plc.IsRun) return $" The PLC runtime on port {FirstPlcPort} reads Run.";
        return $" [Warn] The system reads Run, but the PLC runtime on port {FirstPlcPort} of {netId} reads {plc} " +
               $"after {PlcStartGrace.TotalSeconds:F0} s. With a PLC project on the target, 'Autostart Boot Project' is " +
               "probably off or the boot project failed to start: set it and activate again, or start the PLC " +
               "(twincat_plc_start; ADS WriteControl Run on port 851). Without a PLC project this note can be ignored.";
    }
}

/// <summary>The production <see cref="ISystemControl"/> over Beckhoff.TwinCAT.Ads.</summary>
internal sealed class AdsSystemControl : ISystemControl
{
    private readonly TargetStateReader _reader;
    private readonly string _netId;
    private TargetStateReader? _plcReader;
    private int _plcPort;

    public AdsSystemControl(string netId)
    {
        _netId = netId;
        _reader = TargetStateReader.Open(netId);
    }

    public TargetStateReading ReadState() => _reader.Read();

    public TargetStateReading? ReadPlcState(int amsPort)
    {
        if (_plcReader is null || _plcPort != amsPort)
        {
            _plcReader?.Dispose();
            _plcReader = TargetStateReader.Open(_netId, amsPort);
            _plcPort = amsPort;
        }
        return _plcReader.Read();
    }

#if TWINCAT
    public string? WriteControl(string adsState)
    {
        try
        {
            using var client = new AdsClient { Timeout = 5000 };
            client.Connect(_netId, ActivationTarget.SystemServicePort);
            var state = Enum.Parse<AdsState>(adsState);
            AdsErrorCode error = client.TryWriteControl(new StateInfo(state, 0));
            return error == AdsErrorCode.NoError ? null : $"ADS error {error}";
        }
        catch (Exception ex) { return ex.Message; }
    }
#else
    public string? WriteControl(string adsState) => TwinCatNotBuilt.Message;
#endif

    public void Dispose()
    {
        _reader.Dispose();
        _plcReader?.Dispose();
    }
}
