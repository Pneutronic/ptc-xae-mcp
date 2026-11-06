using System.Text.Json;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Live tests of the hmi-server group against a REAL XAE with a TEMP copy of an HMI
/// project (<see cref="HmiFixture"/>). Stage A only — no PLC is assumed: every ADS read
/// is expected to fail with HMI_ADS_E_* unless a PLC happens to answer, so the tests
/// assert on server-side behaviour (projects, extensions, mapping state, server
/// symbols) and on the confirmation gate. A successful ADS map (stage B) needs a
/// reachable PLC runtime and is not covered here (spec: open questions).
/// </summary>
[Collection("Stage HMI")]
[Trait("Stage", "HMI")]
public class HmiIntegrationTests(HmiFixture fx)
{
    private const string UnknownSymbol = "NoSuch.Symbol.HmiIntegrationTest";

    private static JsonElement Json(string text) =>
        HmiFixture.JsonAfterSummary(text) ?? throw new Xunit.Sdk.XunitException("Expected JSON after the summary, got: " + text);

    [SkippableFact(Timeout = 180_000)]
    public async Task ListProjects_FindsTheTempFixture()
    {
        fx.Guard();
        string text = await fx.CallAsync("twincat_hmi_list_projects");

        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        JsonElement json = Json(text);
        Assert.Equal("Beckhoff.TcHmi.1.12", json.GetProperty("automationId").GetString());
        Assert.True(json.GetProperty("automationVersion").GetInt32() >= 1);
        JsonElement project = json.GetProperty("projects").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == fx.ProjectName);
        Assert.Contains("ptc-xae-mcp-hmi-fixture", project.GetProperty("projectDirectory").GetString());
        Assert.True(project.GetProperty("isReady").GetBoolean());
    }

    [SkippableFact(Timeout = 180_000)]
    public async Task ProjectInfo_ReportsConfigExtensionsAndTree()
    {
        fx.Guard();
        string text = await fx.CallAsync("twincat_hmi_get_project_info", new()
        {
            ["hmiProject"] = fx.ProjectName,
            ["includeFiles"] = true,
            ["maxDepth"] = 1,
        });

        JsonElement json = Json(text);
        var domains = json.GetProperty("serverExtensions").EnumerateArray()
            .ToDictionary(e => e.GetProperty("domainName").GetString()!, e => e.GetProperty("loaded").GetBoolean());
        Assert.True(domains.ContainsKey("ADS"), text);
        Assert.True(domains.GetValueOrDefault("TcHmiSrv"), "TcHmiSrv extension should be loaded: " + text);
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("config").GetProperty("StartupView").GetString()));
        Assert.Contains(json.GetProperty("files").EnumerateArray(), f => f.GetProperty("name").GetString() == "Server");
    }

    [SkippableFact(Timeout = 180_000)]
    public async Task MappedSymbols_ListsAdsMappings()
    {
        fx.Guard();
        string text = await fx.CallAsync("twincat_hmi_list_mapped_symbols", new()
        {
            ["domain"] = "ADS",
            ["namePrefix"] = "ADS.PLC1.",
        });

        JsonElement json = Json(text);
        Assert.All(json.GetProperty("symbols").EnumerateArray(), s =>
            Assert.StartsWith("ADS.PLC1.", s.GetProperty("mappedName").GetString()));
        // The neutral fixture (#163) is a fresh starter project: it maps no ADS symbol, and
        // mapping one needs a reachable PLC (stage B). Only a source with mappings can show more.
        Assert.Equal(json.GetProperty("symbols").GetArrayLength(), json.GetProperty("returned").GetInt32());
        Assert.True(json.GetProperty("mappedCount").GetInt32() >= json.GetProperty("total").GetInt32(), text);
        if (json.GetProperty("total").GetInt32() == 0)
            throw new SkipException("The HMI fixture maps no ADS.PLC1.* symbol (the neutral fixture is a fresh " +
                                    $"starter project); set {HmiFixture.SourceEnvVar} to a neutral HMI project with ADS mappings.");
    }

    [SkippableFact(Timeout = 180_000)]
    public async Task ReadSymbols_OneUnknownNameDoesNotSinkTheBatch()
    {
        fx.Guard();
        string text = await fx.CallAsync("twincat_hmi_read_symbols", new()
        {
            ["symbols"] = new[] { "UserSelectType", UnknownSymbol, "IsAuthRequired" },
        });

        JsonElement results = Json(text).GetProperty("results");
        Assert.True(results[0].GetProperty("ok").GetBoolean(), text);
        Assert.False(results[1].GetProperty("ok").GetBoolean());
        Assert.Contains("SYMBOL_NOT_MAPPED", results[1].GetProperty("error").GetString());
        Assert.DoesNotContain("sessionId", results[1].GetProperty("error").GetString());
        Assert.True(results[2].GetProperty("ok").GetBoolean(), text);
    }

    [SkippableFact(Timeout = 420_000)]
    public async Task ServerControl_ShutdownThenStart_ComesBack()
    {
        fx.Guard();
        string down = await fx.CallAsync("twincat_hmi_server_control",
            new() { ["action"] = "shutdown", ["timeoutMs"] = 60_000 }, TimeSpan.FromMinutes(3));
        string up = await fx.CallAsync("twincat_hmi_server_control",
            new() { ["action"] = "start", ["timeoutMs"] = 90_000 }, TimeSpan.FromMinutes(3));

        Assert.StartsWith("HMI engineering server", down);
        Assert.Contains("shutdown done — 0/", down);
        Assert.StartsWith("HMI engineering server", up);
        Assert.Contains("start done", up);

        // The server answers again after the restart.
        string read = await fx.CallAsync("twincat_hmi_read_symbols", new() { ["symbols"] = new[] { "UserSelectType" } });
        Assert.StartsWith("Read 1/1", read);
    }

    [SkippableFact(Timeout = 180_000)]
    public async Task WriteSymbol_SameValue_FollowsTheConfirmationGate()
    {
        fx.Guard();
        string before = Json(await fx.CallAsync("twincat_hmi_read_symbols",
            new() { ["symbols"] = new[] { "UserSelectType" } })).GetProperty("results")[0].GetProperty("value").GetString()!;

        string text = await fx.CallAsync("twincat_hmi_write_symbol", new()
        {
            ["symbol"] = "UserSelectType",
            ["valueJson"] = before,
            ["confirmTimeoutSeconds"] = 10,
        }, TimeSpan.FromMinutes(1));

        if (HmiFixture.ExpectAutoConfirm)
        {
            Assert.StartsWith($"Wrote {before} to 'UserSelectType'", text);
            Assert.Contains("auto-confirmed (TEMP fixture", text);
            Assert.Contains("verified by read-back", text);
        }
        else
        {
            // Nobody answers the popup in an unattended run: it must expire and write nothing.
            Assert.StartsWith("[Error] Not confirmed within 10 s", text);
            Assert.Contains("Nothing was written", text);
        }
    }

    [SkippableFact(Timeout = 180_000)]
    public async Task UnmapSymbol_OnTheTempCopy_IsVerifiedAgainstTheLiveServer()
    {
        fx.Guard();
        if (!HmiFixture.ExpectAutoConfirm)
            throw new SkipException($"Needs a host started with PTC_XAE_MCP_HMI_AUTOCONFIRM_TEMP=1 and " +
                                    $"{HmiFixture.ExpectAutoConfirmEnvVar}=1 (nobody answers the popup in an unattended run).");

        // A mapping of the template; the TEMP copy is disposable.
        const string mapped = "ADS.PLC1.GVL_HMI.HmiOpened";
        JsonElement listed = Json(await fx.CallAsync("twincat_hmi_list_mapped_symbols", new()
        {
            ["domain"] = "ADS",
            ["namePrefix"] = mapped,
        }));
        if (!listed.GetProperty("symbols").EnumerateArray().Any(s => s.GetProperty("mappedName").GetString() == mapped))
            throw new SkipException($"The HMI fixture does not map '{mapped}' (the neutral fixture is a fresh starter " +
                                    "project; mapping needs a reachable PLC).");

        string text = await fx.CallAsync("twincat_hmi_unmap_symbol", new()
        {
            ["mapName"] = mapped,
            ["confirmTimeoutSeconds"] = 10,
        }, TimeSpan.FromMinutes(1));

        Assert.StartsWith($"Unmapped '{mapped}'", text);
        Assert.Contains("SYMBOL_NOT_MAPPED", text);

        string again = await fx.CallAsync("twincat_hmi_unmap_symbol", new() { ["mapName"] = mapped });
        Assert.StartsWith("Nothing to do", again);
    }
}
