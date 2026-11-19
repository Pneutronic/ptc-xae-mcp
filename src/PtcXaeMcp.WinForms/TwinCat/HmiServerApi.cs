using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-tf2000: a small client for the WebSocket API of a TwinCAT HMI server (TF2000 runtime or the XAE's
// engineering server — the same TcHmiSrv). Requests are {"requestType":"ReadWrite","id":n,"commands":[…]};
// the answer carries the same id. Verified live 2026-11-19 against server 24.0.7:
//   - The engineering server answers local clients without login as __SystemUser with every right.
//   - Login is the command {"symbol":"Login","writeValue":{"userName","password","persistent":false}} on the
//     same socket (as the framework's ServerManager.login does); afterwards GetCurrentUser names that user.
//   - A server whose license expired ("License Expired" page) never answers the WebSocket.

internal sealed class HmiServerApiException(string message) : Exception(message);

internal sealed class HmiServerApiClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private int _nextId = 1;

    private HmiServerApiClient(ClientWebSocket socket) => _socket = socket;

    /// <summary>ws(s)://host:port/ for an http(s) endpoint; null + error for anything else.</summary>
    public static (Uri? WebSocket, Uri? Http, string? Error) ParseEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? u))
            return (null, null, $"endpoint '{endpoint}' is not an absolute URL (e.g. https://192.168.1.10:2020).");
        string? ws = u.Scheme switch
        {
            "http" or "ws" => "ws",
            "https" or "wss" => "wss",
            _ => null,
        };
        if (ws is null) return (null, null, $"endpoint '{endpoint}' must be http(s):// or ws(s)://.");
        string http = ws == "wss" ? "https" : "http";
        return (new UriBuilder(ws, u.Host, u.Port, "/").Uri, new UriBuilder(http, u.Host, u.Port, "/").Uri, null);
    }

    public static async Task<HmiServerApiClient> ConnectAsync(Uri webSocket, Uri http, bool trustAnyCertificate, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", http.GetLeftPart(UriPartial.Authority));
        // Opt-in for the self-signed certificate a TF2000 server creates for itself: an untrusted chain and a
        // host-name mismatch (access by IP) are accepted, a missing certificate is not.
        if (trustAnyCertificate)
            socket.Options.RemoteCertificateValidationCallback = (_, cert, _, errors) => cert is not null &&
                (errors & ~(System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors |
                            System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch)) == 0;
        try
        {
            await socket.ConnectAsync(webSocket, ct).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new HmiServerApiClient(socket);
    }

    /// <summary>Sends one ReadWrite request and returns the answer with the same id.</summary>
    public async Task<JsonObject> RequestAsync(JsonArray commands, CancellationToken ct)
    {
        int id = _nextId++;
        var request = new JsonObject { ["requestType"] = "ReadWrite", ["id"] = id, ["commands"] = commands };
        byte[] bytes = Encoding.UTF8.GetBytes(request.ToJsonString());
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) throw new HmiServerApiException("the server closed the connection.");
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            JsonNode? node;
            try { node = JsonNode.Parse(ms.ToArray()); }
            catch (JsonException) { continue; }
            if (node is JsonObject o && IsAnswerTo(o, id)) return o;
        }
    }

    /// <summary>Whether an answer carries the request id. The server writes numbers as doubles: id 1 comes back
    /// as <c>"id":1.0</c> (also apiVersion 24.0; seen live 2026-11-19, server 24.0.7).</summary>
    internal static bool IsAnswerTo(JsonObject answer, int id) =>
        answer["id"] is JsonValue v && v.TryGetValue(out double got) && got == id;

    /// <summary>Logs in on this socket; null on success, else the server's reason.</summary>
    public async Task<string?> LoginAsync(string user, string password, CancellationToken ct)
    {
        var cmd = new JsonObject
        {
            ["commandOptions"] = new JsonArray("SendErrorMessage"),
            ["symbol"] = "Login",
            ["writeValue"] = new JsonObject { ["userName"] = user, ["password"] = password, ["persistent"] = false },
        };
        JsonObject answer = await RequestAsync([cmd], ct).ConfigureAwait(false);
        return HmiServerApi.ErrorOf(answer) ?? HmiServerApi.ErrorOf(answer["commands"]?[0]);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { /* closing is best effort */ }
        _socket.Dispose();
    }
}

internal sealed record HmiServerEvent(string Time, string Severity, string Domain, string Name, string Text, bool Alarm, string? AlarmState);

internal static class HmiServerApi
{
    internal static readonly string[] SeverityNames = ["verbose", "info", "warning", "error", "critical"];

    /// <summary>The commands one diagnostics call sends.</summary>
    internal static readonly string[] DiagnosticsSymbols =
        ["GetServerInformation", "Diagnostics", "ListDomains", "ListOutdatedSymbols", "ListActiveSessions", "GetCurrentUser", "ListEvents"];

    public static JsonArray DiagnosticsCommands()
    {
        var a = new JsonArray();
        foreach (string s in DiagnosticsSymbols) a.Add(new JsonObject { ["symbol"] = s });
        return a;
    }

    /// <summary>Connects, logs in when a user is given, sends the diagnostics commands and builds the report.</summary>
    public static async Task<(string Summary, string Json)> RunDiagnosticsAsync(string endpoint, string? user, string? password,
        bool trustAnyCertificate, int minSeverity, int maxEvents, int timeoutMs, string? note, JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        var (ws, http, error) = HmiServerApiClient.ParseEndpoint(endpoint);
        if (ws is null || http is null) return ($"[Error] {error}", "{}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await using var client = await HmiServerApiClient.ConnectAsync(ws, http, trustAnyCertificate, cts.Token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(user))
            {
                string? loginError = await client.LoginAsync(user, password ?? "", cts.Token).ConfigureAwait(false);
                if (loginError is not null) return ($"[Error] login as '{user}' at {http} failed: {loginError}.", "{}");
            }
            JsonObject answer = await client.RequestAsync(DiagnosticsCommands(), cts.Token).ConfigureAwait(false);
            if (ErrorOf(answer) is { } requestError && answer["commands"] is null)
                return ($"[Error] the server at {http} refused the request: {requestError}.", "{}");
            var (summary, json) = Diagnose(answer, http.GetLeftPart(UriPartial.Authority), note, minSeverity, maxEvents);
            return (summary, JsonSerializer.Serialize(json, jsonOptions));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ($"[Error] no answer from {http} within {timeoutMs} ms. A TF2000 server whose license expired accepts the " +
                    "connection but never answers (its pages say 'License Expired').", "{}");
        }
        catch (Exception ex) when (ex is WebSocketException or HmiServerApiException or System.Net.Http.HttpRequestException
                                       or System.Security.Authentication.AuthenticationException)
        {
            string hint = IsCertificateError(ex)
                ? " The server certificate is not trusted — pass trustServerCertificate=true for a self-signed TF2000 certificate."
                : "";
            string messages = Messages(ex);
            if (hint.Length > 0 && !messages.EndsWith('.')) messages += ".";
            return ($"[Error] cannot talk to the HMI server at {http}: {messages}{hint}", "{}");
        }
    }

    // ClientWebSocket wraps a TLS failure twice: WebSocketException → HttpRequestException → AuthenticationException.
    internal static bool IsCertificateError(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is System.Security.Authentication.AuthenticationException) return true;
        return false;
    }

    /// <summary>The messages of the exception chain, outer first, without repeats.</summary>
    internal static string Messages(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (!string.IsNullOrWhiteSpace(e.Message) && !parts.Contains(e.Message.Trim())) parts.Add(e.Message.Trim());
        return string.Join(" → ", parts);
    }

    public static int? SeverityLevel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        int i = Array.FindIndex(SeverityNames, s => s.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        return i < 0 ? null : i;
    }

    /// <summary>"code 4110 AUTH_DISABLED: Authentication is disabled …" from an "error" object; null when none.</summary>
    public static string? ErrorOf(JsonNode? n)
    {
        if (n?["error"] is not JsonObject e) return null;
        string code = e["code"]?.ToString() ?? "?";
        string text = string.Join(": ", new[] { e["message"]?.ToString(), e["reason"]?.ToString() }.Where(s => !string.IsNullOrEmpty(s)));
        return text.Length == 0 ? $"code {code}" : $"code {code} {text}";
    }

    // Framework 14.6 Server.Events.d.ts: Type { Message = 0, Alarm = 1, Payload = 2 }, Severity { Verbose … Critical = 4 },
    // AlarmState { Raised = 0, Confirmed = 1, Cleared = 2, ClearedAndConfirmed = 3, Invalid = 4 }.
    internal static readonly string[] AlarmStateNames = ["Raised", "Confirmed", "Cleared", "ClearedAndConfirmed", "Invalid"];

    internal static string? AlarmStateName(JsonNode? state) =>
        state is JsonValue v && v.TryGetValue(out double d) && d >= 0 && d < AlarmStateNames.Length && d == Math.Floor(d)
            ? AlarmStateNames[(int)d]
            : state?.ToString();

    /// <summary>An alarm whose cause is still there (Raised or Confirmed) — Cleared waits only for a confirmation.</summary>
    internal static bool IsActiveAlarm(HmiServerEvent e) => e.Alarm && e.AlarmState is "Raised" or "Confirmed";

    public static IReadOnlyList<HmiServerEvent> Events(JsonNode? listEvents)
    {
        var list = new List<HmiServerEvent>();
        if (listEvents is not JsonArray arr) return list;
        foreach (var e in arr)
        {
            if (e is not JsonObject o) continue;
            JsonNode? p = o["payload"];
            int sev = p?["severity"] is JsonValue sv && sv.TryGetValue(out double s) ? (int)s : 1;     // numbers may come as 1.0
            bool alarm = o["payloadType"] is JsonValue pt && pt.TryGetValue(out double t) && t == 1;
            string time = p?["timeRaised"]?.ToString() ?? o["timeReceived"]?.ToString() ?? "";
            list.Add(new HmiServerEvent(time, sev >= 0 && sev < SeverityNames.Length ? SeverityNames[sev] : sev.ToString(CultureInfo.InvariantCulture),
                o["domain"]?.ToString() ?? p?["domain"]?.ToString() ?? "", o["name"]?.ToString() ?? p?["name"]?.ToString() ?? "",
                o["localizedString"]?.ToString() ?? p?["text"]?.ToString() ?? "", alarm, alarm ? AlarmStateName(p?["alarmState"]) : null));
        }
        return list;
    }

    /// <summary>
    /// The newest events for the summary line: equal ones (severity, domain, text) once with "(×n)", in order of their newest
    /// occurrence, texts cut before a native location ("[system:…") and to 160 characters — a server that failed the same
    /// way on every start would otherwise fill the line with one stack text.
    /// </summary>
    internal static IEnumerable<string> NewestGrouped(IReadOnlyList<HmiServerEvent> newestFirst, int max) =>
        newestFirst.GroupBy(e => (e.Severity, e.Domain, Text: ShortText(e.Text)))
            .Take(max)
            .Select(g => $"{g.Key.Severity} {g.Key.Domain}: {g.Key.Text}" + (g.Count() > 1 ? $" (×{g.Count()})" : ""));

    private static readonly System.Text.RegularExpressions.Regex SessionId =
        new(@"\s*\(Session ID [^)]*\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Event text for the summary: without session id and native location, at most 160 characters.</summary>
    internal static string ShortText(string text)
    {
        int native = text.IndexOf(" [system:", StringComparison.Ordinal);
        string t = SessionId.Replace(native > 0 ? text[..native] : text, "");
        return t.Length <= 160 ? t : t[..159] + "…";
    }

    /// <summary>Builds the diagnostics report from the answer to <see cref="DiagnosticsCommands"/>.</summary>
    public static (string Summary, object Json) Diagnose(JsonObject answer, string endpoint, string? note, int minSeverity, int maxEvents)
    {
        var byName = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var denied = new List<string>();
        if (answer["commands"] is JsonArray cmds)
        {
            foreach (var c in cmds)
            {
                string sym = c?["symbol"]?.ToString() ?? "";
                if (ErrorOf(c) is { } err) denied.Add($"{sym} ({err})");
                else byName[sym] = c?["readValue"];
            }
        }

        JsonNode? info = byName.GetValueOrDefault("GetServerInformation");
        JsonNode? diag = byName.GetValueOrDefault("Diagnostics");
        JsonNode? license = diag?["LICENSE"];
        var extensions = (byName.GetValueOrDefault("ListDomains") as JsonObject)?
            .Select(kv => new
            {
                domain = kv.Key,
                extension = kv.Value?["extension"]?.ToString(),
                state = kv.Value?["state"]?.ToString(),
                licensed = kv.Value?["licensed"] is JsonValue lv && lv.TryGetValue(out bool lb) ? lb : (bool?)null,
                version = kv.Value?["version"]?.ToString(),
            }).ToList();
        var outdated = (byName.GetValueOrDefault("ListOutdatedSymbols") as JsonObject)?.Select(kv => $"{kv.Key} ({kv.Value})").ToList() ?? [];
        int sessions = (byName.GetValueOrDefault("ListActiveSessions")?["clients"] as JsonObject)?
            .Sum(kv => (kv.Value as JsonArray)?.Count ?? 0) ?? 0;
        string? user = byName.GetValueOrDefault("GetCurrentUser")?["name"]?.ToString();

        var events = Events(byName.GetValueOrDefault("ListEvents"));
        var counts = SeverityNames.ToDictionary(s => s, s => events.Count(e => e.Severity == s));
        var filtered = events.Where(e => SeverityLevel(e.Severity) is int l && l >= minSeverity)
            .OrderByDescending(e => e.Time, StringComparer.Ordinal).ToList();
        var shown = filtered.Take(Math.Max(0, maxEvents)).ToList();
        var activeAlarms = events.Where(IsActiveAlarm).ToList();
        int unconfirmed = events.Count(e => e.Alarm && e.AlarmState is "Raised" or "Cleared");

        // A user without rights (e.g. __SystemGuest on a runtime that requires login) gets ListDomains without states and
        // no GetServerInformation/Diagnostics: report "not readable", never "not running" or a license problem.
        string version = info?["productVersion"]?.ToString() ?? diag?["VERSION"]?.ToString() ?? "";
        string kind = info?["creatorMode"] is JsonValue cm && cm.TryGetValue(out bool cb)
            ? (cb ? "engineering server" : "runtime server")
            : "server type not readable";
        string? licenseState = license?["STATE"]?.ToString();
        var notRunning = extensions?.Where(x => x.state is not null && !string.Equals(x.state, "Initialized", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        bool statesHidden = extensions is { Count: > 0 } && extensions.All(x => x.state is null);
        var unlicensed = extensions?.Where(x => x.licensed == false).Select(x => x.domain).ToList() ?? [];

        var sb = new StringBuilder();
        bool warn = licenseState is not (null or "OK" or "Engineering") || notRunning.Count > 0 || unlicensed.Count > 0 || counts["error"] + counts["critical"] > 0;
        if (warn) sb.Append("[Warn] ");
        sb.Append("HMI server");
        if (version.Length > 0) sb.Append(' ').Append(version);
        if (info?["projectName"]?.ToString() is { Length: > 0 } project) sb.Append(CultureInfo.InvariantCulture, $" '{project}'");
        sb.Append(CultureInfo.InvariantCulture, $" at {endpoint} ({kind}");
        if (user is not null) sb.Append(CultureInfo.InvariantCulture, $", as {user}");
        sb.Append("): license ").Append(licenseState ?? "not readable");
        if (license?["CLIENTS"] is not null)
            sb.Append(CultureInfo.InvariantCulture, $" (clients {license["USEDCLIENTS"]}/{license["CLIENTS"]}, targets {license["USEDTARGETS"]}/{license["TARGETS"]})");
        if (extensions is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"; {extensions.Count} extension(s)");
            sb.Append(statesHidden ? ", states not readable"
                : notRunning.Count == 0 ? ", all Initialized"
                : ", not running: " + string.Join(", ", notRunning.Select(x => $"{x.domain} ({x.state})")));
        }
        if (unlicensed.Count > 0) sb.Append("; unlicensed: ").Append(string.Join(", ", unlicensed));
        sb.Append(CultureInfo.InvariantCulture, $"; {sessions} session(s)");
        if (outdated.Count > 0) sb.Append(CultureInfo.InvariantCulture, $"; {outdated.Count} outdated symbol(s): {string.Join(", ", outdated.Take(3))}");
        sb.Append(CultureInfo.InvariantCulture, $"; events: {counts["critical"]} critical, {counts["error"]} error(s), {counts["warning"]} warning(s)");
        if (activeAlarms.Count > 0) sb.Append(CultureInfo.InvariantCulture, $", {activeAlarms.Count} active alarm(s)");
        if (unconfirmed > 0) sb.Append(CultureInfo.InvariantCulture, $", {unconfirmed} unconfirmed");
        if (shown.Count > 0) sb.Append(". Newest: ").Append(string.Join(" | ", NewestGrouped(filtered, 3)));
        sb.Append('.');
        if (denied.Count > 0) sb.Append(" Not readable for this user: ").Append(string.Join(", ", denied)).Append('.');
        if (info is null && diag is null && denied.Count > 0)
            sb.Append(" Log in with user and passwordVariable for the server details.");
        if (note is not null) sb.Append(' ').Append(note);

        return (sb.ToString(), new
        {
            endpoint,
            server = info,
            license,
            uptime = diag?["UPTIME"]?.ToString(),
            memoryUsageMb = diag?["MEMORYUSAGE"]?.ToString(),
            extensions,
            outdatedSymbols = outdated,
            sessions,
            currentUser = user,
            eventCounts = counts,
            activeAlarms,
            unconfirmedAlarms = unconfirmed,
            events = shown,
            notReadable = denied,
        });
    }
}
