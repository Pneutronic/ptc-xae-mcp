using System.Text.Json;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

/// <summary>
/// An <see cref="IAdsWatch"/> that replays scripted changes on a virtual clock: <c>TryTake</c> advances the clock to
/// the next change (or by the timeout), so a window of minutes runs instantly and the outcome never depends on the
/// machine. The runtime's time stamps are the scripted times plus a fixed offset, like a real runtime clock.
/// </summary>
internal sealed class ScriptedAdsWatch : IAdsWatch
{
    private const long PlcOffsetTicks = 638_000_000_000_000_000;

    private readonly List<(TimeSpan At, int Index, SignalReading Reading)> _script = [];
    private TimeSpan _now;
    private int _next;
    private bool _sorted = true;

    public ScriptedAdsWatch(params WatchedSymbol[] symbols) => Symbols = symbols;

    public IReadOnlyList<WatchedSymbol> Symbols { get; }
    public TimeSpan Elapsed => _now;
    public string? Fault { get; set; }
    public bool Disposed { get; private set; }

    /// <summary>The runtime fails (stops) at this time.</summary>
    public TimeSpan? FaultAt { get; set; }

    public static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    public static SignalReading Num(long v) => new(JsonSerializer.SerializeToElement(v), null);
    public static SignalReading Real(double v) => new(JsonSerializer.SerializeToElement(v), null);
    public static SignalReading Bool(bool v) => new(JsonSerializer.SerializeToElement(v), null);
    public static SignalReading Text(string v) => new(JsonSerializer.SerializeToElement(v), null);
    public static SignalReading EnumValue(long v, string? name) => new(JsonSerializer.SerializeToElement(v), name);

    public static WatchedSymbol Sym(string name, string type, SignalReading initial, string? wire = null, bool isEnum = false) =>
        new(name, type, wire ?? type, isEnum, initial);

    public ScriptedAdsWatch Change(double ms, int index, SignalReading reading)
    {
        _script.Add((TimeSpan.FromMilliseconds(ms), index, reading));
        _sorted = false;
        return this;
    }

    public bool TryTake(TimeSpan timeout, out SignalSample sample)
    {
        sample = default;
        if (!_sorted)
        {
            var ordered = _script.OrderBy(c => c.At).ToList(); // stable: changes at the same time keep their script order
            _script.Clear();
            _script.AddRange(ordered);
            _sorted = true;
        }
        TimeSpan until = _now + timeout;
        if (FaultAt is { } f && until >= f && Fault is null)
        {
            _now = f;
            Fault = "the runtime left Run (now Stop)";
            return false;
        }
        if (_next < _script.Count && _script[_next].At <= until)
        {
            var (at, index, reading) = _script[_next++];
            _now = at > _now ? at : _now;
            sample = new SignalSample(index, _now, PlcOffsetTicks + at.Ticks, reading);
            return true;
        }
        _now = until;
        return false;
    }

    public void Dispose() => Disposed = true;
}
