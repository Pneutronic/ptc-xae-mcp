using System.Text.Json.Nodes;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>hmi-tf2000: group rights for bound symbols (offline from the saved server configuration) and the
/// diagnostics report of the HMI server WebSocket API. The response shapes are the ones a 24.0.7 server sent.</summary>
public sealed class HmiTf2000Tests : IDisposable
{
    private const string Prj = "PlantHmi";
    private readonly string _dir = Directory.CreateTempSubdirectory("hmi-tf2000-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ── sample server configuration ───────────────────────────────────────────

    private const string ServerConfig = """
        {
          "REQUIREAUTH": 1,
          "USERGROUPS": {
            "__SystemAdministrators": { "ENABLED": true, "FILEACCESS": 3, "FILES": {}, "SYMBOLACCESS": 3, "SYMBOLS": {} },
            "__SystemGuests": { "ENABLED": true, "FILEACCESS": 0, "FILES": {}, "SYMBOLACCESS": 0, "SYMBOLS": { "Login": 3 } },
            "__SystemUsers": { "ENABLED": true, "FILEACCESS": 3, "FILES": {}, "SYMBOLACCESS": 0, "SYMBOLS": { "PLC1.MAIN.nSpeed": 1 } },
            "Operators": { "ENABLED": true, "FILEACCESS": 3, "FILES": {}, "SYMBOLACCESS": 1,
                           "SYMBOLS": { "PLC1.MAIN.bStart": 3, "PLC1.MAIN.stAxis::fPos": 0, "PLC1.MAIN.stAxis": 3 } },
            "Service": { "ENABLED": true, "FILEACCESS": 3, "FILES": {}, "SYMBOLACCESS": 3.0, "SYMBOLS": {} },
            "Old": { "ENABLED": false, "FILEACCESS": 3, "FILES": {}, "SYMBOLACCESS": 0, "SYMBOLS": {} }
          },
          "USERGROUPUSERS": {
            "TcHmiUserManagement": {
              "__SystemAdministrator": { "USERGROUPUSERS_GROUPS": ["__SystemAdministrators"] },
              "__SystemUser": { "USERGROUPUSERS_GROUPS": ["__SystemUsers"] },
              "op1": { "USERGROUPUSERS_GROUPS": ["Operators"] },
              "old1": { "USERGROUPUSERS_GROUPS": ["Old"] }
            }
          }
        }
        """;

    private const string UserManagement = """
        { "USERS": {
            "__SystemUser": { "ENABLED": true, "PASSWORD": "" },
            "op1": { "ENABLED": true, "PASSWORD": "" },
            "old1": { "ENABLED": false, "PASSWORD": "abc" } } }
        """;

    private const string Page = """
        <div id="Desktop" data-tchmi-type="TcHmi.Controls.System.TcHmiView">
          <div id="Speed" data-tchmi-type="TcHmi.Controls.Beckhoff.TcHmiTextblock" data-tchmi-text="%s%PLC1.MAIN.nSpeed%/s%"></div>
          <div id="Set" data-tchmi-type="TcHmi.Controls.Beckhoff.TcHmiNumericInput" data-tchmi-value="%s%PLC1.MAIN.nSet|BindingMode=TwoWay%/s%"></div>
          <div id="Start" data-tchmi-type="TcHmi.Controls.Beckhoff.TcHmiButton" data-tchmi-state-symbol="%s%PLC1.MAIN.bStart%/s%">
            <script data-tchmi-target-attribute="data-tchmi-trigger" type="application/json">
            [{ "event": "Start.onPressed", "actions": [
               { "objectType": "WriteToSymbol", "symbolExpression": "%s%PLC1.MAIN.bReset%/s%", "valueSource": true },
               { "objectType": "Condition", "if": [{ "compare1": "%s%PLC1.MAIN.nMode%/s%" }] } ] }]
            </script>
          </div>
          <div id="Pos" data-tchmi-type="TcHmi.Controls.Beckhoff.TcHmiTextblock" data-tchmi-text="%s%PLC1.MAIN.stAxis::fPos%/s%"></div>
          <div id="Vel" data-tchmi-type="TcHmi.Controls.Beckhoff.TcHmiTextblock" data-tchmi-text="%s%PLC1.MAIN.stAxis::fVel%/s%"></div>
          <div id="Arr" data-tchmi-type="TcHmi.Controls.Beckhoff.TcHmiTextblock" data-tchmi-text="%s%PLC1.MAIN.aVals[%i%Idx%/i%]%/s%"></div>
          <div id="Speed2" data-tchmi-type="TcHmi.Controls.Beckhoff.TcHmiTextblock" data-tchmi-text="%s%PLC1.MAIN.nSpeed%/s%"></div>
        </div>
        """;

    private static HmiAccessModel Model(string? remote = null) =>
        HmiAccessCheck.BuildModel(JsonNode.Parse(ServerConfig), remote is null ? null : JsonNode.Parse(remote), JsonNode.Parse(UserManagement));

    // ── model ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Model_ReadsGroupsMembersUsersAndAuth()
    {
        var m = Model("{\"REQUIREAUTH\": 2}");
        Assert.Equal(6, m.Groups.Count);
        Assert.Equal(["op1"], m.Group("operators")!.Users);
        Assert.False(m.Group("Old")!.Enabled);
        Assert.Equal((1, 2, 2), (m.RequireAuthDefault!.Value, m.RequireAuthRemote!.Value, m.RequireAuthOnTarget!.Value));
        var op1 = Assert.Single(m.Users, u => u.Name == "op1");
        Assert.False(op1.PasswordSet);
        Assert.Equal(["Operators"], op1.Groups);
        Assert.True(Assert.Single(m.Users, u => u.Name == "old1").PasswordSet);
    }

    [Theory]
    [InlineData("PLC1.MAIN.bStart", 3, "entry 'PLC1.MAIN.bStart'")]
    [InlineData("PLC1.MAIN.nSpeed", 1, "SYMBOLACCESS default")]
    [InlineData("PLC1.MAIN.stAxis::fPos", 0, "entry 'PLC1.MAIN.stAxis::fPos'")]      // explicit entry wins, also downwards
    [InlineData("PLC1.MAIN.stAxis::fVel", 3, "entry 'PLC1.MAIN.stAxis'")]            // most specific parent entry
    [InlineData("PLC1.MAIN.stAxis::fVel::x", 3, "entry 'PLC1.MAIN.stAxis'")]
    public void Effective_ExplicitEntryMostSpecificFirst_ElseGroupDefault(string symbol, int access, string source)
    {
        var (a, s) = HmiAccessModel.Effective(Model().Group("Operators")!, symbol);
        Assert.Equal((access, source), (a, s));
    }

    [Fact]
    public void DefaultGroups_AreEnabledGroupsWithUsers_WithoutAdministratorsAndGuests()
    {
        Assert.Equal(["__SystemUsers", "Operators"], HmiAccessCheck.DefaultGroups(Model()).Select(g => g.Name));
    }

    // ── uses ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Uses_KnowWhatThePageWrites()
    {
        var uses = HmiAccessCheck.Uses("Desktop.view", Page.ReplaceLineEndings("\n"));
        string W(string symbol) => string.Join(",", uses.Where(u => u.Symbol == symbol).Select(u => u.Writes ? "w:" + u.Why : "r"));

        Assert.Equal("r,r", W("PLC1.MAIN.nSpeed"));
        Assert.Equal("w:TwoWay binding", W("PLC1.MAIN.nSet"));
        Assert.Equal("w:writing attribute", W("PLC1.MAIN.bStart"));
        Assert.Equal("w:WriteToSymbol action", W("PLC1.MAIN.bReset"));
        Assert.Equal("r", W("PLC1.MAIN.nMode"));
        Assert.Equal("r", W("PLC1.MAIN.stAxis::fPos"));
        Assert.Equal("r", W("PLC1.MAIN.aVals"));                       // index cut
        Assert.Equal(3, uses.Single(u => u.Symbol == "PLC1.MAIN.nSet").Line);
        Assert.Equal("Start", uses.Single(u => u.Symbol == "PLC1.MAIN.bReset").ControlId);
    }

    [Theory]
    [InlineData("PLC1.MAIN.x|BindingMode=TwoWay", "PLC1.MAIN.x")]
    [InlineData(" PLC1.MAIN.a[3]::b ", "PLC1.MAIN.a")]
    [InlineData("PLC1.MAIN.st::b|EventOnlyOnChange", "PLC1.MAIN.st::b")]
    public void SymbolPath_CutsOptionsAndIndexes_KeepsSubSymbols(string body, string path) =>
        Assert.Equal(path, HmiAccessCheck.SymbolPath(body));

    // ── check ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Check_ReportsUnreadableAndUnwritableSymbolsPerGroup_WithLinesAndControls()
    {
        var m = Model("{\"REQUIREAUTH\": 0}");
        var pages = new Dictionary<string, string> { ["Desktop.view"] = Page.ReplaceLineEndings("\n") };
        var r = HmiAccessCheck.Check(pages, m, HmiAccessCheck.DefaultGroups(m));

        Assert.Equal(["__SystemUsers", "Operators"], r.GroupsChecked);
        HmiAccessFinding F(string g, string sym) => Assert.Single(r.Findings, f => f.Group == g && f.Symbol == sym);

        // __SystemUsers: default 0, only nSpeed readable (twice, one finding with both lines)
        Assert.DoesNotContain(r.Findings, f => f.Group == "__SystemUsers" && f.Symbol == "PLC1.MAIN.nSpeed");
        Assert.Equal("read", F("__SystemUsers", "PLC1.MAIN.nSet").Problem);

        // Operators: default 1 → TwoWay nSet and the WriteToSymbol target bReset cannot be written
        var nSet = F("Operators", "PLC1.MAIN.nSet");
        Assert.Equal(("write", 1, "SYMBOLACCESS default"), (nSet.Problem, nSet.Access, nSet.AccessSource));
        Assert.Contains("TwoWay binding writes it", nSet.Message, StringComparison.Ordinal);
        Assert.Equal("write", F("Operators", "PLC1.MAIN.bReset").Problem);
        Assert.DoesNotContain(r.Findings, f => f.Group == "Operators" && f.Symbol == "PLC1.MAIN.bStart");   // explicit 3
        var pos = F("Operators", "PLC1.MAIN.stAxis::fPos");
        Assert.Equal(("read", 0), (pos.Problem, pos.Access));
        Assert.DoesNotContain(r.Findings, f => f.Group == "Operators" && f.Symbol == "PLC1.MAIN.stAxis::fVel");

        Assert.Contains(r.Notes, n => n.StartsWith("REQUIREAUTH is 0", StringComparison.Ordinal) && n.Contains(".remote", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.Contains("group(s) without users in the project: Service", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.Contains("without a password in the project: op1", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_SameSymbolOnManyRows_IsOneFindingWithAllLines()
    {
        var m = Model();
        string page = Page.ReplaceLineEndings("\n");
        var r = HmiAccessCheck.Check(new Dictionary<string, string> { ["Desktop.view"] = page }, m, [m.Group("Old")!]);
        var speed = Assert.Single(r.Findings, f => f.Symbol == "PLC1.MAIN.nSpeed");
        Assert.Equal(2, speed.Count);
        Assert.Equal([2, 14], speed.Lines);
        Assert.Equal(["Speed", "Speed2"], speed.Controls);
    }

    // ── coordinator ───────────────────────────────────────────────────────────

    private FakeTwinCatSession Project()
    {
        void Write(string rel, string text)
        {
            string p = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        Write("tchmiconfig.json", "{ \"startupView\": \"Desktop.view\" }");
        Write("Desktop.view", Page);
        Write("Server/TcHmiSrv/TcHmiSrv.Config.default.json", ServerConfig);
        Write("Server/TcHmiSrv/TcHmiSrv.Config.remote.json", "{ \"REQUIREAUTH\": 2 }");
        Write("Server/TcHmiUserManagement/TcHmiUserManagement.Config.default.json", UserManagement);
        return new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1, [new HmiProjectEntry(Prj, _dir, true, false)], null),
        };
    }

    [Fact]
    public void Coordinator_CheckAccess_ReadsTheSavedServerConfiguration()
    {
        var f = Project();
        var (summary, json) = new HmiCoordinator(f).CheckAccess(Prj, null, null);
        Assert.Contains("for group(s) __SystemUsers, Operators", summary, StringComparison.Ordinal);
        Assert.Contains("finding(s) 'cannot read'", summary, StringComparison.Ordinal);
        Assert.Contains("REQUIREAUTH 2 for the published server", summary, StringComparison.Ordinal);
        Assert.Contains("\"onTarget\": 2", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0027", json, StringComparison.Ordinal);

        var (one, _) = new HmiCoordinator(f).CheckAccess(Prj, ["Service"], ["Desktop.view"]);
        Assert.Contains("every group can read every bound symbol", one, StringComparison.Ordinal);

        Assert.StartsWith("[Error] 'Nobody' is not a user group", new HmiCoordinator(f).CheckAccess(Prj, ["Nobody"], null).Summary, StringComparison.Ordinal);
        Assert.StartsWith("[Error] 'X.view' is not a page", new HmiCoordinator(f).CheckAccess(Prj, null, ["X.view"]).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_EngineeringEndpoint_IsTheServerPortOfTheProject()
    {
        var f = Project();
        File.WriteAllText(Path.Combine(_dir, "PlantHmi.hmiproj"), "<Project><PropertyGroup><HmiCommunicationServerPort>3017</HmiCommunicationServerPort></PropertyGroup></Project>");
        Assert.Equal(("http://127.0.0.1:3017", null), new HmiCoordinator(f).EngineeringEndpoint(Prj));
    }

    // ── server API ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://127.0.0.1:3000", "ws://127.0.0.1:3000/", "http://127.0.0.1:3000/")]
    [InlineData("https://192.168.1.10:2020/Config", "wss://192.168.1.10:2020/", "https://192.168.1.10:2020/")]
    [InlineData("wss://ipc:2020", "wss://ipc:2020/", "https://ipc:2020/")]
    public void ParseEndpoint_MapsHttpToWebSocket(string endpoint, string ws, string http)
    {
        var (w, h, e) = HmiServerApiClient.ParseEndpoint(endpoint);
        Assert.Null(e);
        Assert.Equal((ws, http), (w!.AbsoluteUri, h!.AbsoluteUri));
    }

    [Fact]
    public void IsAnswerTo_AcceptsTheServersDoubleIds()
    {
        Assert.True(HmiServerApiClient.IsAnswerTo(JsonNode.Parse("{\"apiVersion\":24.0,\"id\":1.0}")!.AsObject(), 1));
        Assert.True(HmiServerApiClient.IsAnswerTo(JsonNode.Parse("{\"id\":2}")!.AsObject(), 2));
        Assert.False(HmiServerApiClient.IsAnswerTo(JsonNode.Parse("{\"id\":0.0}")!.AsObject(), 1));
        Assert.False(HmiServerApiClient.IsAnswerTo(JsonNode.Parse("{\"commands\":[]}")!.AsObject(), 1));
    }

    [Theory]
    [InlineData("127.0.0.1:3000")]
    [InlineData("ftp://x:21")]
    public void ParseEndpoint_RefusesOtherSchemes(string endpoint) => Assert.NotNull(HmiServerApiClient.ParseEndpoint(endpoint).Error);

    private const string DiagnosticsAnswer = """
        { "id": 1, "commands": [
          { "symbol": "GetServerInformation", "readValue": { "productVersion": "24.0.7", "projectName": "PlantHmi", "creatorMode": false } },
          { "symbol": "Diagnostics", "readValue": { "UPTIME": "PT1H", "MEMORYUSAGE": 51.5,
              "LICENSE": { "CLIENTS": 5, "USEDCLIENTS": 2, "TARGETS": 1, "USEDTARGETS": 1, "EXTENSIONS": true, "STATE": "OK" } } },
          { "symbol": "ListDomains", "readValue": {
              "ADS": { "extension": "TcHmiAds", "state": "Initialized", "licensed": true, "version": "24.0.7.0" },
              "TcHmiEventLogger": { "extension": "TcHmiEventLogger", "state": "Error", "licensed": false, "version": "24.0.7.0" } } },
          { "symbol": "ListOutdatedSymbols", "readValue": { "TcHmiUserManagement.Get2FAStatus": "SYMBOL_SCHEMA_DIFFERENCE" } },
          { "symbol": "ListActiveSessions", "readValue": { "clients": { "127.0.0.1": [{}, {}], "10.0.0.5": [{}] } } },
          { "symbol": "GetCurrentUser", "readValue": { "name": "op1" } },
          { "symbol": "ListEvents", "readValue": [
              { "domain": "TcHmiSrv", "name": "START_SRV", "localizedString": "Starting server", "payloadType": 0,
                "payload": { "severity": 1, "timeRaised": "2026-11-18T22:48:00Z" } },
              { "domain": "ADS", "name": "RT", "localizedString": "Error while checking runtime PLC1", "payloadType": 0,
                "payload": { "severity": 3.0, "timeRaised": "2026-11-18T22:49:00Z" } },
              { "domain": "TcHmiEventLogger", "name": "A1", "localizedString": "Pressure low", "payloadType": 1,
                "payload": { "severity": 2, "timeRaised": "2026-11-18T22:50:00Z", "alarmState": 0.0 } },
              { "domain": "TcHmiSrv", "name": "CONFIGURATION_HINT", "localizedString": "Endpoints from startup parameters", "payloadType": 1.0,
                "payload": { "severity": 1, "timeRaised": "2026-11-18T22:47:00Z", "alarmState": 2 } } ] }
        ] }
        """;

    [Fact]
    public void Diagnose_SummarisesLicenseExtensionsSessionsAndNewestEvents()
    {
        var (summary, json) = HmiServerApi.Diagnose(JsonNode.Parse(DiagnosticsAnswer)!.AsObject(), "https://ipc:2020", null, 2, 20);
        Assert.StartsWith("[Warn] HMI server 24.0.7 'PlantHmi' at https://ipc:2020 (runtime server, as op1): license OK (clients 2/5, targets 1/1)", summary, StringComparison.Ordinal);
        Assert.Contains("2 extension(s), not running: TcHmiEventLogger (Error); unlicensed: TcHmiEventLogger", summary, StringComparison.Ordinal);
        Assert.Contains("3 session(s)", summary, StringComparison.Ordinal);
        Assert.Contains("1 outdated symbol(s)", summary, StringComparison.Ordinal);
        Assert.Contains("events: 0 critical, 1 error(s), 1 warning(s), 1 active alarm(s), 2 unconfirmed", summary, StringComparison.Ordinal);
        string jsonText = System.Text.Json.JsonSerializer.Serialize(json);
        Assert.Contains("\"AlarmState\":\"Raised\"", jsonText, StringComparison.Ordinal);
        Assert.DoesNotContain("CONFIGURATION_HINT\",\"Text\":\"Endpoints from startup parameters\",\"Alarm\":true,\"AlarmState\":\"Raised", jsonText, StringComparison.Ordinal);
        Assert.Contains("Newest: warning TcHmiEventLogger: Pressure low | error ADS: Error while checking runtime PLC1.", summary, StringComparison.Ordinal);
        string text = System.Text.Json.JsonSerializer.Serialize(json);
        Assert.DoesNotContain("Starting server", text, StringComparison.Ordinal);          // info below minSeverity
    }

    [Fact]
    public void Diagnose_DeniedCommandsAreNamed_AndAnEngineeringServerIsNoWarning()
    {
        var answer = JsonNode.Parse("""
            { "commands": [
              { "symbol": "GetServerInformation", "readValue": { "productVersion": "24.0.7", "projectName": "P", "creatorMode": true } },
              { "symbol": "Diagnostics", "readValue": { "LICENSE": { "STATE": "Engineering" } } },
              { "symbol": "ListActiveSessions", "error": { "code": 4101, "message": "ACCESS_DENIED" } } ] }
            """)!.AsObject();
        var (summary, _) = HmiServerApi.Diagnose(answer, "http://127.0.0.1:3000", "note.", 2, 5);
        Assert.StartsWith("HMI server 24.0.7 'P' at http://127.0.0.1:3000 (engineering server): license Engineering", summary, StringComparison.Ordinal);
        Assert.Contains("Not readable for this user: ListActiveSessions (code 4101 ACCESS_DENIED).", summary, StringComparison.Ordinal);
        Assert.EndsWith(" note.", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_AGuestOnARuntime_SaysNotReadable_NeverNotRunning()
    {
        // what a licensed TF2000 runtime with REQUIREAUTH answers __SystemGuest (live, server 24.0.7)
        var answer = JsonNode.Parse("""
            { "commands": [
              { "symbol": "GetServerInformation", "error": { "code": 4101, "message": "INSUFFICIENT_ACCESS" } },
              { "symbol": "Diagnostics", "error": { "code": 4101, "message": "INSUFFICIENT_ACCESS" } },
              { "symbol": "ListDomains", "readValue": {
                  "ADS": { "extension": "TcHmiAds", "version": "24.0.7.0" },
                  "TcHmiSrv": { "extension": "TcHmiSrv", "version": "24.0.7.0" } } },
              { "symbol": "GetCurrentUser", "readValue": { "name": "__SystemGuest" } } ] }
            """)!.AsObject();
        var (summary, _) = HmiServerApi.Diagnose(answer, "https://127.0.0.1:2020", null, 2, 5);
        Assert.StartsWith("HMI server at https://127.0.0.1:2020 (server type not readable, as __SystemGuest): license not readable; " +
                          "2 extension(s), states not readable", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("not running", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("[Warn]", summary, StringComparison.Ordinal);
        Assert.EndsWith("Log in with user and passwordVariable for the server details.", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_RepeatedEventsAreGroupedAndNativeLocationsCut()
    {
        // a TF2000 service that failed to bind an endpoint on several starts (live, server 24.0.7)
        const string bind = "Error: bind: Die angeforderte Adresse ist in diesem Kontext ungültig [system:10049 at C:\\\\agent\\\\boost/asio/detail/win_iocp_socket_service.hpp:243:5]";
        var answer = JsonNode.Parse($$"""
            { "commands": [
              { "symbol": "GetServerInformation", "readValue": { "productVersion": "24.0.7", "creatorMode": false } },
              { "symbol": "ListEvents", "readValue": [
                  { "domain": "TcHmiSrv", "name": "ERROR", "localizedString": "{{bind}}", "payloadType": 0, "payload": { "severity": 3, "timeRaised": "2026-11-03T07:10:15Z" } },
                  { "domain": "TcHmiSrv", "name": "ERROR", "localizedString": "{{bind}}", "payloadType": 0, "payload": { "severity": 3, "timeRaised": "2026-10-30T09:59:07Z" } },
                  { "domain": "ADS", "name": "RT", "localizedString": "Runtime not reachable", "payloadType": 0, "payload": { "severity": 3, "timeRaised": "2026-11-01T08:00:00Z" } },
                  { "domain": "TcHmiSrv", "name": "ERROR", "localizedString": "{{bind}}", "payloadType": 0, "payload": { "severity": 3, "timeRaised": "2026-10-09T18:27:15Z" } } ] } ] }
            """)!.AsObject();
        var (summary, json) = HmiServerApi.Diagnose(answer, "http://127.0.0.1:1020", null, 3, 2);
        Assert.Contains("Newest: error TcHmiSrv: Error: bind: Die angeforderte Adresse ist in diesem Kontext ungültig (×3) | error ADS: Runtime not reachable.",
            summary, StringComparison.Ordinal);
        Assert.DoesNotContain("[system:", summary, StringComparison.Ordinal);
        Assert.Contains("[system:10049", System.Text.Json.JsonSerializer.Serialize(json), StringComparison.Ordinal);   // the JSON keeps the full text
        Assert.Equal(new string('x', 159) + "…", HmiServerApi.ShortText(new string('x', 400)));
        Assert.Equal("Login failed for user TcHmiUserManagement::op: Authentication failed (HMI_E_AUTH_FAILED)",
            HmiServerApi.ShortText("Login failed for user TcHmiUserManagement::op: Authentication failed (HMI_E_AUTH_FAILED) (Session ID 994b2fa6...)"));
    }

    [Fact]
    public void CertificateError_IsFoundThroughTheWrappedChain_AndAllMessagesAreShown()
    {
        var tls = new System.Security.Authentication.AuthenticationException("The remote certificate is invalid according to the validation procedure.");
        var http = new System.Net.Http.HttpRequestException("The SSL connection could not be established, see inner exception.", tls);
        var ws = new System.Net.WebSockets.WebSocketException("Unable to connect to the remote server", http);
        Assert.True(HmiServerApi.IsCertificateError(ws));
        Assert.False(HmiServerApi.IsCertificateError(new System.Net.WebSockets.WebSocketException("Unable to connect to the remote server")));
        Assert.Equal("Unable to connect to the remote server → The SSL connection could not be established, see inner exception. → " +
                     "The remote certificate is invalid according to the validation procedure.", HmiServerApi.Messages(ws));
    }

    [Fact]
    public async Task RunDiagnostics_NothingListening_IsAnErrorNotAHang()
    {
        var (summary, _) = await HmiServerApi.RunDiagnosticsAsync("http://127.0.0.1:1", null, null, false, 2, 5, 5000, null,
            HmiCoordinator.ToolJsonOptions, CancellationToken.None);
        Assert.StartsWith("[Error] cannot talk to the HMI server at http://127.0.0.1:1/", summary, StringComparison.Ordinal);
        Assert.StartsWith("[Error] endpoint", (await HmiServerApi.RunDiagnosticsAsync("nope", null, null, false, 2, 5, 5000, null,
            HmiCoordinator.ToolJsonOptions, CancellationToken.None)).Summary, StringComparison.Ordinal);
    }
}
