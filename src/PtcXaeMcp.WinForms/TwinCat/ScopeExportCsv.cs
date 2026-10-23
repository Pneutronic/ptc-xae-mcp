using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>One point of a decimated channel: time in ms since the recording start, and the value.</summary>
internal sealed record ScopePoint(double TimeMs, double Value);

/// <summary>A channel of a scope recording: its metadata block, statistics and (optionally) decimated points.</summary>
internal sealed class ScopeChannel
{
    public required string Name { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public long SampleCount { get; set; }
    public double? FirstMs { get; set; }
    public double? LastMs { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Mean => SampleCount == 0 ? null : Sum / SampleCount;
    public bool Decimated { get; set; }
    public List<ScopePoint>? Points { get; set; }

    internal double Sum { get; set; }

    public string? Meta(string key) => Metadata.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
}

internal sealed record ScopeRecording(
    string? Name, DateTime? StartUtc, DateTime? EndUtc, IReadOnlyList<ScopeChannel> Channels);

/// <summary>Which channels get points, the time window and the point budget per channel.</summary>
internal sealed record ScopePointRequest(IReadOnlySet<string>? Channels, double? FromMs, double? ToMs, int MaxPoints);

/// <summary>
/// Reads the tab-separated CSV that TC3ScopeExportTool.exe writes for a .svdx (layout seen on TE130x 3.x):
/// a header (Name, File, "Starttime/Endtime of export" as FILETIME UTC + localized text), a metadata block with one
/// key/value column pair per channel (Name, SymbolComment, Data-Type, SampleTime[ms], …), then one time/value column
/// pair per channel (time in ms since the start; shorter channels leave their cells empty), closed by "EOF".
/// Streams the data rows: only statistics and, for requested channels, min/max buckets are kept in memory.
/// </summary>
internal static class ScopeExportCsv
{
    public const int DefaultMaxPoints = 500;
    public const int MaxMaxPoints = 5000;

    public static ScopeRecording Parse(TextReader reader, ScopePointRequest? points = null)
    {
        string? name = null;
        DateTime? start = null, end = null;
        string? line;

        // Header until the metadata block, which starts with "Name\t<channel>\tName\t…".
        while ((line = reader.ReadLine()) is not null)
        {
            string[] cells = line.Split('\t');
            if (cells[0] == "Name" && cells.Length >= 4 && cells[2] == "Name") break;
            switch (cells[0])
            {
                case "Name" when cells.Length > 1: name = cells[1]; break;
                case "Starttime of export": start = FileTime(cells); break;
                case "Endtime of export": end = FileTime(cells); break;
            }
        }
        if (line is null) throw new FormatException("No channel block found (expected a line \"Name\t<channel>\tName\t…\").");

        // Metadata: key/value pairs per channel until the first empty line.
        string[] first = line.Split('\t');
        int channelCount = first.Length / 2;
        var metadata = Enumerable.Range(0, channelCount).Select(_ => new Dictionary<string, string>(StringComparer.Ordinal)).ToList();
        AddMeta(first, metadata);
        while ((line = reader.ReadLine()) is not null && line.Trim().Length > 0)
            AddMeta(line.Split('\t'), metadata);

        var channels = metadata.Select(m => new ScopeChannel { Name = m.GetValueOrDefault("Name", ""), Metadata = m }).ToList();
        var buckets = channels.Select(c => Buckets.For(c, points)).ToList();

        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("EOF", StringComparison.Ordinal)) break;
            string[] cells = line.Split('\t');
            for (int c = 0; c < channelCount; c++)
            {
                int ti = c * 2;
                if (ti + 1 >= cells.Length) break;
                if (!TryNumber(cells[ti], out double t) || !TryNumber(cells[ti + 1], out double v)) continue;
                var ch = channels[c];
                ch.SampleCount++;
                ch.Sum += v;
                ch.FirstMs ??= t;
                ch.LastMs = t;
                ch.Min = ch.Min is null ? v : Math.Min(ch.Min.Value, v);
                ch.Max = ch.Max is null ? v : Math.Max(ch.Max.Value, v);
                buckets[c]?.Add(t, v);
            }
        }

        for (int c = 0; c < channelCount; c++)
            if (buckets[c] is { } b) (channels[c].Points, channels[c].Decimated) = b.Result();
        return new ScopeRecording(name, start, end, channels);
    }

    private static void AddMeta(string[] cells, List<Dictionary<string, string>> metadata)
    {
        for (int c = 0; c < metadata.Count && c * 2 < cells.Length; c++)
        {
            string key = cells[c * 2].Trim();
            if (key.Length == 0) continue;
            metadata[c][key] = c * 2 + 1 < cells.Length ? cells[c * 2 + 1].Trim() : "";
        }
    }

    // The FILETIME column is UTC (it matches the localized text shifted by the local offset); the text is never parsed.
    private static DateTime? FileTime(string[] cells) =>
        cells.Length > 1 && long.TryParse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ft) && ft > 0
            ? DateTime.FromFileTimeUtc(ft)
            : null;

    internal static bool TryNumber(string cell, out double value)
    {
        cell = cell.Trim();
        if (double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        // A decimal comma (a localized export) is the only other form accepted.
        return cell.Count(ch => ch == ',') == 1 && !cell.Contains('.', StringComparison.Ordinal) &&
               double.TryParse(cell.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Min/max decimation like the scope display: the time window is cut into MaxPoints/2 buckets and each keeps
    /// its minimum and maximum (with their times), so a spike survives. The window is the request's, or the
    /// channel's full length (known only after the scan: values are kept raw until the budget is exceeded).
    /// </summary>
    private sealed class Buckets
    {
        private readonly double? _from, _to;
        private readonly int _maxPoints;
        private readonly List<ScopePoint> _raw = [];

        private Buckets(double? from, double? to, int maxPoints) { _from = from; _to = to; _maxPoints = maxPoints; }

        public static Buckets? For(ScopeChannel channel, ScopePointRequest? request)
        {
            if (request is null) return null;
            if (request.Channels is { Count: > 0 } wanted &&
                !wanted.Contains(channel.Name) && !(channel.Meta("SymbolName") is { } s && wanted.Contains(s)))
                return null;
            return new Buckets(request.FromMs, request.ToMs, Math.Clamp(request.MaxPoints, 2, MaxMaxPoints));
        }

        public void Add(double t, double v)
        {
            if (_from is { } f && t < f) return;
            if (_to is { } e && t > e) return;
            _raw.Add(new ScopePoint(t, v));
        }

        public (List<ScopePoint> Points, bool Decimated) Result()
        {
            if (_raw.Count <= _maxPoints) return (_raw, false);
            double lo = _raw[0].TimeMs, hi = _raw[^1].TimeMs;
            int bucketCount = _maxPoints / 2;
            double width = (hi - lo) / bucketCount;
            var result = new List<ScopePoint>(_maxPoints);
            int i = 0;
            for (int b = 0; b < bucketCount && i < _raw.Count; b++)
            {
                double end = b == bucketCount - 1 ? double.PositiveInfinity : lo + (b + 1) * width;
                ScopePoint? min = null, max = null;
                for (; i < _raw.Count && _raw[i].TimeMs < end; i++)
                {
                    var p = _raw[i];
                    if (min is null || p.Value < min.Value) min = p;
                    if (max is null || p.Value > max.Value) max = p;
                }
                if (min is null || max is null) continue;
                if (ReferenceEquals(min, max)) { result.Add(min); continue; }
                result.Add(min.TimeMs <= max.TimeMs ? min : max);
                result.Add(min.TimeMs <= max.TimeMs ? max : min);
            }
            return (result, true);
        }
    }
}
