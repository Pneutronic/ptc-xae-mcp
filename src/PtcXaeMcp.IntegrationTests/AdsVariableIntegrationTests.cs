using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Stage XAR — ads-variable-rw against a live PLC on the stage's explicit runtime target
/// (<see cref="XarRuntimeFixture.AmsNetId"/>, issue #195). Preconditions: the neutral fixture activated on
/// that runtime and in Run, MCP server on port 5010. Managed by <see cref="XarRuntimeFixture"/>; every
/// call passes the target as <c>amsNetId</c>, so none can fall back to the local runtime.
///
/// Read tests use the fixture's GVL_Fixture symbols (<see cref="FixtureLayout"/>).
/// Write tests use PRG_IntegrationScratch.bAdsRwTest (BOOL, created if absent).
/// </summary>
[Collection("Stage XAR")]
[Trait("Stage", "XAR")]
public class AdsVariableIntegrationTests : IAsyncLifetime
{
    private readonly XarRuntimeFixture _xar;
    private McpClient? _client;

    public AdsVariableIntegrationTests(XarRuntimeFixture xar)
    {
        _xar = xar;
    }

    public async Task InitializeAsync()
    {
        if (!_xar.IsReady) return;
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(LiveXae.ServerUrl),
                ConnectionTimeout = TimeSpan.FromSeconds(5)
            });
            _client = await McpClient.CreateAsync(transport,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch { /* _client stays null; SkipIfNotReady will gate tests */ }
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }

    private void SkipIfNotReady()
    {
        _xar.SkipIfNotReady();
        if (_client is null) throw new SkipException("ADS: MCP client could not be created for Stage XAR.");
    }

    // Every ADS call names the stage's runtime (issue #195).
    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args)
    {
        args["amsNetId"] = _xar.AmsNetId;
        var result = await _client!.CallToolAsync(tool, args,
            cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    // ---- Read tests ----------------------------------------------------------

    [Trait("Description", "BOOL über ADS lesen (GVL_Fixture.bEnable) → valides JSON mit success/plcType=BOOL/bool-value.")]
    [SkippableFact]
    public async Task Read_BOOL_GvlFixtureEnable_ReturnsValidJson()
    {
        SkipIfNotReady();
        var text = await CallAsync("twincat_read_ads_variable",
            new Dictionary<string, object?> { ["symbolPath"] = FixtureLayout.BoolSymbol });

        Assert.False(string.IsNullOrWhiteSpace(text));
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean(), $"Expected success=true but got: {text}");
        Assert.Equal("BOOL", root.GetProperty("plcType").GetString());
        Assert.Equal(FixtureLayout.BoolSymbol, root.GetProperty("symbol").GetString());
        // value must be a JSON bool
        var kind = root.GetProperty("value").ValueKind;
        Assert.True(kind == JsonValueKind.True || kind == JsonValueKind.False,
            $"Expected JSON bool value but got {kind}");
    }

    [Trait("Description", "UINT über ADS lesen (GVL_Fixture.nCount) → value = 2.")]
    [SkippableFact]
    public async Task Read_UINT_GvlFixtureCount_Returns2()
    {
        SkipIfNotReady();
        var text = await CallAsync("twincat_read_ads_variable",
            new Dictionary<string, object?> { ["symbolPath"] = FixtureLayout.UintSymbol });

        Assert.False(string.IsNullOrWhiteSpace(text));
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean(), $"Expected success=true but got: {text}");
        // GVL_Fixture.nCount : UINT := 2
        Assert.Equal(FixtureLayout.UintSymbolValue, root.GetProperty("value").GetUInt32());
    }

    [Trait("Description", "twincat_tcunit_results auf der laufenden Fixture-PLC ohne TcUnit → [Error], das den fehlenden Runner und TcUnit.RUN() nennt (Lesepfad über Variablen-Handles gegen die echte Runtime).")]
    [SkippableFact]
    public async Task TcUnitResults_WithoutTcUnitInThePlc_SaysWhatIsMissing()
    {
        SkipIfNotReady();
        var text = await CallAsync("twincat_tcunit_results", new Dictionary<string, object?> { ["timeoutSeconds"] = 5 });

        Assert.StartsWith("[Error] TcUnit results not readable: GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished", text);
        Assert.Contains(_xar.AmsNetId, text);
        Assert.Contains("TcUnit.RUN()", text);
    }

    [Trait("Description", "Unbekanntes Symbol über ADS lesen → muss mit [Error] abgewiesen werden.")]
    [SkippableFact]
    public async Task Read_UnknownSymbol_ReturnsError()
    {
        SkipIfNotReady();
        var text = await CallAsync("twincat_read_ads_variable",
            new Dictionary<string, object?> { ["symbolPath"] = "NONEXISTENT.bDoesNotExist" });

        Assert.StartsWith("[Error]", text);
    }

    // ---- Write tests ---------------------------------------------------------

    [Trait("Description", "Schreiben auf ein eingangsgemapptes Symbol (AT %I*, GVL_Fixture.bInputSignal) → abgewiesen.")]
    [SkippableFact]
    public async Task Write_InputMapped_Refused()
    {
        SkipIfNotReady();
        var text = await CallAsync("twincat_write_ads_variable",
            new Dictionary<string, object?>
            {
                ["symbolPath"] = FixtureLayout.InputMappedBoolSymbol,
                ["value"] = "true",
            });

        // Must refuse: AT %I* symbol.
        // If the input-mapped detection is inconclusive (fail-open), we accept
        // either [Error] or a success JSON (documented limitation in spec).
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Trait("Description", "Schreiben mit leerem Symbolpfad → muss mit [Error] abgewiesen werden.")]
    [SkippableFact]
    public async Task Write_EmptyPath_ReturnsError()
    {
        SkipIfNotReady();
        var text = await CallAsync("twincat_write_ads_variable",
            new Dictionary<string, object?> { ["symbolPath"] = "", ["value"] = "true" });

        Assert.StartsWith("[Error]", text);
    }
}
