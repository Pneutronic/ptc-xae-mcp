using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Single source of truth for the neutral live-suite fixture
/// <c>tests/Fixtures/PtcXaeMcpFixture</c> (issue #146): where it lives, how it is
/// copied to %TEMP% per run, and every tree path / ADS symbol the tests rely on.
/// The fixture is (re)created by <c>tests/Fixtures/create_fixture.py</c> — keep the
/// names here in sync with that script.
///
/// <para><b>Never the repo copy.</b> <see cref="PrepareTempCopy"/> copies the
/// fixture to <c>%TEMP%\ptc-xae-mcp-fixture-runs\run-…</c> once per test process and
/// the suite opens only that copy, so the committed sources are never mutated.
/// Previous run folders are deleted at the start of the next run (the XAE keeps the
/// current copy open after the run, so it cannot be deleted at process exit).</para>
///
/// <para><b>Localized PLC root.</b> The nested PLC project node is labelled
/// "&lt;name&gt; Project" with a UI-language suffix ("FixturePlc Projekt" on a German
/// XAE). It is therefore discovered from <c>twincat_list_pous</c> after the fixture
/// is open (<see cref="DiscoverPlcRootAsync"/>), overridable via
/// <see cref="PlcRootEnvVar"/>; every PLC path below derives from it.</para>
/// </summary>
public static class FixtureLayout
{
    /// <summary>Overrides the discovered PLC project root (e.g. <c>TIPC^FixturePlc^FixturePlc Project</c>).</summary>
    public const string PlcRootEnvVar = "PTC_XAE_MCP_FIXTURE_PLC_ROOT";

    public const string SolutionName = "PtcXaeMcpFixture";
    public const string SolutionFile = SolutionName + ".sln";

    /// <summary>Fixture folder relative to the repository root.</summary>
    public static readonly string RepoRelativeDirectory = Path.Combine("tests", "Fixtures", SolutionName);

    public const string PlcName = "FixturePlc";

    // ── PLC tree paths (derived from the discovered root) ──────────────────────

    private static string? _discoveredPlcRoot;

    /// <summary>Caret path of the nested PLC project (parent of POUs/DUTs/GVLs).</summary>
    public static string PlcRoot =>
        NonEmpty(Environment.GetEnvironmentVariable(PlcRootEnvVar))
        ?? _discoveredPlcRoot
        ?? $"TIPC^{PlcName}^{PlcName} Project";

    public static string PousFolder => PlcRoot + "^POUs";
    public static string DutsFolder => PlcRoot + "^DUTs";
    public static string GvlsFolder => PlcRoot + "^GVLs";

    /// <summary>The cyclic PROGRAM (safe to read; never round-tripped by the suite).</summary>
    public static string MainPath => PousFolder + "^MAIN";

    /// <summary>FB with an FB_Base-style step chain (<c>CASE Step OF … 1010: …</c>).</summary>
    public static string StepChainPouPath => PousFolder + "^FB_StepChain";

    /// <summary>A numeric step label present in <see cref="StepChainPouPath"/>.</summary>
    public const int StepChainStep = 1010;

    public static string InterfacePath => PousFolder + "^I_FixtureDevice";

    /// <summary>
    /// FB whose members live in a sub-folder (fix/pou-members-in-folders): folder
    /// <see cref="FolderMembersFolder"/> holds PROPERTY <c>Enabled</c> (Get + Set) and METHOD <c>Reset</c>.
    /// list_pou_members used to skip folders under a POU, so these were missing.
    /// </summary>
    public static string FolderMembersPouPath => PousFolder + "^FB_FolderMembers";
    public const string FolderMembersFolder = "Members";

    // ── DUTs / GVL (created by create_fixture.py) ─────────────────────────────

    /// <summary>ENUM DUT <c>E_FixtureMode</c> (referenced by MAIN — read it, never edit it).</summary>
    public static string EnumDutPath => DutsFolder + "^E_FixtureMode";

    /// <summary>STRUCT DUT <c>ST_FixtureRecord</c> (referenced by MAIN — read it, round-trip it, never edit it).</summary>
    public static string StructDutPath => DutsFolder + "^ST_FixtureRecord";

    /// <summary>ALIAS DUT <c>T_FixtureName</c> (<c>STRING(40)</c>).</summary>
    public static string AliasDutPath => DutsFolder + "^T_FixtureName";

    /// <summary>The main GVL (<see cref="GvlName"/>); every symbol is referenced by MAIN.</summary>
    public static string FixtureGvlPath => GvlsFolder + "^" + GvlName;

    // ── GVL / ADS symbols ─────────────────────────────────────────────────────

    public const string GvlName = "GVL_Fixture";
    public const string BoolSymbol = GvlName + ".bEnable";
    /// <summary>BOOL declared <c>AT %I*</c> — a write via ADS must be refused.</summary>
    public const string InputMappedBoolSymbol = GvlName + ".bInputSignal";
    /// <summary>UINT initialised to <see cref="UintSymbolValue"/>; also the runtime-readiness probe.</summary>
    public const string UintSymbol = GvlName + ".nCount";
    public const uint UintSymbolValue = 2;
    public const string LrealSymbol = GvlName + ".fValue";
    public const string StringSymbol = GvlName + ".sText";
    public const string StringArraySymbol = GvlName + ".aNames";

    // ── IbnLive probe (issue #200) ────────────────────────────────────────────

    /// <summary>
    /// GVL with the test-PRG contract of the <c>twincat_ibn_*</c> tools (IbnLiveCoordinator), passed as their
    /// <c>gvlPath</c>: <c>bEnable</c> (heartbeat), <c>bStepReq</c>, <c>bAbort</c>, and the status
    /// <c>bChainRunning</c>, <c>nActiveChain</c>, <c>nCurrentStep</c>, <c>bFault</c>. The fixture has no test
    /// PRG: MAIN only reads these symbols, so a written bit moves nothing.
    /// </summary>
    public const string IbnProbeGvlName = "GVL_IBN_Probe";

    /// <summary>The abort bit that disarm/abort/release_all set on <see cref="IbnProbeGvlName"/>.</summary>
    public const string IbnProbeAbortSymbol = IbnProbeGvlName + ".bAbort";

    // ── plc-instance-settings ─────────────────────────────────────────────────

    /// <summary>
    /// GVL with two <c>{attribute 'TcInitSymbol'}</c> variables (<see cref="InitSymbolDint"/> := 10,
    /// <see cref="InitSymbolLreal"/> := 1.5) and one <c>VAR_GLOBAL RETAIN</c> variable
    /// (<see cref="RetainSymbol"/>). MAIN only reads them.
    /// </summary>
    public const string InstanceSettingsGvlName = "GVL_InstanceSettings";
    public const string InitSymbolDint = InstanceSettingsGvlName + ".nInitParam";
    public const string InitSymbolDintValue = "10";
    public const string InitSymbolLreal = InstanceSettingsGvlName + ".fInitGain";
    public const string RetainSymbol = InstanceSettingsGvlName + ".nRetainCounter";

    // ── persistent-snapshot ───────────────────────────────────────────────────

    /// <summary>
    /// GVL <c>VAR_GLOBAL PERSISTENT</c> with <c>stRecipe : ST_PersistentRecipe</c> (nested struct, ENUM, STRING,
    /// WSTRING, TIME, DT, <c>aPos : ARRAY[1..5] OF LREAL</c>) and <c>arCal : ARRAY[1..3] OF ST_PersistentInner</c>,
    /// plus the non-persistent FB instances <c>fbCell</c> and <c>aCells[1..2]</c> of <c>FB_PersistentCell</c>
    /// (one <c>VAR PERSISTENT</c> and one <c>VAR RETAIN</c> local). MAIN only reads them.
    /// </summary>
    public const string PersistentGvlName = "GVL_Persistent";

    /// <summary>The three instances of <c>FB_PersistentCell</c>: one plain, two inside an array.</summary>
    public static readonly string[] FbCellInstances =
    [
        $"{PersistentGvlName}.fbCell", $"{PersistentGvlName}.aCells[1]", $"{PersistentGvlName}.aCells[2]",
    ];

    /// <summary>Every leaf the compiler marks PERSISTENT in the fixture (declared array indices).</summary>
    public static readonly string[] PersistentLeaves =
    [
        .. new[] { "stInner.nCount", "stInner.fOffset", "eMode", "sName", "wsName", "tDelay", "dtStamp" }
            .Select(m => $"{PersistentGvlName}.stRecipe.{m}"),
        .. Enumerable.Range(1, 5).Select(i => $"{PersistentGvlName}.stRecipe.aPos[{i}]"),
        .. Enumerable.Range(1, 3).SelectMany(i => new[] { "nCount", "fOffset" }.Select(m => $"{PersistentGvlName}.arCal[{i}].{m}")),
        .. FbCellInstances.Select(i => $"{i}.nPersistentCount"),
    ];

    // ── I/O ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// EtherCAT master with an EK1100 coupler, an EL1008 and an EL3174 — <b>disabled</b>, so
    /// activating the fixture needs no EtherCAT adapter; topology, the offline CoE dictionary
    /// (ESI), links and open_object work on it regardless.
    /// </summary>
    public const string EtherCatMasterPath = "TIID^Device 1 (EtherCAT)";

    // ── locating + copying the fixture ────────────────────────────────────────

    private static readonly object CopyLock = new();
    private static string? _tempCopySln;

    /// <summary>Folders never copied (generated by the XAE; also git-ignored).</summary>
    private static readonly string[] SkippedDirectories = [".vs", "_Boot", "_CompileInfo", "_Libraries"];

    /// <summary>
    /// The committed fixture .sln, found by walking up from the test assembly to the
    /// repository root. Null when the tests run outside a source checkout.
    /// </summary>
    public static string? FindRepoFixtureSln()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, RepoRelativeDirectory, SolutionFile);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Copies the committed fixture to a fresh %TEMP% folder once per test process and
    /// returns the copied .sln (cached). Null when the repo fixture is not found.
    /// </summary>
    public static string? PrepareTempCopy()
    {
        lock (CopyLock)
        {
            if (_tempCopySln is not null) return _tempCopySln;

            string? source = FindRepoFixtureSln();
            if (source is null) return null;

            string baseDir = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-fixture-runs");
            DeleteOldRuns(baseDir);

            string runDir = Path.Combine(baseDir,
                $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
            string target = Path.Combine(runDir, SolutionName);
            CopyTree(Path.GetDirectoryName(source)!, target);

            _tempCopySln = Path.Combine(target, SolutionFile);
            return _tempCopySln;
        }
    }

    /// <summary>
    /// A private, throw-away copy of the committed fixture under the same run base folder
    /// (<c>%TEMP%\ptc-xae-mcp-fixture-runs\run-…-&lt;label&gt;-…</c>, cleaned up with the next run) for a
    /// test that must change the project without touching the shared per-run copy. Returns its .sln,
    /// or null when the repo fixture is not found.
    /// </summary>
    public static string? PrepareScratchCopy(string label)
    {
        string? source = FindRepoFixtureSln();
        if (source is null) return null;
        string runDir = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-fixture-runs",
            $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{label}-{Guid.NewGuid():N}");
        string target = Path.Combine(runDir, SolutionName);
        CopyTree(Path.GetDirectoryName(source)!, target);
        return Path.Combine(target, SolutionFile);
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (string dir in Directory.EnumerateDirectories(source))
        {
            string name = Path.GetFileName(dir);
            if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            CopyTree(dir, Path.Combine(target, name));
        }
    }

    // Best effort: a run folder the XAE still holds is simply left for next time.
    private static void DeleteOldRuns(string baseDir)
    {
        if (!Directory.Exists(baseDir)) return;
        foreach (string dir in Directory.EnumerateDirectories(baseDir, "run-*"))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* locked — keep */ }
        }
    }

    // ── PLC root discovery ────────────────────────────────────────────────────

    private static readonly Regex PouLine = new(@"^\s*POU\s+(?<path>TIPC\^[^\^\r\n]+\^[^\^\r\n]+)\^",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Reads the real (localized) PLC project root from <c>twincat_list_pous</c> of the
    /// open fixture. Keeps the previous value when the listing is not (yet) usable.
    /// </summary>
    public static async Task DiscoverPlcRootAsync(McpClient client)
    {
        for (int attempt = 1; attempt <= 12; attempt++)
        {
            try
            {
                var result = await client.CallToolAsync("twincat_list_pous",
                    cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
                string text = string.Join("\n", (result.Content ?? []).OfType<TextContentBlock>().Select(c => c.Text));
                var match = PouLine.Match(text);
                if (match.Success)
                {
                    _discoveredPlcRoot = match.Groups["path"].Value.TrimEnd();
                    Console.WriteLine($"  ✓ Fixture PLC root: {_discoveredPlcRoot}");
                    return;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException
                                       or ModelContextProtocol.McpException)
            {
                // transient — retry
            }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        Console.WriteLine($"  ⚠ Fixture PLC root not discovered — using {PlcRoot}.");
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
