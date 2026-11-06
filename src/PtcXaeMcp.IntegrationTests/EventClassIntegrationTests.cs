using System.Text.Json;
using System.Xml.Linq;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// The event-class tools, live against the neutral fixture: a time-stamped event class is created, read, used by PLC code
/// (TC_EVENTS), changed, pinned into the PLC project (ExternalTypes.tmc + .plcproj entry, ExternalTypes.tmc node in the tree)
/// and changed again while pinned (the pin is written again). The name is unique per run: the XAE keeps the PLC's global
/// types for the life of the process, so a reused name could pass on a stale definition. Cleanup removes the scratch
/// program, the class (DeleteType through the DTE) and the pin files, with a close and reopen of the fixture.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class EventClassIntegrationTests : IAsyncLifetime
{
    private McpClient? _client;
    private bool _ready;
    private string _fixtureSln = string.Empty;

    public async Task InitializeAsync()
    {
        var fixture = LiveXae.FixtureSln();
        if (fixture is null) { LiveXae.Announce(false, string.Empty); return; }
        _fixtureSln = fixture;
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(LiveXae.ServerUrl),
                ConnectionTimeout = TimeSpan.FromSeconds(5),
            });
            _client = await McpClient.CreateAsync(transport,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            _ready = true;
        }
        catch { _ready = false; }
        LiveXae.Announce(_ready, _fixtureSln);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }

    /// <param name="expectError">true: must be an error; false: must not; null: either (cleanup).</param>
    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args, bool? expectError = false)
    {
        var result = await _client!.CallToolAsync(tool, args);
        string text = string.Join("\n", (result.Content ?? []).OfType<TextContentBlock>().Select(c => c.Text));
        Console.WriteLine($"[event_class] {tool}: {text}");
        if (expectError == true)
            Assert.StartsWith("[Error]", text);
        else if (expectError == false)
            Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal) || result.IsError == true, $"{tool} failed: {text}");
        return text;
    }

    private static Dictionary<string, object?> Ev(int id, string name, string? severity = null, string? text = null) =>
        new() { ["id"] = id, ["name"] = name, ["severity"] = severity, ["text"] = text };

    [Trait("Description", "Event-Klasse anlegen (persistent), lesen, TC_EVENTS im SPS-Code (Check all objects), ändern (neues Event sofort im SPS-Code), per Add Global Data Type ins SPS-Projekt pinnen (ExternalTypes.tmc + .plcproj, Baumknoten), gepinnt erneut ändern → Pin neu geschrieben; Aufräumen mit Schliessen/Öffnen.")]
    [SkippableFact(Timeout = 900_000)]
    public async Task CreateReadUpdatePin_EventClass()
    {
        LiveXae.GuardReady(_ready);

        await CallAsync("twincat_open_project", new() { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        await PlcProjectReadiness.AwaitAsync(_client!);

        string stamp = DateTimeOffset.UtcNow.ToString("HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        string cls = $"EvIt_{stamp}";
        string prg = $"PRG_EvIt_{stamp}";
        string prgPath = $"{FixtureLayout.PousFolder}^{prg}";
        string plcproj = Directory.EnumerateFiles(Path.GetDirectoryName(_fixtureSln)!, "*.plcproj", SearchOption.AllDirectories).Single();
        string externalTypes = Path.Combine(Path.GetDirectoryName(plcproj)!, "ExternalTypes.tmc");
        Assert.False(File.Exists(externalTypes), "the fixture copy must start without ExternalTypes.tmc");

        // Refusals change nothing.
        string text = await CallAsync("twincat_event_class_create",
            new() { ["name"] = cls, ["events"] = new[] { Ev(1, "A"), Ev(1, "B") } }, expectError: true);
        Assert.Contains("id 1 is used twice", text);
        text = await CallAsync("twincat_event_class_read", new() { ["name"] = cls }, expectError: true);
        Assert.Contains($"No event class '{cls}'", text);

        bool classCreated = false, prgCreated = false;
        try
        {
            // ---- create + read ----
            text = await CallAsync("twincat_event_class_create", new()
            {
                ["name"] = cls,
                ["displayText"] = "Integration events",
                ["events"] = new[] { Ev(1, "Started", "Info", "Started {0}"), Ev(2, "Failed", "Error", "Failed") },
            });
            classCreated = true;
            Assert.StartsWith($"Created event class '{cls}' {{", text);
            Assert.Contains("1 Started (Info) 'Started {0}'", text);
            Assert.Contains("Project saved.", text);
            Assert.DoesNotContain("[Warn]", text);

            using (var read = JsonDocument.Parse(await CallAsync("twincat_event_class_read", new() { ["name"] = cls })))
            {
                Assert.Equal("project (.tsproj)", read.RootElement.GetProperty("origin").GetString());
                Assert.Equal(2, read.RootElement.GetProperty("events").GetArrayLength());
                Assert.Equal(0, read.RootElement.GetProperty("pinnedIn").GetArrayLength());
            }
            string tsproj = Directory.EnumerateFiles(Path.GetDirectoryName(_fixtureSln)!, "*.tsproj", SearchOption.AllDirectories).Single();
            Assert.Contains($">{cls}</Name>", File.ReadAllText(tsproj));

            // ---- PLC code sees TC_EVENTS.<class> ----
            await CallAsync("twincat_plc_create", new()
            {
                ["kind"] = "POU", ["subtype"] = "PROGRAM", ["parentPath"] = FixtureLayout.PousFolder, ["name"] = prg,
                ["declaration"] = $"PROGRAM {prg}\nVAR\n    stEvent : TcEventEntry;\nEND_VAR\n",
                ["implementation"] = $"stEvent := TC_EVENTS.{cls}.Started;\n",
            });
            prgCreated = true;
            Assert.Contains("Check all objects: no errors", await CallAsync("twincat_plc_check_all_objects", new()));

            // ---- update: a new event is usable at once ----
            text = await CallAsync("twincat_event_class_update", new()
            {
                ["name"] = cls,
                ["events"] = new[] { Ev(1, "Started", "Warning", "Started {0}"), Ev(3, "Third", "Critical", "Third") },
            });
            Assert.Contains("(GUID kept)", text);
            Assert.Contains("Added events: 3 Third.", text);
            Assert.Contains("Removed events: 2 Failed", text);
            await CallAsync("twincat_plc_write", new()
            {
                ["path"] = prgPath, ["kind"] = "POU", ["implementation"] = $"stEvent := TC_EVENTS.{cls}.Third;\n",
            });
            Assert.Contains("Check all objects: no errors", await CallAsync("twincat_plc_check_all_objects", new()));

            // ---- pin ----
            text = await CallAsync("twincat_plc_add_global_data_type", new() { ["typeName"] = cls });
            Assert.StartsWith($"Pinned event class '{cls}'", text);
            Assert.Contains("ExternalTypes.tmc^Global.", text);
            Assert.DoesNotContain("[Warn]", text);
            Assert.Contains($">{cls}</Name>", File.ReadAllText(externalTypes));
            Assert.Contains(XDocument.Load(plcproj).Descendants(), e => (string?)e.Attribute("Include") == "ExternalTypes.tmc");
            using (var read = JsonDocument.Parse(await CallAsync("twincat_event_class_read", new() { ["name"] = cls })))
            {
                var pin = read.RootElement.GetProperty("pinnedIn")[0];
                Assert.True(pin.GetProperty("upToDate").GetBoolean());
            }
            Assert.Contains("already pinned", await CallAsync("twincat_plc_add_global_data_type", new() { ["typeName"] = cls }));

            // ---- update while pinned: the pin is written again with the new content ----
            text = await CallAsync("twincat_event_class_update", new() { ["name"] = cls, ["displayText"] = "Integration events v2" });
            Assert.Contains("the update dropped the pin", text);
            Assert.Contains($"Pinned event class '{cls}'", text);
            Assert.Contains("Integration events v2", File.ReadAllText(externalTypes));
            await PlcProjectReadiness.AwaitAsync(_client!);
            Assert.Contains("Check all objects: no errors", await CallAsync("twincat_plc_check_all_objects", new()));
        }
        finally
        {
            if (prgCreated)
                await CallAsync("twincat_plc_delete", new() { ["path"] = prgPath, ["kind"] = "POU" }, expectError: null);
            if (classCreated)
            {
                try { Console.WriteLine($"[event_class] cleanup: {EventClassXae.DeleteType(cls)}"); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or MissingMemberException)
                {
                    Console.WriteLine($"[event_class] cleanup failed: {ex.Message}");
                }
                await CallAsync("twincat_save_project", new(), expectError: null);
            }
            if (File.Exists(externalTypes))
            {
                await CallAsync("twincat_close_project", new() { ["saveFirst"] = true }, expectError: null);
                File.Delete(externalTypes);
                var doc = XDocument.Load(plcproj, LoadOptions.PreserveWhitespace);
                foreach (var item in doc.Descendants().Where(e => (string?)e.Attribute("Include") == "ExternalTypes.tmc").ToList())
                {
                    var group = item.Parent;
                    item.Remove();
                    if (group is not null && !group.Elements().Any()) group.Remove();
                }
                doc.Save(plcproj);
                await CallAsync("twincat_open_project", new() { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true }, expectError: null);
                await PlcProjectReadiness.AwaitAsync(_client!);
            }
        }
    }
}
