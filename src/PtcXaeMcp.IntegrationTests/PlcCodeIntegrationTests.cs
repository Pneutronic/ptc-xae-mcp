using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Exercises the PLC source-code MCP surface (twincat_plc_list / _read / _write / _create / _delete)
/// against the live MCP server and XAE with the neutral fixture (<see cref="FixtureLayout"/>); the ST
/// POU under test defaults to its MAIN and can be overridden with PTC_XAE_MCP_FIXTURE_POU.
/// Round-trip tests capture the existing implementation before mutating, so they
/// restore in-memory state on a clean run; abort mid-test still leaves XAE
/// in-memory state for manual revert. Every element a scenario creates carries a
/// time-stamped name and is deleted in a finally block.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class PlcCodeIntegrationTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost:5010/";
    private const string PouEnvVar = "PTC_XAE_MCP_FIXTURE_POU";
    private static string DefaultParentPath => FixtureLayout.PousFolder;
    private static string DutParentPath => FixtureLayout.DutsFolder;
    private static string GvlParentPath => FixtureLayout.GvlsFolder;

    private McpClient? _client;
    private bool _ready;
    private string _fixtureSln = string.Empty;
    private string _pouPath = string.Empty;

    public async Task InitializeAsync()
    {
        var fixture = LiveXae.FixtureSln();
        if (fixture is null) { LiveXae.Announce(false, string.Empty); return; }
        _fixtureSln = fixture;

        var pouOverride = Environment.GetEnvironmentVariable(PouEnvVar);
        _pouPath = string.IsNullOrWhiteSpace(pouOverride) ? FixtureLayout.MainPath : pouOverride;

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

        // The just-(re)opened solution can momentarily report no resolvable PLC
        // project: a sibling test's close/reopen, or the editor not being fully
        // loaded yet, leaves twincat_plc_list transiently returning "No matching
        // PLC elements found." / an editor-not-ready [Error]. This is the
        // suite-order state-bleed window of issue #66 (same family as #56/#73/
        // #76). Block every scenario until the project settles so no test runs
        // against the transient empty view. The fixture always has POUs, so an
        // empty/error listing here is definitively the transient, never truth.
        await PlcProjectReadiness.AwaitAsync(_client!);
    }

    // ── Scenario 1: plc_list reports at least one POU ─────────────────────────

    [Trait("Description", "PLC-Elemente auflisten (plc_list) → Antwort enthält mindestens eine POU-Zeile mit TIPC-Pfad.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task PlcList_AfterOpen_ReturnsAtLeastOnePou()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var result = await _client!.CallToolAsync("twincat_plc_list");
        var text = GetText(result);

        Assert.False(result.IsError == true, $"plc_list error: {text}");
        Assert.StartsWith("Found ", text);
        Assert.Contains("POU", text); // header summary counts the POUs
        // The default fixture has MAIN; for overridden POU paths, just assert at
        // least one POU line is present in the output.
        Assert.Matches(@"\bPOU/\S+\s+TIPC\^", text);
    }

    [Trait("Description", "plc_list mit limit=2 seitenweise durchblättern → dieselben Zeilen wie ohne limit, letzte Seite has_more false.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task PlcList_Paged_VisitsEveryElementOnce()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        static List<string> Lines(string text) =>
            text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("  ", StringComparison.Ordinal)).ToList();

        var full = Lines(GetText(await _client!.CallToolAsync("twincat_plc_list")));
        Assert.True(full.Count > 2, $"the fixture lists only {full.Count} elements");

        var paged = new List<string>();
        int? offset = 0;
        for (int guard = 0; offset is { } o && guard < 100; guard++)
        {
            var text = GetText(await _client!.CallToolAsync("twincat_plc_list",
                new Dictionary<string, object?> { ["limit"] = 2, ["offset"] = o }));
            paged.AddRange(Lines(text));
            var footer = Regex.Match(text, @"Page: offset \d+, returned \d+ of (\d+); has_more (true|false)(, next_offset (\d+))?");
            Assert.True(footer.Success, $"no paging line: {text}");
            Assert.Equal(full.Count, int.Parse(footer.Groups[1].Value, CultureInfo.InvariantCulture));
            offset = footer.Groups[4].Success ? int.Parse(footer.Groups[4].Value, CultureInfo.InvariantCulture) : null;
        }
        Assert.Equal(full, paged);
    }

    [Trait("Description", "plc_read mit paths (MAIN + ein falscher Pfad) → MAIN gelesen, der falsche als [Error] mit Next-Hinweis, Kopfzeile 1 von 2.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task PlcRead_Paths_ReadsTheGoodOnes_AndReportsTheBadOne()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string missing = FixtureLayout.PousFolder + "^DoesNotExist";
        var text = GetText(await _client!.CallToolAsync("twincat_plc_read", new Dictionary<string, object?>
        {
            ["paths"] = new[] { _pouPath, missing },
            ["kind"] = "POU",
        }));

        Assert.StartsWith("Read 1 of 2 elements (1 failed).", text);
        Assert.Contains($"=== {_pouPath} ===", text);
        Assert.Contains("--- Implementation ---", text);
        Assert.Contains($"=== {missing} ===\n[Error] PLC tree path not found", text.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("Next: twincat_plc_list", text);
    }

    [Trait("Description", "Resource ptc-xae://plc/element/<MAIN, URI-kodiert> lesen → derselbe Text und dieselbe Revision wie plc_read.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task ElementResource_ReturnsWhatPlcReadReturns()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string viaTool = await ReadAsync(_pouPath, "POU");
        var resource = await _client!.ReadResourceAsync("ptc-xae://plc/element/" + Uri.EscapeDataString(_pouPath));
        string viaResource = string.Join("\n", resource.Contents.OfType<TextResourceContents>().Select(c => c.Text));

        Assert.Equal(viaTool, viaResource);
    }

    // ── Scenario 2: plc_read returns declaration + implementation ─────────────

    [Trait("Description", "Bekannten POU per plc_read lesen → Kopfzeile mit Revision, \"--- Declaration ---\" und \"--- Implementation ---\" mit POU-Schlüsselwort.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task PlcRead_OnKnownPou_ReturnsDeclarationAndImplementation()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var text = await ReadAsync(_pouPath, "POU");

        Assert.Matches(@"^Read POU/\S+ '.+'.* Revision: [0-9a-f]{16}", text.Split('\n')[0]);
        Assert.Contains("--- Declaration ---", text);
        Assert.Contains("--- Implementation ---", text);

        // Declaration body should start with one of the POU header keywords.
        var declStart = text.IndexOf("--- Declaration ---", StringComparison.Ordinal);
        var declTail = text.Substring(declStart);
        bool hasKeyword = declTail.Contains("PROGRAM ", StringComparison.OrdinalIgnoreCase)
                       || declTail.Contains("FUNCTION_BLOCK", StringComparison.OrdinalIgnoreCase)
                       || declTail.Contains("FUNCTION ", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasKeyword, $"Declaration didn't contain POU keyword: {declTail}");
    }

    // ── Scenario 3+4: plc_write round-trip on a THROWAWAY POU ─────────────────
    // Never mutates the fixture's real MAIN (see
    // feedback_integration_never_mutate_shared_main). Creates its own PROGRAM,
    // round-trips its body, and deletes it in finally.

    [Trait("Description", "Wegwerf-PROGRAM per plc_create anlegen, Rumpf per plc_write überschreiben und zurücklesen → Round-Trip exakt identisch.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task PlcWrite_RoundTrip_RestoresOriginal()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string prg = $"PRG_PlcCodeSpec_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string prgPath = $"{DefaultParentPath}^{prg}";

        try
        {
            await OkAsync("twincat_plc_create", new Dictionary<string, object?>
            {
                ["kind"] = "POU", ["subtype"] = "PROGRAM", ["parentPath"] = DefaultParentPath, ["name"] = prg,
            });

            // Seed a known body, then read it back as our "original".
            string seed = "n := n + 1;";
            await OkAsync("twincat_plc_write", new Dictionary<string, object?>
            {
                ["path"] = prgPath, ["kind"] = "POU",
                ["declaration"] = $"PROGRAM {prg}\nVAR n : INT; END_VAR",
                ["implementation"] = seed,
            });
            string originalImpl = ExtractImplementation(await ReadAsync(prgPath, "POU"));

            // Overwrite with a sentinel; read-back asserts it landed.
            const string sentinel = "n := 42; // round-trip sentinel";
            string w1 = await OkAsync("twincat_plc_write", new Dictionary<string, object?>
            {
                ["path"] = prgPath, ["kind"] = "POU", ["implementation"] = sentinel,
            });
            Assert.StartsWith($"Wrote POU '{prgPath}'.", w1);
            Assert.Contains("round-trip sentinel", await ReadAsync(prgPath, "POU"));

            // Restore the captured original; re-read should match.
            await OkAsync("twincat_plc_write", new Dictionary<string, object?>
            {
                ["path"] = prgPath, ["kind"] = "POU", ["implementation"] = originalImpl,
            });

            string restoredImpl = ExtractImplementation(await ReadAsync(prgPath, "POU"));
            Assert.Equal(Normalise(originalImpl), Normalise(restoredImpl));
        }
        finally { await TryDeleteAsync(prgPath, "POU"); }
    }

    // ── Issue #148: destructive / silent-fallback inputs are refused ─────────

    [Trait("Description", "plc_delete auf PLC-Projekt-Root (Systemknoten) und den nicht leeren POUs-Ordner wird verweigert; Projekt bleibt intakt.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task PlcDelete_ProjectRootAndFolder_AreRefused_ProjectIntact()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        // The project root is a PLC system node: refused by the kind check, whatever kind is claimed.
        string root = await CallAsync("twincat_plc_delete", new Dictionary<string, object?>
        {
            ["path"] = FixtureLayout.PlcRoot, ["kind"] = "FOLDER",
        });
        Assert.StartsWith("[Error]", root);
        Assert.True(root.Contains("system node", StringComparison.Ordinal) ||
                    root.Contains("Cannot verify kind", StringComparison.Ordinal),
            $"Expected the system-node / unreadable-type refusal, got: {root}");

        // The POUs folder is a FOLDER with content: refused without recursive=true (either "is not empty" or
        // "cannot tell whether it is empty" — both name recursive=true and change nothing).
        string folder = await CallAsync("twincat_plc_delete", new Dictionary<string, object?>
        {
            ["path"] = DefaultParentPath, ["kind"] = "FOLDER",
        });
        Assert.StartsWith("[Error]", folder);
        Assert.Contains("recursive=true", folder);

        // LIVE-8: the project root delete used to remove the whole PLC project.
        var list = GetText(await _client!.CallToolAsync("twincat_plc_list"));
        Assert.Contains(FixtureLayout.MainPath, list);
    }

    [Trait("Description", "plc_create POU mit implLanguage=FDB → [Error], kein stilles ST, nichts angelegt.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task PlcCreatePou_UnknownImplLanguage_IsError_NothingCreated()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"FB_Fdb148_{Guid.NewGuid():N}"[..20];
        var text = await CallAsync("twincat_plc_create", new Dictionary<string, object?>
        {
            ["kind"] = "POU", ["subtype"] = "FUNCTION_BLOCK",
            ["parentPath"] = DefaultParentPath, ["name"] = name, ["implLanguage"] = "FDB",
        });
        Assert.StartsWith("[Error] Unknown implLanguage 'FDB'", text);
        Assert.DoesNotContain(name, GetText(await _client!.CallToolAsync("twincat_plc_list")));
    }

    // ── Scenario 5: plc_create POU under the POUs container ───────────────────

    [Trait("Description", "Neues PROGRAM per plc_create anlegen → taucht in plc_list auf und ist per plc_read lesbar.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task PlcCreatePou_NewProgram_ShowsUpInListAndIsReadable()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"PRG_PlcCodeSpecTest_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string expectedPath = $"{DefaultParentPath}^{name}";

        try
        {
            var createText = await OkAsync("twincat_plc_create", new Dictionary<string, object?>
            {
                ["kind"] = "POU", ["subtype"] = "PROGRAM", ["parentPath"] = DefaultParentPath, ["name"] = name,
            });
            Assert.Contains("Created PROGRAM", createText);
            Assert.Contains(name, createText);

            Assert.Matches($@"\bPOU/PROGRAM\s+{Regex.Escape(expectedPath)}", await ListAsync("POU"));

            var readText = await ReadAsync(expectedPath, "POU");
            Assert.StartsWith($"Read POU/PROGRAM '{expectedPath}'", readText);
            Assert.Contains("PROGRAM ", readText);
            Assert.Contains(name, readText);
        }
        finally { await TryDeleteAsync(expectedPath, "POU"); }
    }

    // ── Scenario 6: plc_create DUT (STRUCT) round-trips its declaration ───────
    // The live regression gate for issue #104: creating DUTs/GVLs passed 798
    // unit tests against the fake session while every live CreateChild call failed
    // (and the failure was masked as a transport -32603). These scenarios create
    // the object against real XAE, confirm it lists with the right kind, round-trip
    // its declaration via plc_read, and clean up.

    [Trait("Description", "STRUCT-DUT mit Deklaration per plc_create anlegen → erscheint als DUT/STRUCT in plc_list, Deklaration round-trippt über plc_read.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task PlcCreateDut_Struct_ShowsAsDutAndDeclarationRoundTrips()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"ST_PlcCodeSpec_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string path = $"{DutParentPath}^{name}";
        string decl = $"TYPE {name} :\nSTRUCT\n\tField_RoundTrip : INT;\nEND_STRUCT\nEND_TYPE";

        try
        {
            var createText = await OkAsync("twincat_plc_create", new Dictionary<string, object?>
            {
                ["kind"] = "DUT", ["subtype"] = "STRUCT", ["parentPath"] = DutParentPath, ["name"] = name,
                ["declaration"] = decl,
            });
            Assert.Contains("Created DUT STRUCT", createText);

            Assert.Matches($@"\bDUT/STRUCT\s+{Regex.Escape(path)}", await ListAsync("DUT"));

            var read = await ReadAsync(path, "DUT");
            Assert.Contains("Field_RoundTrip", read);
            Assert.DoesNotContain("--- Implementation ---", read);
        }
        finally { await TryDeleteAsync(path, "DUT"); }
    }

    // ── Scenario 7: plc_create DUT (ENUM) shows up as a DUT ───────────────────

    [Trait("Description", "ENUM-DUT per plc_create anlegen (ohne Deklaration) → erscheint als DUT/ENUM in plc_list.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task PlcCreateDut_Enum_ShowsAsDut()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"E_PlcCodeSpec_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string path = $"{DutParentPath}^{name}";

        try
        {
            var createText = await OkAsync("twincat_plc_create", new Dictionary<string, object?>
            {
                ["kind"] = "DUT", ["subtype"] = "ENUM", ["parentPath"] = DutParentPath, ["name"] = name,
            });
            Assert.Contains("Created DUT ENUM", createText);

            Assert.Matches($@"\bDUT/ENUM\s+{Regex.Escape(path)}", await ListAsync("DUT"));
        }
        finally { await TryDeleteAsync(path, "DUT"); }
    }

    // ── Scenario 7b: plc_create DUT (UNION) shows up as a DUT ─────────────────

    [Trait("Description", "UNION-DUT mit Deklaration per plc_create anlegen → erscheint als DUT/UNION, Deklaration round-trippt.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task PlcCreateDut_Union_ShowsAsDutAndRoundTrips()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"U_PlcCodeSpec_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string path = $"{DutParentPath}^{name}";
        string decl = $"TYPE {name} :\nUNION\n\tAsInt : DINT;\n\tAsReal : REAL;\nEND_UNION\nEND_TYPE";

        try
        {
            var createText = await OkAsync("twincat_plc_create", new Dictionary<string, object?>
            {
                ["kind"] = "DUT", ["subtype"] = "UNION", ["parentPath"] = DutParentPath, ["name"] = name,
                ["declaration"] = decl,
            });
            Assert.Contains("Created DUT UNION", createText);

            Assert.Matches($@"\bDUT/UNION\s+{Regex.Escape(path)}", await ListAsync("DUT"));

            Assert.Contains("AsReal", await ReadAsync(path, "DUT"));
        }
        finally { await TryDeleteAsync(path, "DUT"); }
    }

    // ── Scenario 7c: plc_create DUT (ALIAS) — the issue #106 live gate ─────────
    // ALIAS uniquely needs the base type passed to CreateChild as vInfo; a null
    // vInfo throws "Base class not specified". The coordinator parses the base type
    // out of the declaration. This scenario is the regression that #105's suite
    // (STRUCT + ENUM only) lacked.

    [Trait("Description", "ALIAS-DUT mit Basistyp per plc_create anlegen → erscheint als DUT/ALIAS, Deklaration round-trippt (issue #106).")]
    [SkippableFact(Timeout = 300_000)]
    public async Task PlcCreateDut_Alias_ShowsAsDutAndRoundTrips()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"A_PlcCodeSpec_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string path = $"{DutParentPath}^{name}";
        string decl = $"TYPE {name} : UDINT; END_TYPE";

        try
        {
            var createText = await OkAsync("twincat_plc_create", new Dictionary<string, object?>
            {
                ["kind"] = "DUT", ["subtype"] = "ALIAS", ["parentPath"] = DutParentPath, ["name"] = name,
                ["declaration"] = decl,
            });
            Assert.Contains("Created DUT ALIAS", createText);

            Assert.Matches($@"\bDUT/ALIAS\s+{Regex.Escape(path)}", await ListAsync("DUT"));

            Assert.Contains("UDINT", await ReadAsync(path, "DUT"));
        }
        finally { await TryDeleteAsync(path, "DUT"); }
    }

    // ── Scenario 7d: plc_create DUT (ALIAS) without declaration → clean [Error] ─

    [Trait("Description", "ALIAS-DUT per plc_create ohne Deklaration → sauberer [Error], kein halb angelegtes Objekt (issue #106).")]
    [SkippableFact(Timeout = 240_000)]
    public async Task PlcCreateDut_AliasWithoutDeclaration_ReturnsCleanError()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        const string name = "A_NoDeclProbe";
        var text = await CallAsync("twincat_plc_create", new Dictionary<string, object?>
        {
            ["kind"] = "DUT", ["subtype"] = "ALIAS", ["parentPath"] = DutParentPath, ["name"] = name,
        });
        Assert.StartsWith("[Error]", text);
        Assert.Contains("ALIAS", text);
        Assert.DoesNotContain($"{DutParentPath}^{name}", await ListAsync("DUT"));
    }

    // ── Scenario 8: plc_create GVL round-trips its declaration ────────────────

    [Trait("Description", "GVL mit Deklaration per plc_create anlegen → erscheint als GVL/NORMAL in plc_list, Deklaration round-trippt über plc_read.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task PlcCreateGvl_WithDeclaration_ShowsAsGvlAndRoundTrips()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"GVL_PlcCodeSpec_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string path = $"{GvlParentPath}^{name}";
        string decl = "VAR_GLOBAL\n\tg_RoundTrip : BOOL;\nEND_VAR";

        try
        {
            var createText = await OkAsync("twincat_plc_create", new Dictionary<string, object?>
            {
                ["kind"] = "GVL", ["parentPath"] = GvlParentPath, ["name"] = name, ["declaration"] = decl,
            });
            Assert.Contains($"Created GVL '{name}'", createText);

            Assert.Matches($@"\bGVL/NORMAL\s+{Regex.Escape(path)}", await ListAsync("GVL"));

            Assert.Contains("g_RoundTrip", await ReadAsync(path, "GVL"));
        }
        finally { await TryDeleteAsync(path, "GVL"); }
    }

    // ── Scenario 9: a persistent GVL (NORMAL + PERSISTENT section) ────────────

    [Trait("Description", "GVL mit 'VAR_GLOBAL PERSISTENT'-Abschnitt per plc_create anlegen → erscheint als GVL/NORMAL, Deklaration enthält PERSISTENT.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task PlcCreateGvl_PersistentSection_ShowsAsGvlWithPersistentKeyword()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"GVL_Pers_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string path = $"{GvlParentPath}^{name}";
        string decl = "VAR_GLOBAL PERSISTENT\n\tg_PersRoundTrip : INT;\nEND_VAR";

        try
        {
            var createText = await OkAsync("twincat_plc_create", new Dictionary<string, object?>
            {
                ["kind"] = "GVL", ["subtype"] = "NORMAL", ["parentPath"] = GvlParentPath, ["name"] = name,
                ["declaration"] = decl,
            });
            Assert.Contains($"Created GVL '{name}'", createText);

            Assert.Matches($@"\bGVL/NORMAL\s+{Regex.Escape(path)}", await ListAsync("GVL"));

            var read = await ReadAsync(path, "GVL");
            Assert.Contains("PERSISTENT", read);
            Assert.Contains("g_PersRoundTrip", read);
        }
        finally { await TryDeleteAsync(path, "GVL"); }
    }

    // ── Scenario 9a: an empty persistent section → ordinary GVL, no parameter list ─
    // Regression for fix/gvl-persistent-629: nSubType 629 is a Parameter List — live 2026-10-06 its
    // default skeleton is `VAR_GLOBAL CONSTANT` (saved file ParameterList="True"). A persistent GVL is
    // a NORMAL GVL with a PERSISTENT section; this test checks the kind XAE reports and the text it
    // holds, so a Parameter List fails it.

    [Trait("Description", "GVL/NORMAL mit leerem 'VAR_GLOBAL PERSISTENT'-Abschnitt → normale GVL (nicht GVL/PARAMLIST), kein 'CONSTANT' in der Deklaration.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task PlcCreateGvl_EmptyPersistentSection_IsOrdinaryGvlNotParameterList()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"GVL_PersEmpty_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string path = $"{GvlParentPath}^{name}";

        try
        {
            var createText = await OkAsync("twincat_plc_create", new Dictionary<string, object?>
            {
                ["kind"] = "GVL", ["subtype"] = "NORMAL", ["parentPath"] = GvlParentPath, ["name"] = name,
                ["declaration"] = "VAR_GLOBAL PERSISTENT\nEND_VAR",
            });
            Assert.Contains($"Created GVL '{name}'", createText);

            string list = await ListAsync("GVL");
            Assert.Matches($@"\bGVL/NORMAL\s+{Regex.Escape(path)}", list);
            Assert.DoesNotMatch($@"\bGVL/PARAMLIST\s+{Regex.Escape(path)}", list);

            var read = await ReadAsync(path, "GVL");
            Assert.StartsWith($"Read GVL/NORMAL '{path}'", read);
            Assert.Contains("PERSISTENT", read);
            Assert.DoesNotContain("CONSTANT", read);
        }
        finally { await TryDeleteAsync(path, "GVL"); }
    }

    // ── Scenario 9b: plc_create INTERFACE — the live nSubType=618 (PLCITF) gate ─
    // The one empirically-unconfirmed value in creating an interface is CreateChild's
    // nSubType (618). This is its live gate: if 618 is wrong, create surfaces a
    // clean [Error] naming the rejected code; if right, the node reads back as an
    // INTERFACE. Methods are added separately as MEMBERs (out of scope here).

    [Trait("Description", "Neues INTERFACE per plc_create anlegen → taucht in plc_list auf und ist per plc_read als INTERFACE lesbar (nSubType=618-Gate).")]
    [SkippableFact(Timeout = 240_000)]
    public async Task PlcCreateInterface_New_ShowsUpInListAndIsReadable()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"I_PlcCodeSpecTest_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string expectedPath = $"{DefaultParentPath}^{name}";

        try
        {
            var createText = await OkAsync("twincat_plc_create", new Dictionary<string, object?>
            {
                ["kind"] = "INTERFACE", ["parentPath"] = DefaultParentPath, ["name"] = name,
            });
            Assert.Contains("Created INTERFACE", createText);
            Assert.Contains(name, createText);

            Assert.Matches($@"\bINTERFACE\s+{Regex.Escape(expectedPath)}", await ListAsync("INTERFACE"));

            var readText = await ReadAsync(expectedPath, "INTERFACE");
            Assert.StartsWith($"Read INTERFACE '{expectedPath}'", readText);
            Assert.Contains(name, readText);
            Assert.DoesNotContain("--- Implementation ---", readText);
        }
        finally { await TryDeleteAsync(expectedPath, "INTERFACE"); }
    }

    // ── Scenario 10: bad-container failure surfaces as [Error], not -32603 ─────
    // The other half of #104 (Bug B): a CreateChild/placement failure must reach
    // the caller as a normal "[Error] …" string, never as a transport-level error.

    [Trait("Description", "plc_create DUT unter einer POU (ungültiger Container) → sauberer [Error]-String, kein Transport-Fehler.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task PlcCreateDut_UnderPou_ReturnsCleanErrorNotTransportFault()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var result = await _client!.CallToolAsync("twincat_plc_create",
            new Dictionary<string, object?>
            {
                ["kind"] = "DUT", ["subtype"] = "STRUCT",
                ["parentPath"] = FixtureLayout.MainPath, // MAIN — a POU leaf, not a container
                ["name"] = "ST_BadContainerProbe",
            });
        var text = GetText(result);

        // The MCP boundary must return a content payload (the "[Error] …" string),
        // not a transport fault. A masked -32603 would surface as IsError with no
        // "[Error] " text — exactly the failure mode #104 describes.
        Assert.False(string.IsNullOrWhiteSpace(text), "expected a non-empty [Error] message, got an empty/transport fault");
        Assert.StartsWith("[Error]", text);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // A protocol-level failure ("An error occurred invoking ...") does not start with "[Error]"; it is
    // mapped to one here so no scenario can mistake it for a tool answer.
    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client!.CallToolAsync(tool, args);
        string text = GetText(result);
        return result.IsError == true && !text.StartsWith("[Error]", StringComparison.Ordinal) ? "[Error] (protocol) " + text : text;
    }

    private async Task<string> OkAsync(string tool, Dictionary<string, object?> args)
    {
        string text = await CallAsync(tool, args);
        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), $"{tool} failed: {text}");
        return text;
    }

    private Task<string> ReadAsync(string path, string kind) =>
        OkAsync("twincat_plc_read", new Dictionary<string, object?> { ["path"] = path, ["kind"] = kind });

    private Task<string> ListAsync(string kind) =>
        OkAsync("twincat_plc_list", new Dictionary<string, object?> { ["kind"] = kind });

    private async Task TryDeleteAsync(string path, string kind)
    {
        try
        {
            await _client!.CallToolAsync("twincat_plc_delete",
                new Dictionary<string, object?> { ["path"] = path, ["kind"] = kind });
        }
        catch { /* best-effort teardown */ }
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    // twincat_plc_read formats its output as:
    //   Read POU/PROGRAM '...' (ST). Revision: 0123456789abcdef
    //   --- Declaration ---
    //   {declaration}
    //   --- Implementation ---
    //   {implementation}
    // Return everything after the implementation marker, untrimmed.
    private static string ExtractImplementation(string mcpText)
    {
        const string marker = "--- Implementation ---";
        int i = mcpText.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        int start = i + marker.Length;
        if (start < mcpText.Length && mcpText[start] == '\n') start++;
        else if (start + 1 < mcpText.Length && mcpText[start] == '\r' && mcpText[start + 1] == '\n') start += 2;
        return mcpText.Substring(start);
    }

    private static string Normalise(string s) => s.Replace("\r\n", "\n").TrimEnd();
}
