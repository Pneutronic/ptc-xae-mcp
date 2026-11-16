#if TWINCAT
using System.Collections.Concurrent;
using System.Diagnostics;
using TwinCAT.Ads;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>One symbol of an <see cref="AdsNotificationWatch"/>: where to register and how its bytes decode.</summary>
internal sealed record WatchTarget(string Path, int Size, Func<byte[], SignalReading> Decode);

/// <summary>
/// ADS on-change notifications of a set of symbols over one <see cref="AdsClient"/>. The SDK raises each notification
/// on its own thread; they are queued with the host's receive time and the runtime's time stamp, and
/// <see cref="TryTake"/> hands them to the single consumer (the signal testbench engine). The runtime state is checked
/// about once a second, because a stopped PLC sends nothing and "no change" would then pass for "stable".
/// </summary>
internal sealed class AdsNotificationWatch : IAdsWatch
{
    private static readonly TimeSpan StateCheckInterval = TimeSpan.FromSeconds(1);

    private readonly AdsClient _client;
    private readonly IReadOnlyList<WatchTarget> _targets;
    private readonly BlockingCollection<SignalSample> _queue = new();
    private readonly List<uint> _handles = [];
    private readonly Stopwatch _clock = new();
    private TimeSpan _lastStateCheck;
    private string? _fault;
    private int _disposed;

    public AdsNotificationWatch(AdsClient client, IReadOnlyList<WatchedSymbol> symbols, IReadOnlyList<WatchTarget> targets)
    {
        _client = client;
        Symbols = symbols;
        _targets = targets;
    }

    public IReadOnlyList<WatchedSymbol> Symbols { get; }

    public TimeSpan Elapsed => _clock.Elapsed;

    public string? Fault => Volatile.Read(ref _fault);

    /// <summary>Registers every symbol; null when all are registered, otherwise why one was not.</summary>
    public string? Start(int cycleMs)
    {
        _client.AdsNotification += OnNotification;
        _client.AdsNotificationError += OnError;
        _client.AdsNotificationsInvalidated += OnInvalidated;
        _clock.Start();
        var settings = new NotificationSettings(AdsTransMode.OnChange, cycleMs, 0);
        for (int i = 0; i < _targets.Count; i++)
        {
            try
            {
                _handles.Add(_client.AddDeviceNotification(_targets[i].Path, _targets[i].Size, settings, i));
            }
            catch (Exception ex) when (ComCall.NotBusy(ex))
            {
                return $"could not register a notification for '{Symbols[i].Symbol}': {ex.Message}";
            }
        }
        return null;
    }

    public bool TryTake(TimeSpan timeout, out SignalSample sample)
    {
        if (Elapsed - _lastStateCheck >= StateCheckInterval)
        {
            _lastStateCheck = Elapsed;
            CheckState();
        }
        return _queue.TryTake(out sample, timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
    }

    private void CheckState()
    {
        try
        {
            var error = _client.TryReadState(out StateInfo state);
            if (error != AdsErrorCode.NoError)
                SetFault($"the runtime does not answer ({error})");
            else if (state.AdsState != AdsState.Run)
                SetFault($"the runtime left Run (now {state.AdsState})");
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            SetFault($"the runtime does not answer ({ex.Message})");
        }
    }

    private void OnNotification(object? sender, AdsNotificationEventArgs e)
    {
        if (e.UserData is not int index || index < 0 || index >= _targets.Count) return;
        try
        {
            _queue.Add(new SignalSample(index, _clock.Elapsed, e.TimeStamp.UtcTicks, _targets[index].Decode(e.Data.ToArray())));
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
        {
            SetFault($"a value of '{Symbols[index].Symbol}' could not be decoded: {ex.Message}");
        }
    }

    private void OnError(object? sender, AdsNotificationErrorEventArgs e) =>
        SetFault($"the notification channel failed: {e.Exception.Message}");

    private void OnInvalidated(object? sender, AdsNotificationsInvalidatedEventArgs e) =>
        SetFault("the runtime invalidated the notifications (a new download, a restart or a stop)");

    private void SetFault(string why) => Interlocked.CompareExchange(ref _fault, why, null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.AdsNotification -= OnNotification;
        _client.AdsNotificationError -= OnError;
        _client.AdsNotificationsInvalidated -= OnInvalidated;
        foreach (uint handle in _handles)
        {
            try { _client.TryDeleteDeviceNotification(handle); }
            catch (Exception ex) when (ComCall.NotBusy(ex)) { /* the client is closed next */ }
        }
        _client.Dispose();
        _queue.Dispose();
    }
}
#endif
