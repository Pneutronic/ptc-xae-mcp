using System.Xml.Linq;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// twincat_plc_add_global_data_type for a STRUCT, live against the neutral fixture: an ENUM, a STRUCT using it and an
/// ALIAS of the STRUCT are created in the type system (PersistentType, through the DTE — no MCP tool creates them), a DUT of
/// the enum's name is refused as a collision, the ALIAS is pinned and takes STRUCT and ENUM along (ExternalTypes.tmc + .plcproj
/// entry, a tree node per type), and a program declaring the types builds. Names are unique per run: the XAE keeps the PLC's
/// global types for the life of the process. Cleanup removes the program, the types and the pin files (close + reopen).
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class GlobalDataTypeIntegrationTests : IAsyncLifetime
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
        Console.WriteLine($"[global_type] {tool}: {text}");
        if (expectError == true)
            Assert.StartsWith("[Error]", text);
        else if (expectError == false)
            Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal) || result.IsError == true, $"{tool} failed: {text}");
        return text;
    }

    [Trait("Description", "STRUCT mit ENUM und ALIAS im Typsystem anlegen, gleichnamige DUT als Kollision abgelehnt, ALIAS per Add Global Data Type pinnen → STRUCT und ENUM kommen mit (ExternalTypes.tmc, .plcproj, Baumknoten je Typ), Programm mit Variablen dieser Typen baut; Aufräumen mit Schliessen/Öffnen.")]
    [SkippableFact(Timeout = 900_000)]
    public async Task PinStruct_TakesItsEnumAlong_AndBuilds()
    {
        LiveXae.GuardReady(_ready);

        await CallAsync("twincat_open_project", new() { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        await PlcProjectReadiness.AwaitAsync(_client!);

        string stamp = DateTimeOffset.UtcNow.ToString("HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        string en = $"E_GdtIt_{stamp}", st = $"ST_GdtIt_{stamp}", al = $"T_GdtIt_{stamp}";
        string prg = $"PRG_GdtIt_{stamp}";
        string prgPath = $"{FixtureLayout.PousFolder}^{prg}";
        string dutPath = $"{FixtureLayout.DutsFolder}^{en}";
        string plcproj = Directory.EnumerateFiles(Path.GetDirectoryName(_fixtureSln)!, "*.plcproj", SearchOption.AllDirectories).Single();
        string externalTypes = Path.Combine(Path.GetDirectoryName(plcproj)!, "ExternalTypes.tmc");
        Assert.False(File.Exists(externalTypes), "the fixture copy must start without ExternalTypes.tmc");

        string text = await CallAsync("twincat_plc_add_global_data_type", new() { ["typeName"] = $"Nope_{stamp}" }, expectError: true);
        Assert.Contains($"No data type 'Nope_{stamp}'", text);

        var created = new List<string>();
        bool prgCreated = false, dutCreated = false;
        try
        {
            // ---- the types, as "Data Types > New" would keep them (PersistentType) ----
            string intGuid = EventClassXae.GuidOf("INT") ?? throw new InvalidOperationException("INT is not in the type system.");
            string udintGuid = EventClassXae.GuidOf("UDINT") ?? throw new InvalidOperationException("UDINT is not in the type system.");
            string enGuid = EventClassXae.CreateType(en,
                $"<DataType><Name PersistentType=\"true\">{en}</Name><BitSize>16</BitSize><BaseType GUID=\"{intGuid}\">INT</BaseType>" +
                "<EnumInfo><Text>Idle</Text><Enum>0</Enum></EnumInfo><EnumInfo><Text>Running</Text><Enum>1</Enum></EnumInfo></DataType>");
            created.Add(en);
            string stGuid = EventClassXae.CreateType(st,
                $"<DataType><Name PersistentType=\"true\">{st}</Name><BitSize>64</BitSize>" +
                $"<SubItem><Name>eMode</Name><Type GUID=\"{enGuid}\">{en}</Type><BitSize>16</BitSize><BitOffs>0</BitOffs></SubItem>" +
                $"<SubItem><Name>nCount</Name><Type GUID=\"{udintGuid}\">UDINT</Type><BitSize>32</BitSize><BitOffs>32</BitOffs></SubItem></DataType>");
            created.Insert(0, st);
            EventClassXae.CreateType(al,
                $"<DataType><Name PersistentType=\"true\">{al}</Name><BitSize>64</BitSize><BaseType GUID=\"{stGuid}\">{st}</BaseType></DataType>");
            created.Insert(0, al);
            await CallAsync("twincat_save_project", new());

            // ---- a DUT of the enum's name collides ----
            await CallAsync("twincat_plc_create", new()
            {
                ["kind"] = "DUT", ["subtype"] = "ENUM", ["parentPath"] = FixtureLayout.DutsFolder, ["name"] = en,
            });
            dutCreated = true;
            text = await CallAsync("twincat_plc_add_global_data_type", new() { ["typeName"] = al }, expectError: true);
            Assert.Contains($"the PLC project has a DUT '{en}'", text);
            Assert.EndsWith("Nothing was changed.", text);
            Assert.False(File.Exists(externalTypes));
            await CallAsync("twincat_plc_delete", new() { ["path"] = dutPath, ["kind"] = "DUT" });
            dutCreated = false;

            // ---- pin the alias: struct and enum come along ----
            text = await CallAsync("twincat_plc_add_global_data_type", new() { ["typeName"] = al });
            Assert.StartsWith($"Pinned ALIAS '{al}'", text);
            Assert.Contains($"Pinned its dependency STRUCT '{st}'", text);
            Assert.Contains($"Pinned its dependency ENUM '{en}'", text);
            Assert.Contains("Dependencies taken along:", text);
            Assert.DoesNotContain("[Warn]", text);
            Assert.DoesNotContain("[Error]", text);
            var pinned = XDocument.Load(externalTypes).Descendants().Where(e => e.Name.LocalName == "DataType")
                .Select(d => d.Elements().First(x => x.Name.LocalName == "Name").Value.Trim()).ToList();
            Assert.Equal(new[] { en, st, al }, pinned);
            Assert.Contains(XDocument.Load(plcproj).Descendants(), e => (string?)e.Attribute("Include") == "ExternalTypes.tmc");
            Assert.Contains("already pinned", await CallAsync("twincat_plc_add_global_data_type", new() { ["typeName"] = al }));

            // ---- PLC code declares the types and builds ----
            await PlcProjectReadiness.AwaitAsync(_client!);
            await CallAsync("twincat_plc_create", new()
            {
                ["kind"] = "POU", ["subtype"] = "PROGRAM", ["parentPath"] = FixtureLayout.PousFolder, ["name"] = prg,
                ["declaration"] = $"PROGRAM {prg}\nVAR\n    stRec : {st};\n    tRec : {al};\nEND_VAR\n",
                ["implementation"] = $"stRec.eMode := {en}.Running;\nstRec.nCount := stRec.nCount + 1;\ntRec := stRec;\n",
            });
            prgCreated = true;
            Assert.Contains("Check all objects: no errors", await CallAsync("twincat_plc_check_all_objects", new()));
            Assert.Contains("Build succeeded", await CallAsync("twincat_build_plc", new()), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (prgCreated)
                await CallAsync("twincat_plc_delete", new() { ["path"] = prgPath, ["kind"] = "POU" }, expectError: null);
            if (dutCreated)
                await CallAsync("twincat_plc_delete", new() { ["path"] = dutPath, ["kind"] = "DUT" }, expectError: null);
            foreach (string type in created)
            {
                try { Console.WriteLine($"[global_type] cleanup: {EventClassXae.DeleteType(type)}"); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or MissingMemberException)
                {
                    Console.WriteLine($"[global_type] cleanup failed: {ex.Message}");
                }
            }
            if (created.Count > 0) await CallAsync("twincat_save_project", new(), expectError: null);
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
