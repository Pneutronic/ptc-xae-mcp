using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Exercises the build/activate MCP surface against a live XAE and MCP server.
/// Requires PTC_XAE_MCP_FIXTURE_SLN; skipped when not set.
///
/// <para><b>Safety rule (see feedback_integration_never_mutate_shared_main).</b>
/// Error-injection tests must NEVER mutate the fixture's real MAIN body. They
/// inject broken code into the dedicated <see cref="ScratchPou"/>
/// (PRG_IntegrationScratch) — a logic-free POU made build-reachable by one
/// idempotent, sentinel-tagged hook line appended to MAIN. The scratch body is
/// restored after every test; MAIN's declaration and real logic are never
/// touched. <see cref="ScratchPou.EnsureAsync"/> bootstraps both across any
/// fixture project.</para>
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class BuildIntegrationTests : IAsyncLifetime
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
            new Dictionary<string, object?>
            {
                ["slnPath"] = _fixtureSln,
                ["closeCurrentFirst"] = true,
            });
        Assert.False(open.IsError == true, GetText(open));
    }

    private async Task<string> BuildPlc() =>
        GetText(await _client!.CallToolAsync("twincat_build_plc", new Dictionary<string, object?>()));

    private async Task<string> BuildSolution() =>
        GetText(await _client!.CallToolAsync("twincat_build_solution", new Dictionary<string, object?>()));

    // Inject broken code into the dedicated, build-reachable scratch POU
    // (PRG_IntegrationScratch, called from MAIN via the tagged hook so it IS
    // compiled), assert the build FAILS, then restore the scratch POU's safe
    // body and assert it builds again. We assert only the build verdict:
    // twincat_build_plc returns "Build failed (failedProjects=N)" /
    // "Build succeeded (failedProjects=0)" — the per-error detail comes from a
    // COM ErrorList read that intermittently hits RPC_E_SERVERCALL_RETRYLATER,
    // so asserting a specific compiler-message substring is not reliable.
    // MAIN itself is never mutated beyond the idempotent one-line hook.
    private async Task ErrorInjectDecl(string varBlock)
    {
        await ScratchPou.EnsureAsync(_client!);
        try
        {
            await ScratchPou.WriteAsync(_client!,
                $"PROGRAM {ScratchPou.Name}\nVAR {varBlock} END_VAR", ScratchPou.SafeImpl);

            var buildResult = await ScratchPou.SaveAndBuildAsync(_client!);
            Assert.Contains("Build failed", buildResult, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await ScratchPou.ResetAsync(_client!);
        }

        // Deterministic restore: clears any stale Error List + bounded retry so
        // a sibling test's state cannot make this verdict flake (issue #70).
        var restore = await ScratchPou.RestoreAndVerifyBuildAsync(_client!);
        Assert.Contains("Build succeeded", restore, StringComparison.OrdinalIgnoreCase);
    }

    private const string SafeVars =
        "nVal : INT; bFlag : BOOL; rVal : REAL; sText : STRING; tDelay : TIME; byData : BYTE;";

    private async Task ErrorInjectImpl(string brokenImpl)
    {
        await ScratchPou.EnsureAsync(_client!);
        try
        {
            await ScratchPou.WriteAsync(_client!,
                $"PROGRAM {ScratchPou.Name}\nVAR {SafeVars} END_VAR", brokenImpl);

            var buildResult = await ScratchPou.SaveAndBuildAsync(_client!);
            Assert.Contains("Build failed", buildResult, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await ScratchPou.ResetAsync(_client!);
        }

        // Deterministic restore: clears any stale Error List + bounded retry so
        // a sibling test's state cannot make this verdict flake (issue #70).
        var restore = await ScratchPou.RestoreAndVerifyBuildAsync(_client!);
        Assert.Contains("Build succeeded", restore, StringComparison.OrdinalIgnoreCase);
    }

    // ── Baseline builds (read-only, safe) ─────────────────────────────────────

    [Trait("Description", "Sauber geladenes Projekt lässt sich per twincat_build_plc fehlerfrei bauen.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task BuildPlc_CleanProject_Succeeds()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        await ScratchPou.EnsureAsync(_client!);

        var result = await BuildPlc();

        Assert.Contains("Build succeeded", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failedProjects=1", result);
    }

    [Trait("Description", "Sauber geladenes Projekt lässt sich per twincat_build_solution (inkl. SPS + HPS) fehlerfrei bauen.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task BuildSolution_CleanProject_Succeeds()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        await ScratchPou.EnsureAsync(_client!);

        var result = await BuildSolution();

        Assert.Contains("Build succeeded", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failedProjects=1", result);
    }

    [Trait("Description", "twincat_build_plc(expectSymbols): GVL-Symbol, Member einer FB-Instanz und Programm werden in der .tmc gefunden; ein nie deklarierter Name macht das Ergebnis zum [Error] mit linkalways-Hinweis.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task BuildPlc_ExpectSymbols_FindsCompiledNamesAndFlagsTheMissingOne()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        await ScratchPou.EnsureAsync(_client!);
        string missing = FixtureLayout.GvlName + ".nNeverDeclared";

        string result = GetText(await _client!.CallToolAsync("twincat_build_plc", new Dictionary<string, object?>
        {
            ["expectSymbols"] = new[] { FixtureLayout.UintSymbol, FixtureLayout.StepChainStepMember, FixtureLayout.GvlName, missing },
        }));

        Assert.Contains("Build succeeded", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"[Error] Expected symbols: 1 of 4 missing from the compiled PLC: {missing}.", result);
        Assert.Contains("{attribute 'linkalways'}", result);
        Assert.Contains($"{FixtureLayout.UintSymbol} (symbol)", result);
        Assert.Contains($"{FixtureLayout.StepChainStepMember} (member of {FixtureLayout.StepChainInstanceSymbol})", result);
        Assert.Contains($"{FixtureLayout.GvlName} (", result);
    }

    // ── Category 1: Type system errors (declaration) ──────────────────────────

    [Trait("Description", "Unbekannter Datentyp in der Deklaration → Build muss fehlschlagen.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Decl_UnknownDataType_ReportsError()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        await ErrorInjectDecl("x : NONEXISTENT_TYPE_XYZ;");
    }

    [Trait("Description", "Doppelt deklarierte Variable → Build muss fehlschlagen.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Decl_DuplicateVariable_ReportsError()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        await ErrorInjectDecl("n : INT; n : BOOL;");
    }

    [Trait("Description", "Reserviertes Schlüsselwort als Variablenname (z. B. IF) → Build muss fehlschlagen.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Decl_ReservedKeywordAsName_ReportsError()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        // A reserved-keyword-as-name only needs to fail the build.
        await ErrorInjectDecl("IF : INT;");
    }

    // ── Category 2: Type assignment / conversion errors (implementation) ───────

    [Trait("Description", "String-Literal einer INT-Variable zuweisen → Typfehler, Build schlägt fehl.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Impl_AssignStringToInt_ReportsError()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        await ErrorInjectImpl("nVal := 'hello';");
    }

    [Trait("Description", "REAL-Literal einer BOOL-Variable zuweisen → Typfehler, Build schlägt fehl.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Impl_AssignRealToBool_ReportsError()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        await ErrorInjectImpl("bFlag := 3.14;");
    }

    [Trait("Description", "Nicht deklarierte Variable im Implementation-Teil → Build muss fehlschlagen.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Impl_UndeclaredVariable_ReportsError()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        await ErrorInjectImpl("ghost := 1;");
    }

    [Trait("Description", "get_error_list nach Rebuild mit Fehler + {warning}-Pragma: Einträge vorhanden und als ERROR bzw. WARNING klassifiziert (#178).")]
    [SkippableFact(Timeout = 300_000)]
    public async Task ErrorList_AfterRebuild_ClassifiesErrorAndWarning()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        await ScratchPou.EnsureAsync(_client!);
        string listText;
        try
        {
            await ScratchPou.WriteAsync(_client!,
                $"PROGRAM {ScratchPou.Name}\nVAR {SafeVars} END_VAR",
                "{warning 'probe178 warning'}\nghost := 1;");
            var save = await _client!.CallToolAsync("twincat_save_project", new Dictionary<string, object?>());
            Assert.False(save.IsError == true, $"save before rebuild failed: {GetText(save)}");
            // Rebuild, not build: an incremental build does not re-emit pragma warnings (seen live).
            var rebuild = GetText(await _client.CallToolAsync("twincat_rebuild_plc", new Dictionary<string, object?>()));
            Assert.Contains("Build failed", rebuild, StringComparison.OrdinalIgnoreCase);

            listText = GetText(await _client.CallToolAsync("twincat_get_error_list", new Dictionary<string, object?>()));
        }
        finally
        {
            await ScratchPou.ResetAsync(_client!);
        }

        // Before #178 the list read as empty; on 4026.22 errors and warnings both carry ErrorLevel 2.
        Assert.DoesNotContain("Error list is empty", listText, StringComparison.Ordinal);
        var lines = listText.Split('\n');
        Assert.Contains(lines, l => l.StartsWith("[ERROR]", StringComparison.Ordinal) && l.Contains("ghost", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("[WARNING]", StringComparison.Ordinal) && l.Contains("probe178 warning", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("[WARNING]", StringComparison.Ordinal) && l.Contains("ghost", StringComparison.Ordinal));

        var restore = await ScratchPou.RestoreAndVerifyBuildAsync(_client!);
        Assert.Contains("Build succeeded", restore, StringComparison.OrdinalIgnoreCase);
    }

    // ── Category 4: Control-flow / syntax errors ───────────────────────────────

    [Trait("Description", "Fehlendes Semikolon am Ende einer Anweisung → Syntaxfehler, Build schlägt fehl.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Syntax_MissingSemicolon_ReportsError()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        await ErrorInjectImpl("nVal := 1");
    }

    [Trait("Description", "IF-Block ohne END_IF → Syntaxfehler, Build schlägt fehl.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Syntax_UnclosedIf_ReportsError()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        await ErrorInjectImpl("IF nVal > 0 THEN nVal := 1;");
    }

    // ── xae-error-list: ClearErrorList ────────────────────────────────────────

    [Trait("Description", "twincat_clear_error_list: nach einem fehlgeschlagenen Build die Fehlerliste löschen → Liste ist danach nachweislich leer (kein No-op).")]
    [SkippableFact(Timeout = 300_000)]
    public async Task ClearErrorList_AfterFailedBuild_ActuallyEmptiesList()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        // Inject a build error to populate the Error List, then restore.
        await ScratchPou.EnsureAsync(_client!);
        try
        {
            await ScratchPou.WriteAsync(_client!,
                $"PROGRAM {ScratchPou.Name}\nVAR ghost_undeclared_x_9z : NONEXISTENT_TYPE_XYZ; END_VAR",
                ScratchPou.SafeImpl);
            var failResult = await ScratchPou.SaveAndBuildAsync(_client!);
            Assert.Contains("Build failed", failResult, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await ScratchPou.ResetAsync(_client!);
        }

        // Now clear the error list. The tool self-verifies via a pre/post
        // ErrorItems.Count delta, so a no-op now returns [Error] "... entries
        // remain ..." rather than a false success — the spec's Test-plan
        // step 3 / Gate 0 ("re-read and assert the count dropped") is enforced
        // inside the tool. Asserting no [Error] here therefore proves the list
        // was genuinely emptied, not merely that the command did not throw.
        var clearResult = await _client!.CallToolAsync("twincat_clear_error_list");
        var clearText = GetText(clearResult);

        Assert.False(clearResult.IsError == true, $"twincat_clear_error_list returned error: {clearText}");
        Assert.DoesNotContain("[Error]", clearText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remain", clearText, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            clearText.Contains("Cleared", StringComparison.OrdinalIgnoreCase) ||
            clearText.Contains("already empty", StringComparison.OrdinalIgnoreCase),
            $"Expected a verified clear, got: {clearText}");
    }

    // ── Check all objects ─────────────────────────────────────────────────────

    [Trait("Description", "twincat_plc_check_all_objects: eine nie aufgerufene Funktion mit nicht deklarierter Variable baut mit 0 Fehlern, Check all objects meldet den Fehler; nach dem Löschen der Funktion keine Fehler.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task CheckAllObjects_FindsTheErrorInAnUncalledFunction_ThatTheBuildMisses()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        const string name = "F_CheckAllUnreached";
        string path = FixtureLayout.PousFolder + "^" + name;

        var created = await _client!.CallToolAsync("twincat_plc_create", new Dictionary<string, object?>
        {
            ["kind"] = "POU", ["subtype"] = "FUNCTION", ["parentPath"] = FixtureLayout.PousFolder,
            ["name"] = name, ["returnType"] = "INT",
        });
        Assert.StartsWith("Created", GetText(created));
        try
        {
            string written = GetText(await _client.CallToolAsync("twincat_plc_write", new Dictionary<string, object?>
            {
                ["path"] = path, ["kind"] = "POU", ["implementation"] = $"{name} := nCheckAllUndeclared + 1;",
            }));
            Assert.StartsWith("Wrote", written);

            Assert.Contains("Build succeeded", await BuildPlc(), StringComparison.OrdinalIgnoreCase);

            string check = GetText(await _client.CallToolAsync("twincat_plc_check_all_objects", new Dictionary<string, object?>()));
            Assert.Contains("[Error] Check all objects failed.", check);
            Assert.Contains("nCheckAllUndeclared", check);
            Assert.Contains(name, check);
            // Without bringToFront the check runs through the Automation Interface and leaves the XAE behind.
            Assert.Contains("Check: ran through the Automation Interface", check);
        }
        finally
        {
            await _client.CallToolAsync("twincat_plc_delete", new Dictionary<string, object?> { ["path"] = path, ["kind"] = "POU" });
        }

        string clean = GetText(await _client.CallToolAsync("twincat_plc_check_all_objects", new Dictionary<string, object?>()));
        Assert.Contains("Check all objects: no errors", clean);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}

