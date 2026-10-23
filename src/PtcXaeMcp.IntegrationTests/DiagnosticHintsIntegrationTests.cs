using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Stage XAE — the diagnostic messages of twincat_link_variables and twincat_add_ethercat_box, and
/// twincat_reload_device_descriptions, against the neutral fixture. Every refused call here must leave the
/// fixture copy unchanged.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class DiagnosticHintsIntegrationTests : IAsyncLifetime
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
                ConnectionTimeout = TimeSpan.FromSeconds(5)
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
    }

    private async Task<string> CallAsync(string tool, Dictionary<string, object?>? args = null) =>
        GetText(await _client!.CallToolAsync(tool, args ?? new Dictionary<string, object?>()));

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    // ---- twincat_link_variables ----------------------------------------------

    [Trait("Description", "Zwei Eingänge derselben Klemme linken → Fehler nennt Typ, Bitgrösse und Richtung beider Seiten und den Richtungshinweis; es entsteht kein Link.")]
    [SkippableFact(Timeout = 180_000)]
    public async Task LinkVariables_TwoInputs_NamesBothSidesAndCreatesNoLink()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string text = await CallAsync("twincat_link_variables", new()
        {
            ["variable1TreePath"] = FixtureLayout.El1008Channel1Input,
            ["variable2TreePath"] = FixtureLayout.El1008Channel2Input,
        });

        Assert.StartsWith($"[Error] LinkVariables refused the link: '{FixtureLayout.El1008Channel1Input}' is BIT", text);
        Assert.Contains($"'{FixtureLayout.El1008Channel1Input}' is BIT (1 bit, input)", text);
        Assert.Contains($"'{FixtureLayout.El1008Channel2Input}' is BIT (1 bit, input)", text);
        Assert.Contains("Both are inputs: inputs link to outputs only.", text);

        string channel2 = await CallAsync("twincat_get_device_xml", new() { ["treePath"] = FixtureLayout.El1008Channel2Input });
        Assert.DoesNotContain("<LinkedWith", channel2, StringComparison.Ordinal);
    }

    [Trait("Description", "Link mit einem nicht existierenden Pfad → Fehler nennt genau diesen Pfad.")]
    [SkippableFact(Timeout = 180_000)]
    public async Task LinkVariables_MissingPath_NamesThePathThatDoesNotResolve()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        const string missing = "TIID^Device 1 (EtherCAT)^Term 1 (EK1100)^Term 99 (EL0000)^Channel 1^Input";

        string text = await CallAsync("twincat_link_variables", new()
        {
            ["variable1TreePath"] = FixtureLayout.El1008Channel2Input,
            ["variable2TreePath"] = missing,
        });

        Assert.StartsWith("[Error]", text);
        Assert.Contains($"'{missing}' does not resolve to a tree item.", text);
        Assert.DoesNotContain($"'{FixtureLayout.El1008Channel2Input}' does not resolve", text);
    }

    [Trait("Description", "Link mit einem Klemmenknoten statt einer Variable → Fehler nennt den Pfad, der keine Variable ist.")]
    [SkippableFact(Timeout = 180_000)]
    public async Task LinkVariables_TerminalNode_NamesThePathThatIsNoVariable()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string text = await CallAsync("twincat_link_variables", new()
        {
            ["variable1TreePath"] = FixtureLayout.El1008Path,
            ["variable2TreePath"] = FixtureLayout.El1008Channel2Input,
        });

        Assert.StartsWith("[Error]", text);
        Assert.Contains($"'{FixtureLayout.El1008Path}' is not a variable", text);
    }

    // ---- twincat_reload_device_descriptions -----------------------------------

    [Trait("Description", "Gerätebeschreibungen neu laden mit offener Fixture → läuft ohne Dialog durch und meldet die Laufzeit.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task ReloadDeviceDescriptions_WithFixtureOpen_CompletesWithoutDialog()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string text = await CallAsync("twincat_reload_device_descriptions");

        Assert.StartsWith("Device descriptions reloaded in ", text);
    }

    // ---- twincat_add_ethercat_box ---------------------------------------------

    [Trait("Description", "Box mit einem Typ, den keine ESI beschreibt → Fehler sagt 'No ESI describes …' und verweist auf den Reload; es entsteht kein Knoten.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task AddEtherCatBox_TypeNoEsiDescribes_SaysSoAndCreatesNothing()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        const string vInfo = "ZZ9999-NOSUCH";
        const string boxName = "ZZ_NoEsi (ZZ9999)";

        string text = await CallAsync("twincat_add_ethercat_box", new()
        {
            ["parentPath"] = FixtureLayout.EtherCatMasterPath,
            ["vInfo"] = vInfo,
            ["name"] = boxName,
        });

        Assert.StartsWith("[Error]", text);
        Assert.Contains($"No ESI describes {vInfo} under ", text);
        Assert.Contains("twincat_reload_device_descriptions", text);

        string topology = await CallAsync("twincat_get_fieldbus_topology");
        Assert.DoesNotContain(boxName, topology, StringComparison.Ordinal);
    }
}
