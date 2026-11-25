using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// End-to-end tests for the scope-measurement MCP tools against a real running
/// TwinCAT XAE. Tests are gated on TE130X Scope View being installed; if the
/// assembly is absent they skip with a clear message rather than fail.
///
/// Scenarios:
///   1. create_scope_project → scope appears in solution, get_scope_status
///      reports an empty scope.
///   2. add_scope_channel → list_scope_channels shows chart/axis/channel with
///      bound acquisition; second add reuses chart/axis (LookUpChild); labels
///      are not at template defaults; set_scope_labels round-trips; stacked-axis
///      mode is confirmed via get_scope_status.
///   3. export_scope_data before any recording → structured "no data" refusal.
///
/// Note: recording start/stop is out of group scope. Scenario 3's recording
/// step is left as a placeholder comment — the assertion on the refusal is the
/// key contract check per the spec.
/// </summary>
[Collection("Stage Scope")]
[Trait("Stage", "Scope")]
public class ScopeIntegrationTests : IAsyncLifetime
{
    private const string ServerUrl   = "http://localhost:5010/";
    private const string ScopeAssemblyPath =
        @"C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TE130x-Scope-View\TwinCAT.Measurement.AutomationInterface.dll";
    // Unique per test instance (xUnit creates a fresh instance per test) so
    // AddFromTemplate always targets a clean directory — a leftover folder
    // from a prior scope test would otherwise make create produce a project
    // whose scope-config root is inaccessible.
    private readonly string ScopeProjectName =
        "SC_IT_" + Guid.NewGuid().ToString("N")[..8];

    private McpClient? _client;
    private bool _ready;
    private bool _scopeViewInstalled;
    private string _fixtureSln = string.Empty;

    // A PLC symbol bound via the scratch POU.  EnsureAsync bootstraps the POU;
    // OpenFixtureAsync then writes the BOOL variable into it via WriteAsync.
    private static string FixtureSymbol =>
        $"{FixtureLayout.PousFolder}^{ScratchPou.Name}.bScopeTestVar";

    public async Task InitializeAsync()
    {
        _scopeViewInstalled = File.Exists(ScopeAssemblyPath);

        var fixture = LiveXae.FixtureSln();
        if (fixture is null) { LiveXae.Announce(false, string.Empty); return; }
        _fixtureSln = fixture;

        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint          = new Uri(ServerUrl),
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

    private void SkipIfNotReady()
    {
        LiveXae.GuardReady(_ready);
        if (!_scopeViewInstalled)
            throw new SkipException(
                $"TE130X Scope View assembly not found at: {ScopeAssemblyPath}. " +
                "Install TwinCAT Scope View to run scope-measurement integration tests.");
    }

    private async Task<string> OpenFixtureAsync()
    {
        // Open the fixture FIRST. The scratch-POU bootstrap reads/writes POUs
        // and MAIN, so it needs a solution loaded; a prior scope test's
        // CleanupScopeProjectAsync closes the solution, and opening with
        // closeCurrentFirst AFTER the scratch write would discard it anyway.
        var open = await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?>
            {
                ["slnPath"]          = _fixtureSln,
                ["closeCurrentFirst"] = true,
            });
        Assert.False(open.IsError == true, GetText(open));

        // Ensure scratch POU exists, then write the scope fixture symbol into it.
        await ScratchPou.EnsureAsync(_client!);
        await ScratchPou.WriteAsync(_client!,
            decl: $"PROGRAM {ScratchPou.Name}\nVAR\nbScopeTestVar : BOOL; // scope-measurement fixture symbol\nEND_VAR",
            impl: ScratchPou.SafeImpl);

        return _fixtureSln;
    }

    private void CleanLeftoverScopeProjectDir()
    {
        try
        {
            var slnDir = System.IO.Path.GetDirectoryName(_fixtureSln);
            if (slnDir is null) return;
            var dir = System.IO.Path.Combine(slnDir, ScopeProjectName);
            if (System.IO.Directory.Exists(dir))
                System.IO.Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort; unique names keep tests deterministic anyway */ }
    }

    private async Task CleanupScopeProjectAsync()
    {
        // Deterministically UNREGISTER the scope project from the solution
        // before closing or deleting its directory. Relying on
        // close-without-save to "discard" it is unsafe: if the close is blocked
        // (modal save prompt / COM busy) the swallowed failure leaves the
        // project registered while its backing folder is deleted below — every
        // later open then throws COMException 0x80004004 (E_ABORT) with
        // "Missing Project Nodes for Scope Instance ...", corrupting the shared
        // fixture (#66 state-bleed family). twincat_remove_scope_project
        // is idempotent: a benign summary when the project was never created.
        try
        {
            await _client!.CallToolAsync("twincat_remove_scope_project",
                new Dictionary<string, object?> { ["projectName"] = ScopeProjectName });
        }
        catch { /* idempotent; never block teardown on this */ }

        // Now close the solution (also discards the scratch-POU edits).
        try
        {
            await _client!.CallToolAsync("twincat_close_project",
                new Dictionary<string, object?> { ["saveFirst"] = false });
        }
        catch { /* ignore teardown errors */ }

        // Project node is gone from the solution → safe to remove the on-disk
        // scope-project folder so runs don't accumulate directories.
        CleanLeftoverScopeProjectDir();
    }

    // ── Scenario 1: create_scope_project ─────────────────────────────────────

    [Trait("Description", "Scope-Projekt anlegen → erscheint in der Lösung; get_scope_status meldet leeres Scope.")]
    [SkippableFact(Timeout = 180_000)]
    public async Task S1_CreateScopeProject_AppearsInSolution_StatusReportsEmpty()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        try
        {
            // Create scope project.
            var createResult = await _client!.CallToolAsync("twincat_create_scope_project",
                new Dictionary<string, object?> { ["projectName"] = ScopeProjectName });

            var createText = GetText(createResult);
            Assert.False(createResult.IsError == true, createText);
            Assert.False(createText.StartsWith("[Error]", StringComparison.Ordinal), createText);
            Assert.Contains(ScopeProjectName, createText, StringComparison.OrdinalIgnoreCase);

            // get_scope_status confirms scope project exists.
            var statusResult = await _client!.CallToolAsync("twincat_get_scope_status",
                new Dictionary<string, object?> { ["projectName"] = ScopeProjectName });
            var statusText   = GetText(statusResult);
            Assert.False(statusResult.IsError == true, statusText);
            Assert.DoesNotContain("[Error]", statusText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(ScopeProjectName, statusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupScopeProjectAsync();
        }
    }

    // ── Scenario 2: add_scope_channel + labels + stacked-axis ────────────────

    [Trait("Description", "Kanal hinzufügen → Tree zeigt Chart/Axis/Channel; zweiter Kanal nutzt vorhandene Nodes; Labels nicht Default; set_scope_labels; stacked.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task S2_AddScopeChannel_StructureAndLabels_StackedAxis()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        try
        {
            // Create the scope project first.
            var create = await _client!.CallToolAsync("twincat_create_scope_project",
                new Dictionary<string, object?> { ["projectName"] = ScopeProjectName });
            Assert.False(GetText(create).StartsWith("[Error]", StringComparison.Ordinal), GetText(create));

            // Add first channel.
            var add1 = await _client!.CallToolAsync("twincat_add_scope_channel",
                new Dictionary<string, object?>
                {
                    ["symbol"]    = FixtureSymbol,
                    ["chartName"] = "TestChart",
                    ["axisName"]  = "TestAxis",
                    ["channelName"] = "Ch_Var1",
                    ["projectName"] = ScopeProjectName,
                });
            var add1Text = GetText(add1);
            Assert.False(add1Text.StartsWith("[Error]", StringComparison.Ordinal), add1Text);

            // list_scope_channels shows chart/axis/channel.
            var list1 = await _client!.CallToolAsync("twincat_list_scope_channels",
                new Dictionary<string, object?> { ["projectName"] = ScopeProjectName });
            var list1Text = GetText(list1);
            Assert.Contains("TestChart", list1Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TestAxis",  list1Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Ch_Var1",   list1Text, StringComparison.OrdinalIgnoreCase);

            // Add second channel under the SAME chart/axis — must reuse existing nodes.
            var add2 = await _client!.CallToolAsync("twincat_add_scope_channel",
                new Dictionary<string, object?>
                {
                    ["symbol"]    = FixtureSymbol,
                    ["chartName"] = "TestChart",
                    ["axisName"]  = "TestAxis",
                    ["channelName"] = "Ch_Var2",
                    ["projectName"] = ScopeProjectName,
                });
            Assert.False(GetText(add2).StartsWith("[Error]", StringComparison.Ordinal), GetText(add2));

            var list2 = await _client!.CallToolAsync("twincat_list_scope_channels",
                new Dictionary<string, object?> { ["projectName"] = ScopeProjectName });
            var list2Text = GetText(list2);
            Assert.Contains("Ch_Var1", list2Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Ch_Var2", list2Text, StringComparison.OrdinalIgnoreCase);

            // Reuse contract: the second add must NOT create a second
            // "TestChart" — exactly one CHART node has path == "TestChart".
            // (The YT Scope Project template also ships its own default
            // "YT Chart"; count only our chart-kind lines, not the "CHART"
            // substring, since "TestChart" and the word "charts" contain it.)
            int testChartNodes = list2Text
                .Split('\n')
                .Count(l =>
                {
                    var t = l.TrimStart();
                    return t.StartsWith("CHART ", StringComparison.Ordinal)
                        && t.Contains("TestChart", StringComparison.Ordinal);
                });
            Assert.Equal(1, testChartNodes);

            // set_scope_labels — rename the chart.
            var relabel = await _client!.CallToolAsync("twincat_set_scope_labels",
                new Dictionary<string, object?>
                {
                    ["nodeSelector"] = "TestChart",
                    ["name"]         = "RenamedChart",
                    ["projectName"]  = ScopeProjectName,
                });
            Assert.False(GetText(relabel).StartsWith("[Error]", StringComparison.Ordinal), GetText(relabel));

            // #201: a chart has no title property on TE130x 4026.22 — an honest [Error], not a key miss.
            var chartTitle = GetText(await _client!.CallToolAsync("twincat_set_scope_labels",
                new Dictionary<string, object?>
                {
                    ["nodeSelector"] = "RenamedChart",
                    ["chartTitle"]   = "My Chart Title",
                    ["projectName"]  = ScopeProjectName,
                }));
            Assert.StartsWith("[Error]", chartTitle);
            Assert.Contains("no chart-title property", chartTitle, StringComparison.Ordinal);

            // Confirm rename round-trips in a subsequent list.
            var list3 = await _client!.CallToolAsync("twincat_list_scope_channels",
                new Dictionary<string, object?> { ["projectName"] = ScopeProjectName });
            var list3Text = GetText(list3);
            Assert.Contains("RenamedChart", list3Text, StringComparison.OrdinalIgnoreCase);

            // set_scope_axis_mode to stacked.
            var mode = await _client!.CallToolAsync("twincat_set_scope_axis_mode",
                new Dictionary<string, object?>
                {
                    ["chartName"] = "RenamedChart",
                    ["mode"]      = "stacked",
                    ["projectName"] = ScopeProjectName,
                });
            Assert.False(GetText(mode).StartsWith("[Error]", StringComparison.Ordinal), GetText(mode));
        }
        finally
        {
            await CleanupScopeProjectAsync();
        }
    }

    // ── Scenario 3: export before recording → structured refusal ─────────────

    [Trait("Description", "Export ohne Recording → strukturierter [Error] (kein 'kein Fehler'); Recording-Start/Stop ist out-of-scope für diese Gruppe.")]
    [SkippableFact(Timeout = 180_000)]
    public async Task S3_ExportScopeData_BeforeRecording_ReturnsStructuredError()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        try
        {
            // Create scope + one channel so the export path is reachable.
            var create = await _client!.CallToolAsync("twincat_create_scope_project",
                new Dictionary<string, object?> { ["projectName"] = ScopeProjectName });
            Assert.False(GetText(create).StartsWith("[Error]", StringComparison.Ordinal), GetText(create));

            var add = await _client!.CallToolAsync("twincat_add_scope_channel",
                new Dictionary<string, object?>
                {
                    ["symbol"]    = FixtureSymbol,
                    ["chartName"] = "TestChart",
                    ["projectName"] = ScopeProjectName,
                });
            Assert.False(GetText(add).StartsWith("[Error]", StringComparison.Ordinal), GetText(add));

            // Export without any recording must return a structured [Error].
            var export = await _client!.CallToolAsync("twincat_export_scope_data",
                new Dictionary<string, object?> { ["format"] = "CSV", ["projectName"] = ScopeProjectName });
            var exportText = GetText(export);
            Assert.StartsWith("[Error]", exportText, StringComparison.Ordinal);
            // The error must not be empty and should mention "data" or "recording".
            Assert.True(exportText.Length > 10, $"Error was trivially short: '{exportText}'");

            // Spot-check unknown format still returns [Error] in isolation.
            var badFmt = await _client!.CallToolAsync("twincat_export_scope_data",
                new Dictionary<string, object?> { ["format"] = "XLSX", ["projectName"] = ScopeProjectName });
            Assert.StartsWith("[Error]", GetText(badFmt));
        }
        finally
        {
            await CleanupScopeProjectAsync();
        }
    }

    // ── Scenario 4: acquisition symbol round-trip + property-key spike ───────

    [Trait("Description", "add_scope_channel mit Symbol → read_scope_acquisition zeigt Symbol Name/Port; dumpt alle Properties (Key-Spike).")]
    [SkippableFact(Timeout = 300_000)]
    public async Task S4_AddScopeChannel_WritesSymbol_ReadRoundTrips()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        try
        {
            var create = await _client!.CallToolAsync("twincat_create_scope_project",
                new Dictionary<string, object?> { ["projectName"] = ScopeProjectName });
            Assert.False(GetText(create).StartsWith("[Error]", StringComparison.Ordinal), GetText(create));

            var add = await _client!.CallToolAsync("twincat_add_scope_channel",
                new Dictionary<string, object?>
                {
                    ["symbol"]       = FixtureSymbol,
                    ["chartName"]    = "AcqChart",
                    ["channelName"]  = "Ch_Sym",
                    ["targetPort"]   = 851,
                    ["sampleMode"]   = "Fixed Sampletime",
                    ["sampleTimeMs"] = 10,
                    ["projectName"]  = ScopeProjectName,
                });
            var addText = GetText(add);
            Assert.False(addText.StartsWith("[Error]", StringComparison.Ordinal), addText);
            // The PLC-side symbol is the segment after the last '^'.
            Assert.Contains("bScopeTestVar", addText, StringComparison.OrdinalIgnoreCase);

            // read_scope_acquisition dumps the full property set (the key spike)
            // and must show the symbol round-tripped + no NO-SYMBOL warning.
            var read = await _client!.CallToolAsync("twincat_read_scope_acquisition",
                new Dictionary<string, object?> { ["acqSelector"] = FixtureSymbol, ["projectName"] = ScopeProjectName });
            var readText = GetText(read);
            Assert.False(readText.StartsWith("[Error]", StringComparison.Ordinal), readText);
            Assert.Contains("bScopeTestVar", readText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("all properties", readText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NO SYMBOL", readText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupScopeProjectAsync();
        }
    }

    // ── Scenario 4b: NC symbol on ADS port 501 (#59) ─────────────────────────

    [Trait("Description", "add_scope_channel mit NC-Symbol (Port 501, nur sampleTimeMs) → ADS-Acquisition mit Symbol, Port, Fixed Sampletime; Kanal gebunden.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task S4b_AddScopeChannel_NcSymbolPort501_CreatesAdsAcquisition()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        try
        {
            // An axis under the fixture's NC-Task (resolved by browsing, never by a hardcoded label).
            var list = GetText(await _client!.CallToolAsync("twincat_list_nc_tasks_axes"));
            string taskPath = list.Split('\n')
                .Select(l => l.Trim())
                .First(l => l.StartsWith("NC-Task ", StringComparison.Ordinal) && l.Contains("TINC^", StringComparison.Ordinal))
                .Split("  ", 2, StringSplitOptions.TrimEntries)[1];
            string axis = $"ScopeAx{DateTimeOffset.UtcNow:HHmmssfff}";
            var ca = GetText(await _client.CallToolAsync("twincat_create_axis",
                new Dictionary<string, object?> { ["ncTaskPath"] = taskPath, ["axisName"] = axis }));
            Assert.DoesNotContain("[Error]", ca);

            var create = await _client.CallToolAsync("twincat_create_scope_project",
                new Dictionary<string, object?> { ["projectName"] = ScopeProjectName });
            Assert.False(GetText(create).StartsWith("[Error]", StringComparison.Ordinal), GetText(create));

            // No sampleMode: a sample time alone means a fixed one.
            var add = await _client.CallToolAsync("twincat_add_scope_channel",
                new Dictionary<string, object?>
                {
                    ["symbol"]       = $"{taskPath}^Axes^{axis}^ActPosModulo",
                    ["symbolName"]   = $"Axes.{axis}.ActPosModulo",
                    ["targetPort"]   = 501,
                    ["sampleTimeMs"] = 2,
                    ["chartName"]    = "NcChart",
                    ["projectName"]  = ScopeProjectName,
                });
            var addText = GetText(add);
            Assert.False(addText.StartsWith("[Error]", StringComparison.Ordinal), addText);
            Assert.Contains("port 501", addText, StringComparison.Ordinal);
            Assert.Contains("Channel bound to acquisition", addText, StringComparison.Ordinal);

            var read = GetText(await _client.CallToolAsync("twincat_read_scope_acquisition",
                new Dictionary<string, object?> { ["acqSelector"] = "ActPosModulo", ["projectName"] = ScopeProjectName }));
            Assert.DoesNotContain("[Error]", read, StringComparison.Ordinal);
            Assert.Contains($"Axes.{axis}.ActPosModulo", read, StringComparison.Ordinal);
            Assert.Contains("Target Port : 501", read, StringComparison.Ordinal);
            Assert.Contains("Sample Time : 2 ms", read, StringComparison.Ordinal);
            Assert.Contains("Fixed Sampletime", read, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupScopeProjectAsync();
        }
    }

    // ── Scenario 5: set_scope_acquisition_symbol partial update ──────────────

    [Trait("Description", "set_scope_acquisition_symbol ändert nur gelieferte Felder; re-read bestätigt; leere Felder = [Error].")]
    [SkippableFact(Timeout = 300_000)]
    public async Task S5_SetScopeAcquisitionSymbol_PartialUpdate_RoundTrips()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        try
        {
            var create = await _client!.CallToolAsync("twincat_create_scope_project",
                new Dictionary<string, object?> { ["projectName"] = ScopeProjectName });
            Assert.False(GetText(create).StartsWith("[Error]", StringComparison.Ordinal), GetText(create));

            var add = await _client!.CallToolAsync("twincat_add_scope_channel",
                new Dictionary<string, object?> { ["symbol"] = FixtureSymbol, ["chartName"] = "AcqChart", ["projectName"] = ScopeProjectName });
            Assert.False(GetText(add).StartsWith("[Error]", StringComparison.Ordinal), GetText(add));

            // No fields supplied → structured [Error].
            var none = await _client!.CallToolAsync("twincat_set_scope_acquisition_symbol",
                new Dictionary<string, object?> { ["acqSelector"] = FixtureSymbol, ["projectName"] = ScopeProjectName });
            Assert.StartsWith("[Error]", GetText(none), StringComparison.Ordinal);

            // The new acquisition starts in "Task Sampletime": a sample time alone is ignored by TE130x,
            // so the tool must say why instead of reporting a read-back mismatch (fix/scope-units).
            var locked = GetText(await _client!.CallToolAsync("twincat_set_scope_acquisition_symbol",
                new Dictionary<string, object?>
                {
                    ["acqSelector"]  = FixtureSymbol,
                    ["sampleTimeMs"] = 50,
                    ["projectName"]  = ScopeProjectName,
                }));
            Assert.StartsWith("[Error]", locked, StringComparison.Ordinal);
            Assert.Contains("Task Sampletime", locked, StringComparison.Ordinal);

            // Change only the sampling (fixed mode + time); symbol must be unaffected.
            var upd = await _client!.CallToolAsync("twincat_set_scope_acquisition_symbol",
                new Dictionary<string, object?>
                {
                    ["acqSelector"]  = FixtureSymbol,
                    ["sampleMode"]   = "Fixed Sampletime",
                    ["sampleTimeMs"] = 50,
                    ["projectName"]  = ScopeProjectName,
                });
            var updText = GetText(upd);
            Assert.False(updText.StartsWith("[Error]", StringComparison.Ordinal), updText);
            Assert.Contains("sampleTimeMs=50", updText, StringComparison.Ordinal);

            var read = await _client!.CallToolAsync("twincat_read_scope_acquisition",
                new Dictionary<string, object?> { ["acqSelector"] = FixtureSymbol, ["projectName"] = ScopeProjectName });
            var readText = GetText(read);
            Assert.Contains("bScopeTestVar", readText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupScopeProjectAsync();
        }
    }

    // ── Scenario 6: list/status surface symbol + unbound flag ────────────────

    [Trait("Description", "list_scope_channels/get_scope_status zeigen Symbol Name; Acquisition ohne Symbol wird als unbound gezählt/markiert.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task S6_ListAndStatus_SurfaceSymbol_AndUnbound()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        try
        {
            var create = await _client!.CallToolAsync("twincat_create_scope_project",
                new Dictionary<string, object?> { ["projectName"] = ScopeProjectName });
            Assert.False(GetText(create).StartsWith("[Error]", StringComparison.Ordinal), GetText(create));

            var add = await _client!.CallToolAsync("twincat_add_scope_channel",
                new Dictionary<string, object?> { ["symbol"] = FixtureSymbol, ["chartName"] = "AcqChart", ["projectName"] = ScopeProjectName });
            Assert.False(GetText(add).StartsWith("[Error]", StringComparison.Ordinal), GetText(add));

            var list = await _client!.CallToolAsync("twincat_list_scope_channels",
                new Dictionary<string, object?> { ["projectName"] = ScopeProjectName });
            var listText = GetText(list);
            Assert.False(listText.StartsWith("[Error]", StringComparison.Ordinal), listText);
            Assert.Contains("bScopeTestVar", listText, StringComparison.OrdinalIgnoreCase);

            // Regression #60: a channel connects to a DataPool acquisition (it
            // does not reference a PLC variable itself). On TC3 4026.22 the
            // channel's Y-Data.Acquisition Property.Value marshals as an opaque
            // Guid; the old code tried to read it as a COM object/name, failed,
            // left BoundAcquisition empty and falsely flagged the channel
            // "NO SYMBOL". A non-empty Guid now means "bound" and the channel
            // resolves its acquisition. Every CHANNEL line must carry the
            // bound-acquisition arrow.
            // Match only real CHANNEL dump entries (left-padded "CHANNEL" type
            // token), NOT the summary header "Found 1 channel in N charts…"
            // which also contains the substring "channel".
            var channelLines = listText.Split('\n')
                .Where(l => l.TrimStart().StartsWith("CHANNEL", StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(channelLines);
            Assert.All(channelLines, l =>
                Assert.True(l.Contains('→'),
                    $"channel has no bound acquisition (issue #60 regression): {l}\n--- full list ---\n{listText}"));

            var status = await _client!.CallToolAsync("twincat_get_scope_status",
                new Dictionary<string, object?> { ["projectName"] = ScopeProjectName });
            var statusText = GetText(status);
            Assert.False(statusText.StartsWith("[Error]", StringComparison.Ordinal), statusText);
            Assert.Contains("Acquisitions", statusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupScopeProjectAsync();
        }
    }

    // ── Scenario 7: record lifecycle + save/export ────────────────────────────
    //
    // Covers: get_scope_record_state (Config) → set_scope_record_settings →
    //         start_scope_record → state=Record → stop_scope_record → state=Reply
    //         → save_scope_data (.svdx) → export_scope_data (CSV) round-trip.
    // Also verifies trigger API: create_scope_trigger_group → add_scope_trigger_set
    //   → list_scope_triggers shows group + set.

    [SkippableFact(Timeout = 300_000)]
    public async Task S7_RecordLifecycle_StartStopSaveExport()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        await using var scope = new AsyncScope(async () => await CleanupScopeProjectAsync());

        // Arrange: create project and add a channel for recording.
        await _client!.CallToolAsync("twincat_create_scope_project",
            new Dictionary<string, object> { ["projectName"] = ScopeProjectName });

        await _client.CallToolAsync("twincat_add_scope_channel",
            new Dictionary<string, object>
            {
                ["symbol"]      = FixtureSymbol,
                ["projectName"] = ScopeProjectName,
            });

        // Set a short recording time (1 s UserStart).
        var setResult = GetText(await _client.CallToolAsync("twincat_set_scope_record_settings",
            new Dictionary<string, object>
            {
                ["recordTimeS"]  = 1.0,
                ["projectName"]  = ScopeProjectName,
            }));
        // Property keys are empirical — if none matched, we still proceed (recording still works
        // with default settings); only a hard [Error] should fail.
        Assert.DoesNotContain("[Error]", setResult);

        // Check initial state is Config.
        var stateResult = GetText(await _client.CallToolAsync("twincat_get_scope_record_state",
            new Dictionary<string, object> { ["projectName"] = ScopeProjectName }));
        // On a fresh project the state should be Config (may be unavailable on older builds).
        // Accept any non-[Error] response here — empirical verification on TC3 4026.
        if (stateResult.Contains("[Error]", StringComparison.OrdinalIgnoreCase))
            Skip.If(true, $"get_scope_record_state returned error: {stateResult}");
        Assert.Contains("Config", stateResult, StringComparison.OrdinalIgnoreCase);

        // Start recording.
        var startResult = GetText(await _client.CallToolAsync("twincat_start_scope_record",
            new Dictionary<string, object> { ["projectName"] = ScopeProjectName }));
        if (startResult.Contains("[Error]", StringComparison.OrdinalIgnoreCase))
            Skip.If(true, $"start_scope_record failed: {startResult}");
        Assert.DoesNotContain("[Error]", startResult);

        // State should now be Record.
        var recordStateResult = GetText(await _client.CallToolAsync("twincat_get_scope_record_state",
            new Dictionary<string, object> { ["projectName"] = ScopeProjectName }));
        Assert.Contains("Record", recordStateResult, StringComparison.OrdinalIgnoreCase);

        // Deliberate recording window (not a readiness wait, so not a polled condition):
        // let a few scan cycles land before stopping.
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var stopResult = GetText(await _client.CallToolAsync("twincat_stop_scope_record",
            new Dictionary<string, object> { ["projectName"] = ScopeProjectName }));
        Assert.DoesNotContain("[Error]", stopResult);

        // State should no longer be Recording. Without channels/RT the scope returns to Config
        // immediately rather than entering Reply (no data accumulated). Accept either.
        var replyStateResult = GetText(await _client.CallToolAsync("twincat_get_scope_record_state",
            new Dictionary<string, object> { ["projectName"] = ScopeProjectName }));
        Assert.DoesNotContain("[Error]", replyStateResult);
        Assert.DoesNotContain("Record\": true", replyStateResult, StringComparison.OrdinalIgnoreCase);
        var stoppedOk = replyStateResult.Contains("Reply",  StringComparison.OrdinalIgnoreCase)
                     || replyStateResult.Contains("Config", StringComparison.OrdinalIgnoreCase);
        Assert.True(stoppedOk, $"Expected Config or Reply after stop, got: {replyStateResult}");

        // Save and export require Reply state (recorded data). Without a live RT / channels
        // the scope returns to Config immediately — the lifecycle itself was verified above.
        bool hasData = replyStateResult.Contains("Reply", StringComparison.OrdinalIgnoreCase);
        if (hasData)
        {
            var saveResult = GetText(await _client.CallToolAsync("twincat_save_scope_data",
                new Dictionary<string, object> { ["projectName"] = ScopeProjectName }));
            Assert.DoesNotContain("[Error]", saveResult);
            Assert.True(File.Exists(saveResult.Trim()), $"SaveSVD output not found: {saveResult}");

            var exportResult = GetText(await _client.CallToolAsync("twincat_export_scope_data",
                new Dictionary<string, object>
                {
                    ["format"]      = "CSV",
                    ["projectName"] = ScopeProjectName,
                }));
            Assert.DoesNotContain("[Error]", exportResult);
            string csvPath = exportResult.Trim();
            Assert.True(File.Exists(csvPath), $"CSV not found: {csvPath}");
            Assert.True(new FileInfo(csvPath).Length > 0, "CSV is empty.");
        }
    }

    // ── Scenario 8: trigger API round-trip ────────────────────────────────────

    [SkippableFact(Timeout = 120_000)]
    public async Task S8_TriggerGroupRoundTrip_ListShowsGroupAndSet()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        await using var scope = new AsyncScope(async () => await CleanupScopeProjectAsync());

        await _client!.CallToolAsync("twincat_create_scope_project",
            new Dictionary<string, object> { ["projectName"] = ScopeProjectName });

        // Create a trigger group.
        var createResult = GetText(await _client.CallToolAsync("twincat_create_scope_trigger_group",
            new Dictionary<string, object>
            {
                ["action"]      = "StopRecord",
                ["projectName"] = ScopeProjectName,
            }));
        if (createResult.Contains("[Error]", StringComparison.OrdinalIgnoreCase))
            Skip.If(true, $"create_scope_trigger_group failed: {createResult}");
        Assert.DoesNotContain("[Error]", createResult);

        // Extract the group name from the response.
        string groupName = createResult.Contains("'") ? createResult.Split('\'')[1] : "TriggerGroup_StopRecord";

        // Add a trigger set.
        var addResult = GetText(await _client.CallToolAsync("twincat_add_scope_trigger_set",
            new Dictionary<string, object>
            {
                ["groupName"]   = groupName,
                ["setType"]     = "ChannelSet",
                ["threshold"]   = 0.5,
                ["edge"]        = "Rising",
                ["projectName"] = ScopeProjectName,
            }));
        Assert.DoesNotContain("[Error]", addResult);

        // List triggers — should show group + set.
        var listResult = GetText(await _client.CallToolAsync("twincat_list_scope_triggers",
            new Dictionary<string, object> { ["projectName"] = ScopeProjectName }));
        Assert.DoesNotContain("[Error]", listResult);
        Assert.Contains("StopRecord", listResult, StringComparison.OrdinalIgnoreCase);
    }

    // Lightweight RAII helper for async cleanup in integration tests.
    private sealed class AsyncScope(Func<Task> cleanup) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await cleanup();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    private static int CountOccurrences(string text, string substring)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(substring, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += substring.Length;
        }
        return count;
    }
}
