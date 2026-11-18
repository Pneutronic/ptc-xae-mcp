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
    // Failed since 2.5.0 because enable_runtime_tmc wrote the TMC leaves of a runtime that has no TMC object
    // (refused by the server); the tool now writes the object whole.
    [SkippableFact(Timeout = 300_000)]
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

    // ── hmi-offline-types ───────────────────────────────────────────────────

    private string SchemaPath => Path.Combine(fx.ProjectDirectory, "Properties", "tchmi.project.Schema.json");

    private async Task<JsonElement> DatatypesAsync()
    {
        string text = await fx.CallAsync("twincat_hmi_list_project_datatypes", new() { ["hmiProject"] = fx.ProjectName });
        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        return Json(text);
    }

    [Trait("Description", "Projekt-Datentypen der HMI-Fixture lesbar: Schema-Datei gefunden, keine verwaisten $refs.")]
    [SkippableFact(Timeout = 180_000)]
    public async Task ListProjectDatatypes_ReadsTheFixtureSchema()
    {
        fx.Guard();
        JsonElement json = await DatatypesAsync();
        Assert.Equal(SchemaPath, json.GetProperty("schemaFile").GetString(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, json.GetProperty("danglingCount").GetInt32());
    }

    [Trait("Description", "Remote-Typ aus angehängter TMC: attach → create_project_datatype ST_FixtureRecord (Struct + Enum) → list zeigt beide ohne verwaiste $refs → zweites create abgewiesen; Schema-Datei danach zurückgesetzt.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task CreateProjectDatatype_FromAttachedTmc_ReadsBack()
    {
        fx.Guard();
        Skip.IfNot(HmiFixture.ExpectAutoConfirm, "Write lifecycle needs the TEMP-only auto-confirm (PTC_XAE_MCP_HMI_EXPECT_AUTOCONFIRM=1).");

        string original = File.ReadAllText(SchemaPath);
        string tmc = CopyFixtureTmcIntoTempTree();
        string root = VirtualPlc + ".ST_FixtureRecord";
        try
        {
            string attach = await fx.CallAsync("twincat_hmi_attach_tmc_file", new()
            {
                ["tmcFile"] = tmc,
                ["hmiProject"] = fx.ProjectName,
                ["confirmTimeoutSeconds"] = 30,
            });
            Assert.False(attach.StartsWith("[Error]", StringComparison.Ordinal), attach);

            string create = await fx.CallAsync("twincat_hmi_create_project_datatype", new()
            {
                ["remoteType"] = root,
                ["hmiProject"] = fx.ProjectName,
                ["confirmTimeoutSeconds"] = 30,
            });
            Assert.False(create.StartsWith("[Error]", StringComparison.Ordinal), create);
            JsonElement c = Json(create);
            Assert.Equal("tchmi:project#/definitions/" + root, c.GetProperty("parameterType").GetString());
            // Whether the server accepts RefreshDefinitions through WriteSymbolJson is recorded, not asserted.
            Console.WriteLine($"create_project_datatype refreshed={c.GetProperty("refreshed").GetBoolean()} " +
                              $"error={(c.TryGetProperty("refreshError", out JsonElement re) ? re.ToString() : "")}");

            JsonElement list = await DatatypesAsync();
            var byName = list.GetProperty("definitions").EnumerateArray().ToDictionary(d => d.GetProperty("name").GetString()!);
            Assert.Equal("struct", byName[root].GetProperty("kind").GetString());
            Assert.Equal("enum", byName[VirtualPlc + ".E_FixtureMode"].GetProperty("kind").GetString());
            Assert.Equal("attachedTmc", byName[root].GetProperty("source").GetString());
            Assert.Equal(0, list.GetProperty("danglingCount").GetInt32());

            string again = await fx.CallAsync("twincat_hmi_create_project_datatype", new()
            {
                ["remoteType"] = root,
                ["hmiProject"] = fx.ProjectName,
                ["confirmTimeoutSeconds"] = 30,
            });
            Assert.StartsWith("[Error]", again);
            Assert.Contains("already exists", again, StringComparison.Ordinal);
        }
        finally
        {
            File.WriteAllText(SchemaPath, original);   // TEMP copy only; there is no delete tool
            await fx.CallAsync("twincat_hmi_detach_tmc_file", new()
            {
                ["virtualPlcName"] = VirtualPlc,
                ["hmiProject"] = fx.ProjectName,
                ["confirmTimeoutSeconds"] = 30,
            });
        }
    }

    [Trait("Description", "Switch Mapping to PLC: ein TMC-Domain-Eintrag (per Server-Config angelegt) wird auf die ADS-Runtime mit USE_TMC umgestellt — gleicher Name, Domain ADS, Runtime kennt das Symbol; danach unmap + disable.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task SwitchMappingToPlc_TmcEntry_BecomesAdsUnderTheSameName()
    {
        fx.Guard();
        Skip.IfNot(HmiFixture.ExpectAutoConfirm, "Write lifecycle needs the TEMP-only auto-confirm (PTC_XAE_MCP_HMI_EXPECT_AUTOCONFIRM=1).");

        string mapName = VirtualPlc + "." + FixtureLayout.BoolSymbol;
        string config = "TcHmiSrv.Config::SYMBOLS::" + mapName;
        string? runtime = null;
        try
        {
            (runtime, _) = await EnableRuntimeTmcAsync();
            // The entry of a TMC-domain mapping (the domain itself never serves values, #160).
            string entry = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ACCESS"] = 3,
                ["DOMAIN"] = "TMC",
                ["MAPPING"] = VirtualPlc + "::" + FixtureLayout.BoolSymbol.Replace(".", "::", StringComparison.Ordinal),
                ["USEMAPPING"] = true,
            });
            string write = await fx.CallAsync("twincat_hmi_write_symbol", new()
            {
                ["symbol"] = config,
                ["valueJson"] = entry,
                ["hmiProject"] = fx.ProjectName,
                ["confirmTimeoutSeconds"] = 30,
            });
            Assert.False(write.StartsWith("[Error]", StringComparison.Ordinal), write);
            Assert.False(Json(write).GetProperty("writesToPlc").GetBoolean(), write);

            string sw = await fx.CallAsync("twincat_hmi_switch_mapping_to_plc", new()
            {
                ["runtime"] = runtime,
                ["mapNames"] = new[] { mapName },
                ["hmiProject"] = fx.ProjectName,
                ["confirmTimeoutSeconds"] = 30,
            });
            Assert.False(sw.StartsWith("[Error]", StringComparison.Ordinal), sw);
            JsonElement result = Json(sw).GetProperty("results")[0];
            Assert.Equal(mapName, result.GetProperty("mappedName").GetString());
            string state = result.GetProperty("state").GetString()!;
            Assert.True(state is "switched" or "switchedTmcMode", sw);

            string read = await fx.CallAsync("twincat_hmi_read_symbols", new()
            {
                ["symbols"] = new[] { config },
                ["hmiProject"] = fx.ProjectName,
            });
            string value = Json(read).GetProperty("results")[0].GetProperty("value").GetString()!;
            using var after = JsonDocument.Parse(value);
            Assert.Equal("ADS", after.RootElement.GetProperty("DOMAIN").GetString());
        }
        finally
        {
            await fx.CallAsync("twincat_hmi_unmap_symbol", new()
            {
                ["mapName"] = mapName,
                ["domain"] = "ADS",
                ["hmiProject"] = fx.ProjectName,
                ["confirmTimeoutSeconds"] = 30,
            });
            if (runtime is not null) await DisableRuntimeTmcAsync(runtime);   // best effort
        }
    }
}
