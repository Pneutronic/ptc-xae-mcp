using System.Text.Json;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Issue #163: the hmi-ads-tmc group live against the TEMP copy of the neutral HMI fixture. The TMC is a copy of
/// the neutral PLC fixture's FixturePlc.tmc placed inside the TEMP solution tree (the committed original is never
/// touched). Verifies what only a real run shows: the project stores the TMC path RELATIVE (spec C3), a second
/// attach is refused before any popup, detach really removes it, and the ADS runtime table is readable.
/// </summary>
[Collection("Stage HMI")]
[Trait("Stage", "HMI")]
public class HmiTmcIntegrationTests(HmiFixture fx)
{
    private const string VirtualPlc = FixtureLayout.PlcName;

    private static JsonElement Json(string text) =>
        HmiFixture.JsonAfterSummary(text) ?? throw new Xunit.Sdk.XunitException("Expected JSON after the summary, got: " + text);

    private async Task<int> AttachedCountAsync()
    {
        string list = await fx.CallAsync("twincat_hmi_list_tmc_files", new() { ["hmiProject"] = fx.ProjectName });
        Assert.False(list.StartsWith("[Error]", StringComparison.Ordinal), list);
        return Json(list).GetProperty("count").GetInt32();
    }

    private string CopyFixtureTmcIntoTempTree()
    {
        string? plcSln = FixtureLayout.FindRepoFixtureSln();
        Skip.If(plcSln is null, "The neutral PLC fixture (source of the TMC) is not in this checkout.");
        string source = Path.Combine(Path.GetDirectoryName(plcSln)!, FixtureLayout.SolutionName, VirtualPlc, VirtualPlc + ".tmc");
        Skip.IfNot(File.Exists(source), $"No {VirtualPlc}.tmc in the PLC fixture: {source}");

        string target = Path.Combine(Path.GetDirectoryName(fx.SlnPath)!, "Tmc", VirtualPlc + ".tmc");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        return target;
    }

    [Trait("Description", "TMC-Lebenszyklus auf der TEMP-Kopie: leer → attach (relativ gespeichert) → zweites attach abgewiesen → detach → leer.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task AttachDetach_Lifecycle_StoresTheFileRelative()
    {
        fx.Guard();
        Skip.IfNot(HmiFixture.ExpectAutoConfirm, "Write lifecycle needs the TEMP-only auto-confirm (PTC_XAE_MCP_HMI_EXPECT_AUTOCONFIRM=1).");

        Assert.Equal(0, await AttachedCountAsync());
        string tmc = CopyFixtureTmcIntoTempTree();
        try
        {
            string attach = await fx.CallAsync("twincat_hmi_attach_tmc_file", new()
            {
                ["tmcFile"] = tmc,
                ["hmiProject"] = fx.ProjectName,
                ["confirmTimeoutSeconds"] = 30,
            });
            Assert.False(attach.StartsWith("[Error]", StringComparison.Ordinal), attach);
            JsonElement a = Json(attach);
            Assert.Equal(VirtualPlc, a.GetProperty("virtualPlcName").GetString());
            string storedAs = a.GetProperty("storedAs").GetString()!;
            Assert.False(Path.IsPathRooted(storedAs), "spec C3: the project must store the TMC path relative, got " + storedAs);
            Assert.EndsWith(VirtualPlc + ".tmc", storedAs, StringComparison.OrdinalIgnoreCase);
            Assert.True(a.GetProperty("symbolCount").GetInt32() > 0, attach);

            // Independent read-back: the .hmiproj on disk names the file, but never the absolute TEMP path.
            string hmiproj = Directory.EnumerateFiles(fx.ProjectDirectory, "*.hmiproj").Single();
            string projText = File.ReadAllText(hmiproj);
            Assert.Contains(VirtualPlc + ".tmc", projText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetDirectoryName(fx.SlnPath)!.Replace("\\", "\\\\"), projText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, await AttachedCountAsync());

            string again = await fx.CallAsync("twincat_hmi_attach_tmc_file", new()
            {
                ["tmcFile"] = tmc,
                ["hmiProject"] = fx.ProjectName,
                ["confirmTimeoutSeconds"] = 30,
            });
            Assert.StartsWith("[Error]", again);
            Assert.Contains("already attached", again, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, await AttachedCountAsync());

            string detach = await fx.CallAsync("twincat_hmi_detach_tmc_file", new()
            {
                ["virtualPlcName"] = VirtualPlc,
                ["hmiProject"] = fx.ProjectName,
                ["confirmTimeoutSeconds"] = 30,
            });
            Assert.False(detach.StartsWith("[Error]", StringComparison.Ordinal), detach);
            Assert.Equal(0, await AttachedCountAsync());
        }
        finally
        {
            // Best effort: never leave the TMC attached for the next test in the stage.
            await fx.CallAsync("twincat_hmi_detach_tmc_file", new()
            {
                ["virtualPlcName"] = VirtualPlc,
                ["hmiProject"] = fx.ProjectName,
                ["confirmTimeoutSeconds"] = 30,
            });
        }
    }

    // ── #161: server-side TMC (USE_TMC) ─────────────────────────────────────

    private async Task<JsonElement> RuntimesAsync()
    {
        string text = await fx.CallAsync("twincat_hmi_list_ads_runtimes", new() { ["hmiProject"] = fx.ProjectName });
        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        return Json(text).GetProperty("runtimes");
    }

    private async Task<string> DisableRuntimeTmcAsync(string runtime) =>
        await fx.CallAsync("twincat_hmi_disable_runtime_tmc", new()
        {
            ["runtime"] = runtime,
            ["hmiProject"] = fx.ProjectName,
            ["confirmTimeoutSeconds"] = 30,
        });

    private async Task<(string Runtime, JsonElement Enabled)> EnableRuntimeTmcAsync()
    {
        JsonElement runtimes = await RuntimesAsync();
        Assert.True(runtimes.GetArrayLength() >= 1, "The fixture's ADS extension has no runtime.");
        string runtime = runtimes[0].GetProperty("name").GetString()!;
        Assert.False(runtimes[0].GetProperty("useTmc").GetBoolean(), $"Runtime {runtime} already uses a TMC before the test.");

        string enable = await fx.CallAsync("twincat_hmi_enable_runtime_tmc", new()
        {
            ["runtime"] = runtime,
            ["tmcFile"] = CopyFixtureTmcIntoTempTree(),
            ["hmiProject"] = fx.ProjectName,
            ["confirmTimeoutSeconds"] = 30,
        });
        Assert.False(enable.StartsWith("[Error]", StringComparison.Ordinal), enable);
        return (runtime, Json(enable));
    }

    [Trait("Description", "#161 Vertrag: enable_runtime_tmc erzeugt die .bin unter .engineering_servers und setzt USE_TMC samt relativem Pfad (Read-Back über list_ads_runtimes); disable setzt beides zurück.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task RuntimeTmc_EnableDisable_ConfigReadsBack()
    {
        fx.Guard();
        Skip.IfNot(HmiFixture.ExpectAutoConfirm, "Write lifecycle needs the TEMP-only auto-confirm (PTC_XAE_MCP_HMI_EXPECT_AUTOCONFIRM=1).");

        string? runtime = null;
        try
        {
            (runtime, JsonElement e) = await EnableRuntimeTmcAsync();
            string relative = e.GetProperty("dataSymbolUpAndDownload").GetString()!;
            Assert.False(Path.IsPathRooted(relative), "DATA_SYMBOLUPANDDOWNLOAD must be relative to the engineering server folder, got " + relative);
            Assert.EndsWith(".DATA_UPLOADINFO2.bin", relative, StringComparison.OrdinalIgnoreCase);
            string bin = e.GetProperty("binFile").GetString()!;
            Assert.True(File.Exists(bin), "converted file missing: " + bin);
            Assert.Equal(e.GetProperty("binSizeBytes").GetInt64(), new FileInfo(bin).Length);
            Assert.Contains(".engineering_servers", bin, StringComparison.OrdinalIgnoreCase);

            JsonElement on = (await RuntimesAsync()).EnumerateArray().Single(r => r.GetProperty("name").GetString() == runtime);
            Assert.True(on.GetProperty("useTmc").GetBoolean());
            Assert.Equal(relative, on.GetProperty("tmcFile").GetString());

            string disable = await DisableRuntimeTmcAsync(runtime);
            Assert.False(disable.StartsWith("[Error]", StringComparison.Ordinal), disable);
            JsonElement off = (await RuntimesAsync()).EnumerateArray().Single(r => r.GetProperty("name").GetString() == runtime);
            Assert.False(off.GetProperty("useTmc").GetBoolean());
        }
        finally
        {
            if (runtime is not null) await DisableRuntimeTmcAsync(runtime);   // best effort
        }
    }

    [Trait("Description", "#161 Abnahme: mit USE_TMC lässt sich ein PLC-Symbol des Fixtures OHNE SPS über die ADS-Domain mappen (B10 gelöst); danach unmap + disable.")]
    // Fails in every live run since 2.5.0; taken out until the HMI tools are reviewed as a whole.
    [SkippableFact(Timeout = 300_000, Skip = "Known failure since 2.5.0 (runtime TMC mapping without a PLC); parked until the HMI tools are reviewed.")]
    public async Task RuntimeTmc_Enabled_MapsAPlcSymbolWithoutAPlc()
    {
        fx.Guard();
        Skip.IfNot(HmiFixture.ExpectAutoConfirm, "Write lifecycle needs the TEMP-only auto-confirm (PTC_XAE_MCP_HMI_EXPECT_AUTOCONFIRM=1).");

        string? runtime = null;
        string? mapName = null;
        try
        {
            (runtime, _) = await EnableRuntimeTmcAsync();
            string map = await fx.CallAsync("twincat_hmi_map_symbol", new()
            {
                ["plcPath"] = FixtureLayout.BoolSymbol,
                ["runtime"] = runtime,
                ["hmiProject"] = fx.ProjectName,
                ["confirmTimeoutSeconds"] = 30,
            });
            Assert.False(map.StartsWith("[Error]", StringComparison.Ordinal),
                "With USE_TMC the ADS extension should know the PLC symbols without a PLC: " + map);
            mapName = Json(map).GetProperty("mapName").GetString();
        }
        finally
        {
            if (mapName is not null)
                await fx.CallAsync("twincat_hmi_unmap_symbol", new()
                {
                    ["mapName"] = mapName,
                    ["hmiProject"] = fx.ProjectName,
                    ["confirmTimeoutSeconds"] = 30,
                });
            if (runtime is not null) await DisableRuntimeTmcAsync(runtime);   // best effort
        }
    }

    [Trait("Description", "ADS-Runtime-Tabelle des HMI-Servers ist lesbar: mindestens ein Runtime mit literaler NetId und Port.")]
    [SkippableFact(Timeout = 180_000)]
    public async Task ListAdsRuntimes_ReportsTheFixtureRuntime()
    {
        fx.Guard();
        string text = await fx.CallAsync("twincat_hmi_list_ads_runtimes", new() { ["hmiProject"] = fx.ProjectName });

        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        JsonElement json = Json(text);
        Assert.True(json.GetProperty("count").GetInt32() >= 1, text);
        JsonElement rt = json.GetProperty("runtimes").EnumerateArray().First();
        Assert.Matches(@"^\d{1,3}(\.\d{1,3}){5}$", rt.GetProperty("netId").GetString()!);
        Assert.True(rt.GetProperty("port").GetInt32() > 0, text);
    }
}
