using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// PLC visualizations (twincat_visu_*), live against the neutral fixture (<see cref="FixtureLayout"/>). The fixture's
/// own page is only read or written back unchanged; pages the tests create carry a time-stamped name and are deleted
/// in a finally block, like the temporary target visualization that makes the PLC compile the visualization code.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class VisuIntegrationTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost:5010/";

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
                Endpoint = new Uri(ServerUrl),
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

    private void SkipIfNotReady() => LiveXae.GuardReady(_ready);

    private async Task OpenFixtureAsync()
    {
        var open = await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.IsError == true, GetText(open));
        await PlcProjectReadiness.AwaitAsync(_client!);
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    private async Task<string> CallAsync(string tool, params (string Key, object? Value)[] args)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in args) dict[key] = value;
        var result = await _client!.CallToolAsync(tool, dict);
        string text = GetText(result);
        return result.IsError == true && !text.StartsWith("[Error]", StringComparison.Ordinal) ? "[Error] (protocol) " + text : text;
    }

    private async Task<string> OkAsync(string tool, params (string Key, object? Value)[] args)
    {
        string text = await CallAsync(tool, args);
        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), $"{tool} failed: {text}");
        return text;
    }

    private async Task<JsonElement> ReadVisuAsync(string path)
    {
        string json = await OkAsync("twincat_visu_read", ("path", path), ("kind", "VISU"));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement ElementOfType(JsonElement page, string type, int index = 0) =>
        page.GetProperty("elements").EnumerateArray().Where(e => e.GetProperty("type").GetString() == type).ElementAt(index);

    private async Task TryDeleteAsync(string path)
    {
        try { await CallAsync("twincat_plc_delete", ("path", path), ("kind", "VISU")); }
        catch { /* best-effort teardown */ }
    }

    private static string Stamp() => DateTimeOffset.UtcNow.ToString("MMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);

    private const string Page = """
        {
          "interface": "VAR_IN_OUT\n\tnLevel : INT;\nEND_VAR",
          "elements": [
            { "type": "Rectangle", "x": 10, "y": 10, "width": 120, "height": 40, "text": "Count %d", "textVariable": "GVL_Fixture.nCount",
              "frameColor": "#000000", "fillColor": "#C0FFC0", "alarmFrameColor": "#FF0000", "alarmFillColor": "#FFC0C0",
              "toggleColorVariable": "GVL_Fixture.bEnable", "invisibleVariable": "NOT GVL_Fixture.bEnable",
              "textColorVariable": "SEL(GVL_Fixture.bEnable, 16#FF000000, 16#FFFF0000)",
              "moveXVariable": "GVL_Fixture.nCount", "moveYVariable": "nLevel", "rotationVariable": "GVL_Fixture.nCount",
              "toggleVariable": "GVL_Fixture.bEnable" },
            { "type": "RoundedRectangle", "x": 150, "y": 10, "width": 100, "height": 40, "text": "Ready", "fillColor": "#E0E0FF" },
            { "type": "Ellipse", "x": 270, "y": 10, "width": 40, "height": 40, "fillColor": "#00FF00", "toggleColorVariable": "GVL_Fixture.bEnable" },
            { "type": "Line", "x": 10, "y": 60, "width": 300, "height": 0, "lineColor": "#0000FF", "moveYVariable": "nLevel" },
            { "type": "Label", "x": 10, "y": 70, "width": 200, "height": 20, "text": "Live page" },
            { "type": "TextField", "x": 10, "y": 100, "width": 120, "height": 24, "text": "%.2f", "textVariable": "GVL_Fixture.fValue",
              "fillColorVariable": "SEL(GVL_Fixture.bEnable, 16#FFC0C0C0, 16#FF00C000)" },
            { "type": "Button", "x": 150, "y": 100, "width": 100, "height": 30, "text": "Enable", "textColorVariable": "16#FF0000FF",
              "tapVariable": "GVL_Fixture.bEnable" },
            { "type": "Frame", "x": 320, "y": 10, "width": 160, "height": 40, "visualization": "@BLOCK@", "parameters": { "fb": "MAIN.fbStepChain" } }
          ]
        }
        """;

    /// <summary>A block for frames: its interface takes a step chain.</summary>
    private const string Block = """
        { "interface": "VAR_IN_OUT\n\tfb : FB_StepChain;\nEND_VAR",
          "elements": [ { "type": "Rectangle", "x": 0, "y": 0, "width": 160, "height": 40, "text": "Step %d", "textVariable": "fb.Step",
                          "fillColor": "#C0C0C0", "alarmFillColor": "#FF0000", "toggleColorVariable": "fb.Step = 0" } ] }
        """;

    // ── read ─────────────────────────────────────────────────────────────────

    [Trait("Description", "visu_read liest die Fixture-Visu strukturiert (7 Elemente, Variablen); falsches kind, POU-Pfad und fehlender Pfad liefern [Error] (nur lesend).")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Read_FixturePage_AndRefusals()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var page = await ReadVisuAsync(FixtureLayout.FixtureVisuPath);
        Assert.Equal(FixtureLayout.FixtureVisuElementCount, page.GetProperty("elementCount").GetInt32());
        Assert.Equal(0, page.GetProperty("unsupportedCount").GetInt32());
        Assert.True(page.GetProperty("writable").GetBoolean());
        var rect = ElementOfType(page, "Rectangle");
        Assert.Equal("Step %d", rect.GetProperty("text").GetString());
        Assert.Equal(FixtureLayout.StepChainStepMember, rect.GetProperty("textVariable").GetString());
        Assert.Equal("GVL_Fixture.bEnable", rect.GetProperty("toggleColorVariable").GetString());
        Assert.Equal("#FF000000", ElementOfType(page, "Line").GetProperty("lineColor").GetString());

        string wrongKind = await CallAsync("twincat_visu_read", ("path", FixtureLayout.FixtureVisuPath), ("kind", "POU"));
        Assert.StartsWith("[Error] The visu tools work on kind VISU", wrongKind);
        string pou = await CallAsync("twincat_visu_read", ("path", FixtureLayout.MainPath), ("kind", "VISU"));
        Assert.Contains("is a POU, not a VISU", pou);
        string missing = await CallAsync("twincat_visu_read", ("path", FixtureLayout.VisusFolder + "^VISU_DoesNotExist"), ("kind", "VISU"));
        Assert.StartsWith("[Error] PLC tree path not found", missing);
    }

    // ── a page XAE saved, written back ────────────────────────────────────────

    [Trait("Description", "Die von XAE gespeicherte Fixture-Visu wird über edit_elements (nur die unveränderte Schnittstelle) zurückgeschrieben: Datei danach Byte für Byte gleich, XAE übernimmt sie (Löschen + Import).")]
    [SkippableFact(Timeout = 300_000)]
    public async Task XaeSavedPage_RoundTripsByteForByte()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var page = await ReadVisuAsync(FixtureLayout.FixtureVisuPath);
        string file = page.GetProperty("file").GetString()!;
        byte[] before = await File.ReadAllBytesAsync(file);

        string result = await OkAsync("twincat_visu_edit_elements",
            ("path", FixtureLayout.FixtureVisuPath), ("kind", "VISU"), ("interface", page.GetProperty("interface").GetString()),
            ("expectedRevision", page.GetProperty("revision").GetString()));
        Assert.Contains("exactly the content written", result);

        var after = await ReadVisuAsync(FixtureLayout.FixtureVisuPath);
        Assert.Equal(before, await File.ReadAllBytesAsync(after.GetProperty("file").GetString()!));
        Assert.Equal(page.GetProperty("revision").GetString(), after.GetProperty("revision").GetString());
    }

    // ── create, edit, XAE keeps it, it compiles ──────────────────────────────────

    [Trait("Description", "Neue Visu mit allen Elementtypen (auch Frame auf eine Baustein-Visu mit VAR_IN_OUT, Tippen/Umschalten) per visu_apply anlegen, zurücklesen, Elemente ändern/entfernen/hinzufügen/kopieren, im XAE öffnen, per Umbenennen von XAE speichern lassen (Inhalt bleibt), mit temporärer TargetVisu ohne Fehler übersetzen; alles wird wieder gelöscht.")]
    [SkippableFact(Timeout = 900_000)]
    public async Task CreateEditAndCompile_AndXaeKeepsTheContent()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string stamp = Stamp();
        string visu = FixtureLayout.VisusFolder + "^VISU_Live" + stamp;
        string renamed = visu + "R";
        string target = FixtureLayout.VisuManagerPath + "^TargetVisu" + stamp;
        string blockName = "VISU_LiveBlock" + stamp;
        string block = FixtureLayout.VisusFolder + "^" + blockName;
        try
        {
            await OkAsync("twincat_visu_apply", ("path", block), ("kind", "VISU"), ("page", Block));
            string created = await OkAsync("twincat_visu_apply", ("path", visu), ("kind", "VISU"), ("page", Page.Replace("@BLOCK@", blockName, StringComparison.Ordinal)));
            Assert.StartsWith("Created visualization", created);
            Assert.Contains("Interface written.", created);

            var page = await ReadVisuAsync(visu);
            Assert.Equal(8, page.GetProperty("elementCount").GetInt32());
            Assert.Equal("GVL_Fixture.bEnable", ElementOfType(page, "Button").GetProperty("tapVariable").GetString());
            Assert.Equal("GVL_Fixture.bEnable", ElementOfType(page, "Rectangle").GetProperty("toggleVariable").GetString());
            var frame = ElementOfType(page, "Frame");
            Assert.Equal(blockName, frame.GetProperty("frame").GetProperty("visualizations")[0].GetString());
            Assert.Equal("MAIN.fbStepChain", frame.GetProperty("frame").GetProperty("parameters").GetProperty("fb").GetString());
            int frameId = frame.GetProperty("id").GetInt32();
            Assert.Equal("VAR_IN_OUT\n\tnLevel : INT;\nEND_VAR", page.GetProperty("interface").GetString());
            var rect = ElementOfType(page, "Rectangle");
            Assert.Equal("#FFC0FFC0", rect.GetProperty("fillColor").GetString());
            Assert.Equal("nLevel", rect.GetProperty("moveYVariable").GetString());
            int rectId = rect.GetProperty("id").GetInt32();
            int lineId = ElementOfType(page, "Line").GetProperty("id").GetInt32();
            int ellipseId = ElementOfType(page, "Ellipse").GetProperty("id").GetInt32();
            int buttonId = ElementOfType(page, "Button").GetProperty("id").GetInt32();
            Assert.Equal("#FF0000FF", ElementOfType(page, "Line").GetProperty("lineColor").GetString());

            string ops = $$"""
                [ { "op": "update", "id": {{rectId}}, "set": { "text": "Level %d", "textVariable": "nLevel", "fillColor": "#FFFF00" } },
                  { "op": "update", "id": {{lineId}}, "set": { "y": 70, "height": -10, "lineColor": "#FF0000" } },
                  { "op": "remove", "id": {{ellipseId}} },
                  { "op": "add", "element": { "type": "Label", "x": 10, "y": 140, "width": 150, "height": 20, "text": "Added" } },
                  { "op": "add", "element": { "copyFrom": {{buttonId}}, "x": 260 } },
                  { "op": "update", "id": {{frameId}}, "set": { "x": 330, "width": 200, "parameters": { "fb": "MAIN.fbStepChain" } } },
                  { "op": "add", "element": { "copyFrom": {{frameId}}, "y": 60 } } ]
                """;
            string edited = await OkAsync("twincat_visu_edit_elements", ("path", visu), ("kind", "VISU"), ("operations", ops),
                ("expectedRevision", page.GetProperty("revision").GetString()));
            Assert.Contains($"Removed element {ellipseId}.", edited);

            // Open and close the editor: XAE shows the page (no prompt, nothing to save).
            await OkAsync("twincat_open_object", ("treePath", visu));
            await OkAsync("twincat_close_object", ("treePath", visu));

            // A rename makes XAE write the page from what it holds: the edit must be in it.
            await OkAsync("twincat_plc_rename", ("path", visu), ("kind", "VISU"), ("newName", "VISU_Live" + stamp + "R"));
            var saved = await ReadVisuAsync(renamed);
            Assert.Equal(10, saved.GetProperty("elementCount").GetInt32());
            var savedFrames = saved.GetProperty("elements").EnumerateArray().Where(e => e.GetProperty("type").GetString() == "Frame").ToList();
            Assert.Equal(2, savedFrames.Count);
            Assert.All(savedFrames, f => Assert.Equal("MAIN.fbStepChain", f.GetProperty("frame").GetProperty("parameters").GetProperty("fb").GetString()));
            Assert.Equal(200, savedFrames.Single(f => f.GetProperty("id").GetInt32() == frameId).GetProperty("width").GetInt32());
            Assert.Equal("GVL_Fixture.bEnable", saved.GetProperty("elements").EnumerateArray()
                .First(e => e.GetProperty("type").GetString() == "Button").GetProperty("tapVariable").GetString());
            var savedRect = saved.GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == rectId);
            Assert.Equal("Level %d", savedRect.GetProperty("text").GetString());
            Assert.Equal("#FFFFFF00", savedRect.GetProperty("fillColor").GetString());
            Assert.DoesNotContain(saved.GetProperty("elements").EnumerateArray(), e => e.GetProperty("id").GetInt32() == ellipseId);
            var savedLine = saved.GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == lineId);
            Assert.Equal("#FFFF0000", savedLine.GetProperty("lineColor").GetString());
            Assert.Equal(-10, savedLine.GetProperty("height").GetInt32());
            Assert.Equal(2, saved.GetProperty("elements").EnumerateArray().Count(e => e.GetProperty("type").GetString() == "Button"));
            Assert.Contains(saved.GetProperty("elements").EnumerateArray(), e => e.TryGetProperty("text", out var t) && t.GetString() == "Added");

            // The integrated visualization generates no code; with a target visualization the PLC compiles the pages.
            await OkAsync("twincat_plc_create", ("kind", "VISU"), ("subtype", "TARGET"), ("parentPath", FixtureLayout.VisuManagerPath),
                ("name", "TargetVisu" + stamp));
            string check = await CallAsync("twincat_plc_check_all_objects");
            Assert.StartsWith("Check all objects: no errors", check);
        }
        finally
        {
            await TryDeleteAsync(target);
            await TryDeleteAsync(renamed);
            await TryDeleteAsync(visu);
            await TryDeleteAsync(block);
        }
    }
}
