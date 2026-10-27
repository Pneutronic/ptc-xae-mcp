using System.Runtime.InteropServices;
#if TWINCAT
using TwinCAT.Ads;
#endif

namespace PtcXaeMcp.WinForms.TwinCat;

// Issue #195: the probes behind ActivationWait.AwaitRun. Win32 and ADS only, no COM, so they are safe
// on the background thread that watches the target while StartRestartTwinCAT() runs.

/// <summary>Win32 side of the activation wait: the attached XAE's modal windows, and closing an OK-only box.</summary>
internal static class ActivationWindows
{
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);

    private static readonly TimeSpan ClickCloseWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CloseMessageWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The modal windows of the XAE in <paramref name="scope"/> right now. A window counts when it is visible,
    /// enabled (the top of a modal chain) and either an ownerless message box or owned by a disabled window.
    /// That is how Win32 modality looks from outside (#149). Never the host's own windows (#150). Never throws.
    /// </summary>
    public static IReadOnlyList<XaeDialog> OpenModals(XaeWindowScope scope)
    {
        try { return XaeDialogs.Snapshot(scope, keep: IsModal); }
        catch (Exception) { return []; } // a Win32 enumeration hiccup: nothing seen this time
    }

    /// <summary>Handles of <see cref="OpenModals"/>: the windows that were open before an activation started.</summary>
    public static IReadOnlySet<IntPtr> OpenModalHandles(XaeWindowScope scope) =>
        OpenModals(scope).Select(d => d.Hwnd).ToHashSet();

    // Decided before the window's controls are read (XaeDialogs.Snapshot's keep filter).
    private static bool IsModal(IntPtr hwnd, IntPtr owner, string className) =>
        IsWindowEnabled(hwnd)
        && (owner == IntPtr.Zero
            ? string.Equals(className, "#32770", StringComparison.Ordinal)
            : !IsWindowEnabled(owner));

    /// <summary>
    /// Closes an OK-only box with the #150 helpers: BM_CLICK on its OK button (WM_COMMAND IDOK only when no such
    /// button exists), verified from window state. When the click leaves it open, WM_CLOSE follows. True only
    /// when the window is gone.
    /// </summary>
    public static bool CloseOkBox(XaeDialog box)
    {
        ArgumentNullException.ThrowIfNull(box);
        XaeDialogs.Execute(box, XaeDialogs.PlanClick(box, XaeDialogs.IDOK, "OK"));
        if (XaeDialogs.WaitClosed(box.Hwnd, ClickCloseWait)) return true;
        XaeDialogs.Close(box.Hwnd);
        return XaeDialogs.WaitClosed(box.Hwnd, CloseMessageWait);
    }
}

/// <summary>
/// ADS ReadState of a target's TwinCAT system service (AMS port <see cref="ActivationTarget.SystemServicePort"/>),
/// over one connection that is reused between reads. Thread-safe. <see cref="Read"/> never throws: a failed read
/// is a <see cref="TargetStateReading"/> with its error.
/// </summary>
internal sealed class TargetStateReader : IDisposable
{
    /// <summary>ADS timeout of one read, so a silent target cannot stall the wait beyond a poll or two.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly string _netId;
    private readonly int _port;
    private bool _disposed;

    private TargetStateReader(string netId, int port)
    {
        _netId = netId;
        _port = port;
    }

    public string NetId => _netId;

    /// <summary>Reader for the system service (port 10000) or, with <paramref name="port"/>, another AMS port such as a PLC runtime.</summary>
    public static TargetStateReader Open(string netId, int port = ActivationTarget.SystemServicePort) => new(netId, port);

#if TWINCAT
    private AdsClient? _client;

    public TargetStateReading Read()
    {
        lock (_gate)
        {
            if (_disposed) return TargetStateReading.Unreachable("the state reader is closed");
            try
            {
                if (_client is null)
                {
                    var client = new AdsClient { Timeout = (int)ReadTimeout.TotalMilliseconds };
                    client.Connect(_netId, _port);
                    _client = client;
                }
                AdsErrorCode error = _client.TryReadState(out StateInfo state);
                return error == AdsErrorCode.NoError
                    ? TargetStateReading.Of(state.AdsState.ToString())
                    : TargetStateReading.Unreachable($"ADS error {error}");
            }
            catch (Exception ex)
            {
                // Next read reconnects: a router that was down may be back.
                DropClient();
                return TargetStateReading.Unreachable(ex.Message);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            DropClient();
        }
    }

    private void DropClient()
    {
        try { _client?.Dispose(); }
        catch (Exception) { /* a client that fails to dispose is dropped all the same */ }
        _client = null;
    }
#else
    public TargetStateReading Read()
    {
        lock (_gate)
            return _disposed
                ? TargetStateReading.Unreachable("the state reader is closed")
                : TargetStateReading.Unreachable(TwinCatNotBuilt.Message);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }
#endif
}
