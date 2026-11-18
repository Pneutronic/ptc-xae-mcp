using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-authoring: twincat_hmi_check_liveview — load the live view of the HMI engineering server in a headless
// browser and report what a human would only see in the browser console: exceptions, console errors, failed
// requests, a view that never appears, controls that render with no size. Optionally a screenshot and a
// JavaScript expression evaluated in the page (the test hook for user controls).

internal sealed record HmiLiveViewOptions(
    string Url,
    string? View,
    int WaitMs,
    int SettleMs,
    int Width,
    int Height,
    string? ScreenshotPath,
    string? Expression);

internal static class HmiLiveViewCheck
{
    internal const int DefaultWaitMs = 30_000;
    internal const int MaxWaitMs = 120_000;
    internal const int DefaultSettleMs = 1_500;
    internal const int MaxExpressionLength = 20_000;
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    // Framework state without depending on one API version: TcHmi present, current view and its id.
    internal const string StateScript = """
        (() => {
          const t = window.TcHmi;
          if (location.pathname.toLowerCase().indexOf('login') >= 0) return { state: 'login', path: location.pathname };
          if (!t) return { state: 'no-framework' };
          let view = null;
          try { view = t.View && typeof t.View.get === 'function' ? t.View.get() : null; } catch (e) { return { state: 'error', error: String(e) }; }
          if (!view) return { state: 'loading' };
          let id = null;
          try { id = typeof view.getId === 'function' ? view.getId() : null; } catch (e) { }
          return { state: 'ready', view: id, version: (t.Version || t.version || null) };
        })()
        """;

    // Controls as rendered: TcHmi's control registry when the API offers it, the DOM otherwise; plus controls
    // that are attached but take no space (a typical sign of a broken parameter or template).
    internal const string ControlsScript = """
        (() => {
          const out = { source: 'dom', total: 0, types: {}, zeroSize: [] };
          const seen = new Set();
          const visit = (id, type, el) => {
            if (!id || seen.has(id)) return; seen.add(id); out.total++;
            if (type) out.types[type] = (out.types[type] || 0) + 1;
            if (el && el.getBoundingClientRect) {
              const r = el.getBoundingClientRect();
              if ((r.width === 0 || r.height === 0) && out.zeroSize.length < 25) out.zeroSize.push(id);
            }
          };
          try {
            const map = window.TcHmi && TcHmi.Controls && typeof TcHmi.Controls.getMap === 'function' ? TcHmi.Controls.getMap() : null;
            if (map && typeof map.forEach === 'function') {
              out.source = 'TcHmi.Controls.getMap';
              map.forEach((c, id) => {
                let type = null, el = null;
                try { type = typeof c.getType === 'function' ? c.getType() : null; } catch (e) { }
                try { const e = typeof c.getElement === 'function' ? c.getElement() : null; el = e && e[0] ? e[0] : e; } catch (e) { }
                visit(id, type, el);
              });
            }
          } catch (e) { out.registryError = String(e); }
          if (out.total === 0) document.querySelectorAll('[data-tchmi-type]').forEach(el => visit(el.id, el.getAttribute('data-tchmi-type'), el));
          return out;
        })()
        """;

    public static async Task<(string Summary, string Json)> RunAsync(HmiLiveViewOptions o, CancellationToken ct)
    {
        string? browserPath = CdpBrowser.FindBrowser();
        if (browserPath is null)
            return ("[Error] No Chromium browser found for the headless check (Microsoft Edge, Google Chrome or a Playwright " +
                    $"Chromium). Install one or set {CdpBrowser.BrowserEnvVar} to the path of msedge.exe or chrome.exe.", "{}");

        var sw = Stopwatch.StartNew();
        CdpBrowser browser;
        try { browser = await CdpBrowser.LaunchAsync(browserPath, o.Width, o.Height, TimeSpan.FromSeconds(20), ct); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or HttpRequestException
                                       or System.Net.WebSockets.WebSocketException or System.ComponentModel.Win32Exception)
        {
            return ($"[Error] The headless browser ({browserPath}) could not be started: {ex.Message}", "{}");
        }

        await using (browser)
        {
            try
            {
                foreach (string domain in new[] { "Runtime.enable", "Log.enable", "Network.enable", "Page.enable" })
                    await browser.SendAsync(domain, null, CallTimeout, ct);
                await browser.SendAsync("Emulation.setDeviceMetricsOverride", new JsonObject
                    { ["width"] = o.Width, ["height"] = o.Height, ["deviceScaleFactor"] = 1, ["mobile"] = false }, CallTimeout, ct);
                var nav = await browser.SendAsync("Page.navigate", new JsonObject { ["url"] = o.Url }, CallTimeout, ct);
                if (nav["errorText"]?.GetValue<string>() is { Length: > 0 } navError)
                    return ($"[Error] The live view {o.Url} could not be loaded: {navError}. Is the HMI engineering server running " +
                            "(twincat_hmi_server_control start) and the project built (twincat_hmi_build)?", "{}");

                // wait for the framework and its first view
                JsonNode? state = null;
                var deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < o.WaitMs)
                {
                    var (json, _) = await browser.EvaluateAsync(StateScript, CallTimeout, ct);
                    state = json is null ? null : JsonNode.Parse(json);
                    string s = state?["state"]?.GetValue<string>() ?? "";
                    if (s is "ready" or "login" or "error") break;
                    await Task.Delay(Step, ct);
                }
                string ready = state?["state"]?.GetValue<string>() ?? "unknown";
                long readyMs = deadline.ElapsedMilliseconds;

                string? viewLoaded = state?["view"]?.GetValue<string>();
                string? viewError = null;
                if (ready == "ready" && o.View is { Length: > 0 } wanted)
                {
                    string js = "new Promise(res => { try { TcHmi.View.load(" + JsonSerializer.Serialize(wanted) +
                                ", d => res(d && d.error ? String(d.error) + (d.details && d.details.reason ? ': ' + d.details.reason : '') : null)); } " +
                                "catch (e) { res(String(e)); } })";
                    var (r, err) = await browser.EvaluateAsync(js, TimeSpan.FromMilliseconds(Math.Max(5000, o.WaitMs / 2)), ct);
                    viewError = err ?? (r is null or "null" ? null : JsonNode.Parse(r)?.GetValue<string>());
                    var (after, _) = await browser.EvaluateAsync(StateScript, CallTimeout, ct);
                    viewLoaded = after is null ? viewLoaded : JsonNode.Parse(after)?["view"]?.GetValue<string>() ?? viewLoaded;
                }

                if (o.SettleMs > 0) await Task.Delay(o.SettleMs, ct);

                JsonNode? controls = null;
                if (ready == "ready")
                {
                    var (cj, _) = await browser.EvaluateAsync(ControlsScript, CallTimeout, ct);
                    controls = cj is null ? null : JsonNode.Parse(cj);
                }

                string? expressionJson = null, expressionError = null;
                if (o.Expression is { Length: > 0 } expr)
                    (expressionJson, expressionError) = await browser.EvaluateAsync(expr, TimeSpan.FromMilliseconds(Math.Max(5000, o.WaitMs)), ct);

                string? shot = null;
                if (o.ScreenshotPath is { Length: > 0 } path)
                {
                    var img = await browser.SendAsync("Page.captureScreenshot", new JsonObject { ["format"] = "png" }, TimeSpan.FromSeconds(20), ct);
                    string? data = img["data"]?.GetValue<string>();
                    if (data is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await File.WriteAllBytesAsync(path, Convert.FromBase64String(data), ct);
                        shot = path;
                    }
                }

                var events = browser.Events;
                return Report(o, browserPath, ready, readyMs, state, viewLoaded, viewError, controls, events, shot,
                    expressionJson, expressionError, sw.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException
                                           or System.Net.WebSockets.WebSocketException or JsonException)
            {
                return ($"[Error] Live-view check of {o.Url} failed: {ex.Message}", "{}");
            }
        }
    }

    internal static (string Summary, string Json) Report(HmiLiveViewOptions o, string browserPath, string ready, long readyMs,
        JsonNode? state, string? view, string? viewError, JsonNode? controls, IReadOnlyList<CdpEvent> events, string? screenshot,
        string? expressionJson, string? expressionError, long totalMs)
    {
        // the same message repeated by a polling binding is one finding
        var grouped = events
            .GroupBy(e => (e.Kind, e.Level, e.Text, e.Url))
            .Select(g => new { kind = g.Key.Kind, level = g.Key.Level, text = g.Key.Text, url = g.Key.Url, line = g.First().Line, count = g.Count() })
            .ToList();
        int errors = grouped.Count(g => g.level == "error");
        int warnings = grouped.Count(g => g.level == "warning");
        int total = controls?["total"]?.GetValue<int>() ?? 0;
        int zero = (controls?["zeroSize"] as JsonArray)?.Count ?? 0;

        string verdict = ready switch
        {
            "ready" when viewError is not null => $"view '{o.View}' did not load: {viewError}",
            "ready" => $"view '{view}' ready after {readyMs} ms, {total} control(s){(zero > 0 ? $", {zero} without size" : "")}",
            "login" => "the server answered with its login page — the live view needs a user (engineering server with authentication)",
            "no-framework" => $"no TcHmi framework after {o.WaitMs} ms — not an HMI live view, or the project was not built",
            "loading" => $"the framework did not show a view within {o.WaitMs} ms",
            _ => $"framework state '{ready}'",
        };
        bool ok = ready == "ready" && viewError is null && errors == 0 && expressionError is null;
        string summary = (ok ? "" : "[Warn] ") + $"Live view {o.Url}: {verdict}; {errors} error(s), {warnings} warning(s) in the console/network" +
                         (screenshot is not null ? $"; screenshot {screenshot}" : "") +
                         (o.Expression is not null ? expressionError is null ? $"; expression → {Shorten(expressionJson)}" : $"; expression threw: {expressionError}" : "") +
                         ".";
        if (errors > 0)
            summary += " First: " + string.Join(" | ", grouped.Where(g => g.level == "error").Take(3)
                .Select(g => $"{g.kind}: {g.text}{(g.url is null ? "" : $" ({ShortUrl(g.url)}{(g.line is null ? "" : $":{g.line}")})")}"));

        var json = new
        {
            url = o.Url, browser = browserPath, ok, framework = ready, readyMs, frameworkState = state, view, requestedView = o.View,
            viewError, controls, errors, warnings, events = grouped, screenshot,
            expression = o.Expression is null ? null : new { result = expressionJson is null ? null : JsonNode.Parse(expressionJson), error = expressionError },
            totalMs,
        };
        return (summary, JsonSerializer.Serialize(json, Indented));
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string Shorten(string? s) => s is null ? "null" : s.Length <= 200 ? s : s[..200] + "…";

    private static string ShortUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
}
