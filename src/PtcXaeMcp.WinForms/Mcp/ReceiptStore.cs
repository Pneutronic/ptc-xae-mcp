using System.Text.Json;
using System.Text.Json.Serialization;
using PtcXaeMcp.WinForms.Diagnostics;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>How far a request with a <c>requestKey</c> got.</summary>
internal enum ReceiptOutcome
{
    /// <summary>Started, not finished (a second call with the key does not start it again).</summary>
    Running,
    /// <summary>Finished without an error.</summary>
    Ok,
    /// <summary>Finished with an error.</summary>
    Error,
    /// <summary>Started and never seen to finish (host restart, a cancelled call, a call held by an XAE dialog): it may or may not have run.</summary>
    Unknown,
}

/// <summary>The record of one request: who asked for what, when, and what came of it.</summary>
internal sealed record Receipt(
    string RequestKey, string Tool, string ArgumentHash, string? TargetNetId,
    DateTime StartedUtc, DateTime? FinishedUtc, ReceiptOutcome Outcome, string? ResultText)
{
    public static string Name(ReceiptOutcome outcome) => outcome.ToString().ToLowerInvariant();
}

/// <summary>What <see cref="ReceiptStore.Begin"/> found: a new request, or the one that is there already.</summary>
internal sealed record BeginResult(bool Created, Receipt Receipt, string? Failure);

/// <summary>
/// The receipts of the requests that carry a <c>requestKey</c>, kept in <c>%LOCALAPPDATA%\ptc-xae-mcp\receipts.jsonl</c>.
/// A line is appended for every state a request reaches (running, then ok, error or unknown); the last line of a key
/// wins. Lines older than <see cref="Retention"/> are dropped when the host starts, and a request that was
/// still running then becomes <c>unknown</c>: its host is gone, so nobody knows whether it finished.
/// </summary>
internal sealed class ReceiptStore
{
    public const string FileName = "receipts.jsonl";

    /// <summary>How long a receipt is kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>Longest result text kept per receipt; a longer one is cut and says so.</summary>
    internal const int MaxResultChars = 256 * 1024;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ptc-xae-mcp", FileName);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Receipt> _receipts = new(StringComparer.OrdinalIgnoreCase);

    public ReceiptStore(string path, Func<DateTime>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTime.UtcNow);
        Load();
    }

    public int Count
    {
        get { lock (_gate) return _receipts.Count; }
    }

    public Receipt? Get(string requestKey)
    {
        lock (_gate) return _receipts.GetValueOrDefault(requestKey);
    }

    /// <summary>
    /// Opens the request when the key is new (it is written down before anything runs); otherwise the receipt that is
    /// there comes back unchanged. Fails when the receipt cannot be written: a request that cannot be remembered
    /// must not run under a key that promises it runs once.
    /// </summary>
    public BeginResult Begin(string requestKey, string tool, string argumentHash, string? targetNetId)
    {
        lock (_gate)
        {
            if (_receipts.TryGetValue(requestKey, out var existing)) return new BeginResult(false, existing, null);
            var receipt = new Receipt(requestKey, tool, argumentHash, targetNetId, _clock(), null, ReceiptOutcome.Running, null);
            try
            {
                Append(receipt, "running");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new BeginResult(false, receipt, $"the receipt could not be written to {_path}: {ex.Message}");
            }
            _receipts[requestKey] = receipt;
            return new BeginResult(true, receipt, null);
        }
    }

    /// <summary>Records how the request ended. A failure to write is logged, the in-memory state stays right.</summary>
    public void Finish(string requestKey, ReceiptOutcome outcome, string? resultText)
    {
        lock (_gate)
        {
            if (!_receipts.TryGetValue(requestKey, out var open)) return;
            string? text = resultText is { Length: > MaxResultChars }
                ? resultText[..MaxResultChars] + $"\n[Receipt] The stored result was cut at {MaxResultChars} characters."
                : resultText;
            var done = open with { Outcome = outcome, ResultText = text, FinishedUtc = outcome == ReceiptOutcome.Unknown ? null : _clock() };
            _receipts[requestKey] = done;
            TryAppend(done, Receipt.Name(outcome));
        }
    }

    /// <summary>Forgets the request: nothing was done (a refusal), so the key may be used again.</summary>
    public void Void(string requestKey)
    {
        lock (_gate)
        {
            if (!_receipts.Remove(requestKey, out var open)) return;
            TryAppend(open, "void");
        }
    }

    // ---- file -----------------------------------------------------------------------------------------------

    private sealed record Line(
        string RequestKey, string Tool, string ArgumentHash, string? TargetNetId,
        DateTime StartedUtc, DateTime? FinishedUtc, string Outcome, string? ResultText);

    private void Append(Receipt receipt, string outcome)
    {
        var line = new Line(receipt.RequestKey, receipt.Tool, receipt.ArgumentHash, receipt.TargetNetId,
            receipt.StartedUtc, receipt.FinishedUtc, outcome, receipt.ResultText);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.AppendAllText(_path, JsonSerializer.Serialize(line, JsonOptions) + "\n");
    }

    private void TryAppend(Receipt receipt, string outcome)
    {
        try
        {
            Append(receipt, outcome);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HostLog.Shared.Warn($"Receipt of request {receipt.RequestKey} could not be written: {ex.Message}");
        }
    }

    private void Load()
    {
        int lines = 0;
        bool changed = false;
        try
        {
            if (!File.Exists(_path)) return;
            DateTime cutoff = _clock() - Retention;
            foreach (string text in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                lines++;
                Line? line;
                try { line = JsonSerializer.Deserialize<Line>(text, JsonOptions); }
                catch (JsonException) { changed = true; continue; }
                if (line is null || string.IsNullOrWhiteSpace(line.RequestKey)) { changed = true; continue; }

                if (line.Outcome == "void") { _receipts.Remove(line.RequestKey); continue; }
                var outcome = line.Outcome switch
                {
                    "ok" => ReceiptOutcome.Ok,
                    "error" => ReceiptOutcome.Error,
                    "unknown" => ReceiptOutcome.Unknown,
                    _ => ReceiptOutcome.Running,
                };
                _receipts[line.RequestKey] = new Receipt(line.RequestKey, line.Tool, line.ArgumentHash, line.TargetNetId,
                    line.StartedUtc, line.FinishedUtc, outcome, line.ResultText);
            }

            foreach (var (key, receipt) in _receipts.ToList())
            {
                if ((receipt.FinishedUtc ?? receipt.StartedUtc) < cutoff)
                {
                    _receipts.Remove(key);
                    changed = true;
                }
                else if (receipt.Outcome == ReceiptOutcome.Running)
                {
                    // The host that started it is gone: nobody knows whether it finished.
                    _receipts[key] = receipt with { Outcome = ReceiptOutcome.Unknown, FinishedUtc = null };
                    changed = true;
                }
            }
            if (changed || lines != _receipts.Count) Compact();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HostLog.Shared.Warn($"Receipts could not be read from {_path}: {ex.Message}");
        }
    }

    // The file keeps one line per request after a start: the folded state, written next to it and moved over the old file.
    private void Compact()
    {
        string temp = _path + ".tmp";
        File.WriteAllLines(temp, _receipts.Values.OrderBy(r => r.StartedUtc).Select(r => JsonSerializer.Serialize(
            new Line(r.RequestKey, r.Tool, r.ArgumentHash, r.TargetNetId, r.StartedUtc, r.FinishedUtc, Receipt.Name(r.Outcome), r.ResultText),
            JsonOptions)));
        File.Move(temp, _path, overwrite: true);
    }
}
