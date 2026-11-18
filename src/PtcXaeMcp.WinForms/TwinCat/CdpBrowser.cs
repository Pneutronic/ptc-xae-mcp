using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-authoring: a minimal Chrome DevTools Protocol client for twincat_hmi_check_liveview. Starts a headless
// Chromium (Edge, Chrome or a Playwright download) with a private, temporary profile,
// talks CDP over a WebSocket and is disposed with its process and profile. No package dependency.

/// <summary>A console message, exception, failed request or log entry seen while the page ran.</summary>
internal sealed record CdpEvent(string Kind, string Level, string Text, string? Url, int? Line);

internal sealed class CdpBrowser : IAsyncDisposable
{
    internal const string BrowserEnvVar = "PTC_XAE_MCP_BROWSER";

    private readonly Process _process;
    private readonly string _profile;
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentQueue<CdpEvent> _events = new();
    private readonly ConcurrentQueue<string> _methods = new();
    private Task? _receiver;
    private int _nextId;

    public string BrowserPath { get; }

    private CdpBrowser(Process process, string profile, string browserPath)
    {
        _process = process;
        _profile = profile;
        BrowserPath = browserPath;
    }

    public IReadOnlyList<CdpEvent> Events => [.. _events];

    /// <summary>Candidate browsers in order: the env override, Edge, Chrome, a Playwright Chromium.</summary>
    public static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable(BrowserEnvVar) is { Length: > 0 } env) yield return env;
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string root in new[] { pf86, pf, local }.Where(r => r.Length > 0).Distinct())
        {
            yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
        }
        // Chromium downloaded by Playwright (developer machines without Edge). The WebView2 runtime is NOT a
        // candidate: msedgewebview2.exe started on its own exits at once (code 13, live 2026-11-18).
        string playwright = Path.Combine(local, "ms-playwright");
        if (local.Length > 0 && Directory.Exists(playwright))
        {
            static int Build(string dir) => int.TryParse(dir[(dir.LastIndexOf('-') + 1)..], out int n) ? n : 0;
            foreach (string dir in Directory.GetDirectories(playwright, "chromium-*").OrderByDescending(Build))
                yield return Path.Combine(dir, "chrome-win64", "chrome.exe");
            foreach (string dir in Directory.GetDirectories(playwright, "chromium_headless_shell-*").OrderByDescending(Build))
                yield return Path.Combine(dir, "chrome-headless-shell-win64", "chrome-headless-shell.exe");
        }
    }

    public static string? FindBrowser() => Candidates().FirstOrDefault(File.Exists);

    public static async Task<CdpBrowser> LaunchAsync(string browserPath, int width, int height, TimeSpan timeout, CancellationToken ct)
    {
        string profile = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-liveview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var psi = new ProcessStartInfo(browserPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string a in new[]
                 {
                     "--headless=new", "--remote-debugging-port=0", "--remote-allow-origins=*", $"--user-data-dir={profile}",
                     "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-gpu",
                     "--disable-background-networking", "--disable-sync", "--mute-audio", $"--window-size={width},{height}",
                     "about:blank",
                 })
            psi.ArgumentList.Add(a);

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {browserPath}");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var browser = new CdpBrowser(process, profile, browserPath);
        try
        {
            string portFile = Path.Combine(profile, "DevToolsActivePort");
            var sw = Stopwatch.StartNew();
            string? port = null;
            while (sw.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException($"{Path.GetFileName(browserPath)} exited at start (code {process.ExitCode}).");
                if (File.Exists(portFile))
                {
                    try { port = (await File.ReadAllLinesAsync(portFile, ct)).FirstOrDefault(); } catch (IOException) { }
                    if (!string.IsNullOrWhiteSpace(port)) break;
                }
                await Task.Delay(100, ct);
            }
            if (string.IsNullOrWhiteSpace(port))
                throw new TimeoutException($"{Path.GetFileName(browserPath)} did not open its DevTools port within {timeout.TotalSeconds:0} s.");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string list = await http.GetStringAsync($"http://127.0.0.1:{port.Trim()}/json/list", ct);
            string? ws = JsonNode.Parse(list)?.AsArray()
                .Select(n => n?.AsObject())
                .FirstOrDefault(o => o?["type"]?.GetValue<string>() == "page")?["webSocketDebuggerUrl"]?.GetValue<string>();
            if (ws is null) throw new InvalidOperationException("the headless browser has no page target.");
            await browser._socket.ConnectAsync(new Uri(ws), ct);
            browser._receiver = Task.Run(() => browser.ReceiveLoop(browser._stop.Token), CancellationToken.None);
            return browser;
        }
        catch
        {
            await browser.DisposeAsync();
            throw;
        }
    }

    /// <summary>Sends a CDP command and waits for its result (throws on a protocol error).</summary>
    public async Task<JsonObject> SendAsync(string method, JsonObject? parameters, TimeSpan timeout, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        byte[] bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await using (cts.Token.Register(() => tcs.TrySetException(new TimeoutException($"CDP {method} did not answer within {timeout.TotalSeconds:0} s."))))
        {
            JsonObject reply = await tcs.Task;
            if (reply["error"] is JsonObject err)
                throw new InvalidOperationException($"CDP {method}: {err["message"]?.GetValue<string>()}");
            return reply["result"] as JsonObject ?? new JsonObject();
        }
    }

    /// <summary>True once <paramref name="method"/> was received (e.g. Page.loadEventFired).</summary>
    public bool Seen(string method) => _methods.Contains(method);

    /// <summary>Evaluates JavaScript in the page; returns the result as JSON text (promises awaited).</summary>
    public async Task<(string? Json, string? Error)> EvaluateAsync(string expression, TimeSpan timeout, CancellationToken ct)
    {
        var r = await SendAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = true, ["userGesture"] = true,
            ["timeout"] = (int)timeout.TotalMilliseconds,
        }, timeout + TimeSpan.FromSeconds(2), ct);
        if (r["exceptionDetails"] is JsonObject ex)
            return (null, ex["exception"]?["description"]?.GetValue<string>() ?? ex["text"]?.GetValue<string>() ?? "exception");
        JsonNode? result = r["result"];
        if (result?["type"]?.GetValue<string>() == "undefined") return ("null", null);
        return (result?["value"]?.ToJsonString() ?? "null", null);
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var r = await _socket.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                JsonObject? obj = null;
                try { obj = JsonNode.Parse(message.ToArray()) as JsonObject; } catch (JsonException) { }
                message.SetLength(0);
                if (obj is null) continue;
                if (obj["id"] is JsonValue idv && idv.TryGetValue(out int id) && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(obj);
                else if (obj["method"]?.GetValue<string>() is { } method)
                {
                    _methods.Enqueue(method);
                    if (Translate(method, obj["params"] as JsonObject) is { } e) _events.Enqueue(e);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
        foreach (var p in _pending.Values) p.TrySetException(new InvalidOperationException("the browser connection closed."));
    }

    /// <summary>The CDP events the live-view check reports; null for everything else.</summary>
    internal static CdpEvent? Translate(string method, JsonObject? p)
    {
        if (p is null) return null;
        switch (method)
        {
            case "Runtime.exceptionThrown":
                var d = p["exceptionDetails"] as JsonObject;
                string text = d?["exception"]?["description"]?.GetValue<string>() ?? d?["text"]?.GetValue<string>() ?? "exception";
                return new CdpEvent("exception", "error", FirstLine(text), d?["url"]?.GetValue<string>(), Int(d?["lineNumber"]) + 1);
            case "Runtime.consoleAPICalled":
                string type = p["type"]?.GetValue<string>() ?? "log";
                if (type is not ("error" or "warning" or "assert")) return null;
                string args = string.Join(" ", (p["args"] as JsonArray ?? new JsonArray()).Select(a =>
                    a?["value"] is JsonNode v ? (v is JsonValue jv && jv.TryGetValue(out string? s) ? s : v.ToJsonString())
                    : a?["description"]?.GetValue<string>() ?? a?["type"]?.GetValue<string>() ?? ""));
                var frame = (p["stackTrace"]?["callFrames"] as JsonArray)?.FirstOrDefault();
                return new CdpEvent("console", type == "warning" ? "warning" : "error", FirstLine(args),
                    frame?["url"]?.GetValue<string>(), Int(frame?["lineNumber"]) + 1);
            case "Log.entryAdded":
                var entry = p["entry"] as JsonObject;
                string level = entry?["level"]?.GetValue<string>() ?? "info";
                if (level is not ("error" or "warning")) return null;
                return new CdpEvent("log", level, FirstLine(entry?["text"]?.GetValue<string>() ?? ""), entry?["url"]?.GetValue<string>(), null);
            case "Network.loadingFailed":
                if (p["canceled"]?.GetValue<bool>() == true) return null;
                return new CdpEvent("request", "error", p["errorText"]?.GetValue<string>() ?? "failed", null, null);
            case "Network.responseReceived":
                var resp = p["response"] as JsonObject;
                int status = Int(resp?["status"]) ?? 0;
                return status >= 400
                    ? new CdpEvent("request", "error", $"HTTP {status}", resp?["url"]?.GetValue<string>(), null)
                    : null;
            default:
                return null;
        }
    }

    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue(out int i) ? i : n is JsonValue d && d.TryGetValue(out double x) ? (int)x : null;

    private static string FirstLine(string s)
    {
        int nl = s.IndexOf('\n');
        string line = nl < 0 ? s : s[..nl];
        return line.Length > 400 ? line[..400] + "…" : line;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                try { await SendAsync("Browser.close", null, TimeSpan.FromSeconds(3), CancellationToken.None); }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or WebSocketException) { }
            }
        }
        finally
        {
            _stop.Cancel();
            try { if (_receiver is not null) await _receiver.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            _socket.Dispose();
            try { if (!_process.WaitForExit(3000)) _process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _process.Dispose();
            for (int i = 0; i < 5; i++)
            {
                try { if (Directory.Exists(_profile)) Directory.Delete(_profile, recursive: true); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
            }
            _stop.Dispose();
        }
    }
}
