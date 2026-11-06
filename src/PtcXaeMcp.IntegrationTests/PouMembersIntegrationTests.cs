using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// End-to-end coverage for POU members (METHOD / ACTION / PROPERTY / Get / Set), object rename/delete and
/// PLCopen interchange against the live MCP server + XAE, through the twincat_plc_* verbs (members are
/// kind=MEMBER). Serialized assembly-wide (AssemblyInfo: DisableTestParallelization) against the single
/// live XAE per PR #17.
///
/// <para><b>Safety rule (learned the hard way 2026-05-15).</b> These scenarios
/// must NEVER read, write, or "restore" the fixture's real <c>MAIN</c> (or any
/// pre-existing shared POU), and must never save the user's project. An
/// earlier helper that round-tripped MAIN's declaration through formatted MCP
/// text corrupted a real production project. Instead each scenario creates its
/// OWN throwaway <c>FB_PouSpec_{timestamp}</c> FB, wires it into the build-reachable
/// scratch POU (<see cref="ScratchPou"/>) to exercise its members, then deletes it
/// in <c>finally</c>. The fixture's MAIN is never touched beyond the scratch hook.</para>
///
/// <para><b>Compilation-reachability.</b> TwinCAT drops an FB/member that
/// nothing references. The scratch POU references the FB, calls its
/// METHOD / ACTION and reads its PROPERTY, so a clean <c>twincat_build_plc</c>
/// proves the members were actually compiled.</para>
///
/// <para><b>METHOD (issue #19).</b> <c>twincat_plc_create kind=MEMBER subtype=METHOD</c>
/// works on a freshly created FB. The earlier "Name mismatch"/C0243 was caused by
/// reserved-word method names (CALC, ADD — IL operators); the name must be a valid,
/// non-reserved IEC 61131-3 identifier. Scenario 1 below adds a real METHOD ("Compute"),
/// gives it a body, calls it from the scratch POU and asserts a green build.</para>
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class PouMembersIntegrationTests : IAsyncLifetime, IClassFixture<PouSpecCleanupFixture>
{
    private const string ServerUrl = "http://localhost:5010/";
    private static string DefaultParentPath => FixtureLayout.PousFolder;

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

    // ── Scenario 0: members inside a folder under a FB are listed ─────────────
    // fix/pou-members-in-folders: the member walk descended only into PROPERTY, so a FB that
    // groups its members in a folder (TC >= 4024.14) listed "0 PROPERTY". The fixture FB
    // FB_FolderMembers keeps PROPERTY Enabled (Get/Set) and METHOD Reset in folder "Members".
    // Read-only: nothing is created, written or deleted.

    [Trait("Description", "FB mit Members in einem Unterordner → plc_list(parentPath=FB) zeigt den Ordner, PROPERTY inkl. Get/Set und METHOD aus dem Ordner (nur lesend).")]
    [SkippableFact(Timeout = 240_000)]
    public async Task ListMembers_MembersInsideFolder_AreListed()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string pou = FixtureLayout.FolderMembersPouPath;
        string folder = $"{pou}^{FixtureLayout.FolderMembersFolder}";
        var list = await ListMembersAsync(pou);

        Assert.DoesNotContain("[Error]", list);
        // One line per element: "  KIND/SUBTYPE  <path>" (members carry no language suffix).
        Assert.Matches($@"(?m)\bFOLDER\s+{Regex.Escape(folder)}\s*$", list);
        Assert.Matches($@"(?m)\bMEMBER/PROPERTY\s+{Regex.Escape(folder + "^Enabled")}\s*$", list);
        Assert.Matches($@"(?m)\bMEMBER/GET\s+{Regex.Escape(folder + "^Enabled^Get")}\s*$", list);
        Assert.Matches($@"(?m)\bMEMBER/SET\s+{Regex.Escape(folder + "^Enabled^Set")}\s*$", list);
        Assert.Matches($@"(?m)\bMEMBER/METHOD\s+{Regex.Escape(folder + "^Reset")}\s*$", list);
        Assert.Equal(1, CountLines(list, "MEMBER/PROPERTY"));
        Assert.Equal(1, CountLines(list, "MEMBER/METHOD"));
    }

    // ── Scenario 1: FB + ACTION + PROPERTY + METHOD, wired into the scratch POU ─

    [Trait("Description", "FB mit ACTION, PROPERTY und METHOD per plc_create (kind=MEMBER) anlegen, METHOD per plc_write füllen, in Scratch-POU verdrahten → Build grün.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task AddMembers_WiredIntoOwnProgram_BuildsClean()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string stamp = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string fb = $"FB_PouSpec_{stamp}";
        string fbPath = $"{DefaultParentPath}^{fb}";

        await ScratchPou.EnsureAsync(_client!);
        try
        {
            await CreateOk(new()
            {
                ["kind"] = "POU", ["subtype"] = "FUNCTION_BLOCK", ["parentPath"] = DefaultParentPath, ["name"] = fb,
            }, "Created FUNCTION_BLOCK");

            await CreateOk(new()
            {
                ["kind"] = "MEMBER", ["subtype"] = "ACTION", ["parentPath"] = fbPath, ["name"] = "Reset",
            }, "Added ACTION");

            await CreateOk(new()
            {
                ["kind"] = "MEMBER", ["subtype"] = "PROPERTY", ["parentPath"] = fbPath,
                ["name"] = "Level", ["returnType"] = "INT",
            }, "Added PROPERTY");

            // METHOD add (issue #19): works on a freshly created FB with a
            // valid, non-reserved IEC 61131-3 name. "Compute" is fine; CALC/ADD
            // (IL operators) would be rejected by TC3 with "Name mismatch".
            await CreateOk(new()
            {
                ["kind"] = "MEMBER", ["subtype"] = "METHOD", ["parentPath"] = fbPath,
                ["name"] = "Compute", ["returnType"] = "INT",
            }, "Added METHOD");

            // Give the METHOD a real signature + body so the build exercises it
            // (also proves plc_write round-trips a METHOD).
            await WriteOk($"{fbPath}^Compute", "MEMBER",
                "METHOD Compute : INT\r\nVAR_INPUT\r\n\ta : INT;\r\n\tb : INT;\r\nEND_VAR",
                "Compute := a + b;");

            // list surfaces the METHOD, the ACTION, the PROPERTY + Get/Set.
            var list = await ListMembersAsync(fbPath);
            Assert.Matches($@"\bMEMBER/METHOD\s+{Regex.Escape(fbPath + "^Compute")}", list);
            Assert.Matches($@"\bMEMBER/ACTION\s+{Regex.Escape(fbPath + "^Reset")}", list);
            Assert.Matches($@"\bMEMBER/PROPERTY\s+{Regex.Escape(fbPath + "^Level")}", list);
            Assert.Matches($@"\bMEMBER/GET\s+{Regex.Escape(fbPath + "^Level^Get")}", list);
            Assert.Matches($@"\bMEMBER/SET\s+{Regex.Escape(fbPath + "^Level^Set")}", list);

            // The METHOD declaration must carry the requested INT return type —
            // the scalar-vInfo path would otherwise leave "... : 1".
            var rm = await ReadMemberOk($"{fbPath}^Compute", "METHOD");
            Assert.Contains("METHOD Compute : INT", rm);
            Assert.Contains("Compute := a + b;", rm);
            await ReadMemberOk($"{fbPath}^Reset", "ACTION");
            await ReadMemberOk($"{fbPath}^Level", "PROPERTY");

            // Wire the FB into the build-reachable scratch POU so its members
            // are actually compiled (an unreferenced FB is dropped by the
            // build). MAIN itself is never touched beyond the scratch hook.
            await ScratchPou.WriteAsync(_client!,
                $"PROGRAM {ScratchPou.Name}\r\nVAR\r\n\tfb{fb} : {fb};\r\n\tiLevel : INT;\r\n\tiSum : INT;\r\nEND_VAR",
                $"iSum := fb{fb}.Compute(a := 2, b := 3);\r\nfb{fb}.Reset();\r\niLevel := fb{fb}.Level;");

            var build = GetText(await _client!.CallToolAsync("twincat_build_plc",
                new Dictionary<string, object?>()));
            Assert.Contains("Build succeeded", build, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await ScratchPou.ResetAsync(_client!);
            await DeleteQuiet(fbPath, "POU");
        }
    }

    // ── Scenario 1b: interface METHOD live gate (nSubType 610) + build green ───
    // THE primary live gate for interface members. Creating a METHOD member on
    // an INTERFACE must use subtype 610 (vInfo[0]=return type), NOT the POU method
    // 609 — the latter fails with CreateChild "Kann nicht einfuegen". If 610 is
    // wrong, the create surfaces a clean [Error] naming the rejected code. If right,
    // the method declaration round-trips and an FB that IMPLEMENTS the interface
    // (providing a matching method) compiles green — proving the interface method
    // is a real, implementable member.

    [Trait("Description", "INTERFACE mit METHOD (Subtyp 610) per plc_create anlegen, Deklaration per plc_write/plc_read round-trippen, FB IMPLEMENTS → Build grün.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task AddInterfaceMethod_ImplementedByFb_BuildsClean()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string stamp = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string itf = $"I_PouSpec_{stamp}";
        string itfPath = $"{DefaultParentPath}^{itf}";
        string fb = $"FB_PouSpecImpl_{stamp}";
        string fbPath = $"{DefaultParentPath}^{fb}";

        // The interface method and the FB's implementing method must share an
        // identical signature, so both are written from the same declaration body
        // (only the METHOD keyword line is shared shape; interfaces are decl-only).
        string methodVars = "VAR_INPUT\r\n\tPlace : INT;\r\n\tToWorking : BOOL;\r\n\tToHome : BOOL;\r\nEND_VAR";

        await ScratchPou.EnsureAsync(_client!);
        try
        {
            await CreateOk(new()
            {
                ["kind"] = "INTERFACE", ["parentPath"] = DefaultParentPath, ["name"] = itf,
            }, "Created INTERFACE");

            // GATE: METHOD on an interface — subtype 610. A wrong subtype fails here.
            await CreateOk(new()
            {
                ["kind"] = "MEMBER", ["subtype"] = "METHOD", ["parentPath"] = itfPath,
                ["name"] = "SetCoil", ["returnType"] = "BOOL",
            }, "Added METHOD");

            // Declaration round-trips on the interface method (declaration-only).
            await WriteOk($"{itfPath}^SetCoil", "MEMBER", $"METHOD SetCoil : BOOL\r\n{methodVars}", implementation: null);
            var rmItf = await ReadMemberOk($"{itfPath}^SetCoil", "METHOD");
            Assert.Contains("METHOD SetCoil : BOOL", rmItf);
            Assert.Contains("Place", rmItf);

            // list surfaces the interface method; the plain listing shows the interface itself.
            var members = await ListMembersAsync(itfPath);
            Assert.Matches($@"\bMEMBER/METHOD\s+{Regex.Escape(itfPath + "^SetCoil")}", members);
            Assert.Matches($@"\bINTERFACE\s+{Regex.Escape(itfPath)}",
                GetText(await _client!.CallToolAsync("twincat_plc_list",
                    new Dictionary<string, object?> { ["kind"] = "INTERFACE" })));

            // FB IMPLEMENTS the interface and provides a matching SetCoil method.
            await CreateOk(new()
            {
                ["kind"] = "POU", ["subtype"] = "FUNCTION_BLOCK", ["parentPath"] = DefaultParentPath, ["name"] = fb,
            }, "Created FUNCTION_BLOCK");
            await WriteOk(fbPath, "POU", $"FUNCTION_BLOCK {fb} IMPLEMENTS {itf}", "");
            await CreateOk(new()
            {
                ["kind"] = "MEMBER", ["subtype"] = "METHOD", ["parentPath"] = fbPath,
                ["name"] = "SetCoil", ["returnType"] = "BOOL",
            }, "Added METHOD");
            await WriteOk($"{fbPath}^SetCoil", "MEMBER",
                $"METHOD SetCoil : BOOL\r\n{methodVars}",
                "SetCoil := ToWorking AND NOT ToHome;");

            // Wire the FB (typed as the interface) into the build-reachable scratch POU.
            await ScratchPou.WriteAsync(_client!,
                $"PROGRAM {ScratchPou.Name}\r\nVAR\r\n\tfb{stamp} : {fb};\r\n\tip{stamp} : {itf};\r\n\tb : BOOL;\r\nEND_VAR",
                $"ip{stamp} := fb{stamp};\r\nb := ip{stamp}.SetCoil(Place := 1, ToWorking := TRUE, ToHome := FALSE);");

            var build = GetText(await _client!.CallToolAsync("twincat_build_plc",
                new Dictionary<string, object?>()));
            Assert.Contains("Build succeeded", build, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await ScratchPou.ResetAsync(_client!);
            await DeleteQuiet(fbPath, "POU");
            await DeleteQuiet(itfPath, "INTERFACE");
        }
    }

    // ── Scenario 1c: interface PROPERTY live gate (612 / 654 / 655) ────────────
    // Lower-priority companion to 1b: confirms CreateChild accepts the interface
    // PROPERTY subtype 612 and the accessor subtypes 654/655 (no build — the
    // method path is the must-have; this just proves the property subtypes land).

    [Trait("Description", "INTERFACE mit PROPERTY (Subtyp 612 + GET 654/SET 655) per plc_create anlegen → erscheint in plc_list(parentPath); ACTION auf INTERFACE → [Error].")]
    [SkippableFact(Timeout = 240_000)]
    public async Task AddInterfaceProperty_SurfacesGetAndSet()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string stamp = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string itf = $"I_PouSpecProp_{stamp}";
        string itfPath = $"{DefaultParentPath}^{itf}";

        try
        {
            await CreateOk(new()
            {
                ["kind"] = "INTERFACE", ["parentPath"] = DefaultParentPath, ["name"] = itf,
            }, "Created INTERFACE");

            // GATE: PROPERTY on an interface — subtype 612 with accessors 654/655.
            await CreateOk(new()
            {
                ["kind"] = "MEMBER", ["subtype"] = "PROPERTY", ["parentPath"] = itfPath,
                ["name"] = "Enabled", ["returnType"] = "BOOL",
            }, "Added PROPERTY");

            var members = await ListMembersAsync(itfPath);
            Assert.Matches($@"\bMEMBER/PROPERTY\s+{Regex.Escape(itfPath + "^Enabled")}", members);
            Assert.Matches($@"\bMEMBER/GET\s+{Regex.Escape(itfPath + "^Enabled^Get")}", members);
            Assert.Matches($@"\bMEMBER/SET\s+{Regex.Escape(itfPath + "^Enabled^Set")}", members);

            // ACTION must be rejected on an interface with a clear, non-transport error.
            var badAction = await _client!.CallToolAsync("twincat_plc_create",
                new Dictionary<string, object?>
                {
                    ["kind"] = "MEMBER", ["subtype"] = "ACTION", ["parentPath"] = itfPath, ["name"] = "DoWork",
                });
            var badText = GetText(badAction);
            Assert.StartsWith("[Error]", badText);
            Assert.Contains("interface", badText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"{itfPath}^DoWork", await ListMembersAsync(itfPath));
        }
        finally
        {
            await DeleteQuiet(itfPath, "INTERFACE");
        }
    }

    // ── Scenario 2: rename action + FB, delete action; bad-then-good build ─────

    [Trait("Description", "ACTION per plc_rename umbenennen + per plc_delete löschen + FB umbenennen → Build schlägt fehl, nach Korrektur wieder grün.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task RenameAndDelete_BadBuildThenFixed()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        // Millisecond precision (matches scenarios 1 & 3) — a coarser second-stamp
        // widened the cross-run collision window that breeds TwinCAT _1 suffixes (#94).
        string stamp = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string fb = $"FB_PouSpec_{stamp}";
        string fbPath = $"{DefaultParentPath}^{fb}";

        await ScratchPou.EnsureAsync(_client!);
        try
        {
            await CreateOk(new()
            {
                ["kind"] = "POU", ["subtype"] = "FUNCTION_BLOCK", ["parentPath"] = DefaultParentPath, ["name"] = fb,
            }, "Created FUNCTION_BLOCK");
            await CreateOk(new()
            {
                ["kind"] = "MEMBER", ["subtype"] = "ACTION", ["parentPath"] = fbPath, ["name"] = "DoWork",
            }, "Added ACTION");
            await CreateOk(new()
            {
                ["kind"] = "MEMBER", ["subtype"] = "ACTION", ["parentPath"] = fbPath, ["name"] = "Clear",
            }, "Added ACTION");

            // Wire into the build-reachable scratch POU (not a dead PROGRAM).
            await ScratchPou.WriteAsync(_client!,
                $"PROGRAM {ScratchPou.Name}\r\nVAR\r\n\tfb{fb} : {fb};\r\nEND_VAR",
                $"fb{fb}.DoWork();\r\nfb{fb}.Clear();");

            var b0 = GetText(await _client!.CallToolAsync("twincat_build_plc",
                new Dictionary<string, object?>()));
            Assert.Contains("Build succeeded", b0, StringComparison.OrdinalIgnoreCase);

            // Rename the first action and the FB; delete the second action.
            // String tools report failures as "[Error] ..." text, not IsError (issue #190: a refused
            // delete went unnoticed, and the later build check failed for the wrong reason).
            var rm = await OkAsync("twincat_plc_rename", new Dictionary<string, object?>
            {
                ["path"] = $"{fbPath}^DoWork", ["kind"] = "MEMBER", ["newName"] = "DoWork2",
            });
            Assert.StartsWith("Renamed ", rm);
            Assert.Contains($"{fbPath}^DoWork2", rm);

            var del = await OkAsync("twincat_plc_delete", new Dictionary<string, object?>
            {
                ["path"] = $"{fbPath}^Clear", ["kind"] = "MEMBER",
            });
            Assert.Contains("Deleted", del);

            var rfb = await OkAsync("twincat_plc_rename", new Dictionary<string, object?>
            {
                ["path"] = fbPath, ["kind"] = "POU", ["newName"] = fb + "_R",
            });
            Assert.StartsWith("Renamed ", rfb);
            Assert.Contains($"{DefaultParentPath}^{fb}_R", rfb);
            fbPath = $"{DefaultParentPath}^{fb}_R";

            // BAD build: must fail because the scratch POU still calls the deleted Clear action (and,
            // since the renames answer TwinCAT's refactoring prompt with the default "Nein", still uses
            // the old names). Issue #190: with a refused delete every build stayed green.
            // The rename can lag the SaveBeforeBuild+compile: the first build
            // after a just-issued rename may still compile the stale pre-rename
            // tree and report success (issue #66 state-bleed/timing family,
            // same as the post-reopen window). Poll the build until it reflects
            // the rename (i.e. fails) before asserting.
            var bad = await BuildUntil(
                b => b.Contains("Build failed", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Build failed", bad, StringComparison.OrdinalIgnoreCase);

            // Fix the scratch POU to the new names and rebuild green.
            await ScratchPou.WriteAsync(_client!,
                $"PROGRAM {ScratchPou.Name}\r\nVAR\r\n\tfb{fb} : {fb}_R;\r\nEND_VAR",
                $"fb{fb}.DoWork2();");
            var good = GetText(await _client.CallToolAsync("twincat_build_plc",
                new Dictionary<string, object?>()));
            Assert.Contains("Build succeeded", good, StringComparison.OrdinalIgnoreCase);

            // Deterministic read: the tree can lag the just-completed
            // rename+rebuild, so the first listing may still show the old
            // member view (issue #73, #66 suite-order family). Poll until it
            // reflects the rename before asserting.
            var members = await ListMembersUntil(fbPath,
                m => m.Contains("DoWork2", StringComparison.Ordinal)
                  && !m.Contains("Clear", StringComparison.Ordinal));
            Assert.Matches($@"\bMEMBER/ACTION\s+{Regex.Escape(fbPath + "^DoWork2")}", members);
            Assert.DoesNotContain("Clear", members);
        }
        finally
        {
            await ScratchPou.ResetAsync(_client!);
            // Cover both the renamed form (fbPath == {fb}_R, the success path) and
            // the original (in case the rename never took, which would otherwise
            // orphan {fb} — issue #94). The suite-end sweep is the final backstop.
            await DeleteQuiet(fbPath, "POU");
            await DeleteQuiet($"{DefaultParentPath}^{fb}", "POU");
        }
    }

    // ── Scenario 3: plcopen export → delete → import → rebuild green ──────────

    [Trait("Description", "FB exportieren (PLCopen XML), per plc_delete löschen, reimportieren → Build wieder grün.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task PlcOpenRoundTrip_ExportDeleteImport_BuildsClean()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string stamp = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        string fb = $"FB_PouSpec_{stamp}";
        string fbPath = $"{DefaultParentPath}^{fb}";
        string xml = Path.Combine(Path.GetTempPath(), fb + ".xml");

        await ScratchPou.EnsureAsync(_client!);
        try
        {
            await CreateOk(new()
            {
                ["kind"] = "POU", ["subtype"] = "FUNCTION_BLOCK", ["parentPath"] = DefaultParentPath, ["name"] = fb,
            }, "Created FUNCTION_BLOCK");
            await CreateOk(new()
            {
                ["kind"] = "MEMBER", ["subtype"] = "ACTION", ["parentPath"] = fbPath, ["name"] = "Tick",
            }, "Added ACTION");

            // Wire into the build-reachable scratch POU.
            await ScratchPou.WriteAsync(_client!,
                $"PROGRAM {ScratchPou.Name}\r\nVAR\r\n\tfb{fb} : {fb};\r\nEND_VAR",
                $"fb{fb}.Tick();");

            var b0 = GetText(await _client!.CallToolAsync("twincat_build_plc",
                new Dictionary<string, object?>()));
            Assert.Contains("Build succeeded", b0, StringComparison.OrdinalIgnoreCase);

            // Issue #56: in the full suite (not in isolation) PlcOpenExport
            // intermittently fails to resolve the freshly-created FB, surfacing
            // as "export file missing". twincat_build_plc does SaveBeforeBuild,
            // but a preceding test can leave the tree in a state where the FB is
            // not yet persisted where PlcOpenExport's resolver looks. Force an
            // explicit Save All immediately before the export so the new FB is
            // on disk regardless of suite order.
            Assert.False(
                GetText(await _client!.CallToolAsync("twincat_save_project",
                    new Dictionary<string, object?>())).StartsWith("[Error]", StringComparison.Ordinal),
                "twincat_save_project failed before PlcOpenExport (issue #56)");

            // Export the FB (selector = "POUs.<fb>", verified form).
            var exp = await _client.CallToolAsync("twincat_plcopen_export",
                new Dictionary<string, object?>
                {
                    ["filePath"] = xml, ["selector"] = $"POUs.{fb}",
                });
            Assert.False(exp.IsError == true, GetText(exp));
            // Surfaces the actual PlcOpenExport error (text-based "[Error]") before checking the file.
            // See issue #56 for suite-order flake investigation.
            Assert.DoesNotContain("[Error]", GetText(exp), StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(xml), $"export file missing: {xml}");

            // Stop referencing the FB, delete it, then re-import from XML.
            // Issue #98: save after delete so TwinCAT commits the removal before
            // the import runs — without this, the node lingers in the in-memory tree
            // and the import creates FB_PouSpec_<stamp>_1 instead of replacing it.
            await ScratchPou.ResetAsync(_client!);
            await DeleteQuiet(fbPath, "POU");
            Assert.False(
                GetText(await _client!.CallToolAsync("twincat_save_project",
                    new Dictionary<string, object?>())).StartsWith("[Error]", StringComparison.Ordinal),
                "twincat_save_project failed after delete, before PlcOpenImport (issue #98)");

            var impSkip = await _client.CallToolAsync("twincat_plcopen_import",
                new Dictionary<string, object?>
                {
                    ["filePath"] = xml, ["conflictMode"] = "Skip",
                });
            Assert.False(impSkip.IsError == true, GetText(impSkip));

            var impReplace = await _client.CallToolAsync("twincat_plcopen_import",
                new Dictionary<string, object?>
                {
                    ["filePath"] = xml, ["conflictMode"] = "Replace",
                });
            Assert.False(impReplace.IsError == true, GetText(impReplace));

            // Re-wire via the scratch POU and assert the round-tripped FB builds.
            await ScratchPou.WriteAsync(_client!,
                $"PROGRAM {ScratchPou.Name}\r\nVAR\r\n\tfb{fb} : {fb};\r\nEND_VAR",
                $"fb{fb}.Tick();");
            var good = GetText(await _client.CallToolAsync("twincat_build_plc",
                new Dictionary<string, object?>()));
            Assert.Contains("Build succeeded", good, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await ScratchPou.ResetAsync(_client!);
            await DeleteQuiet(fbPath, "POU");
            await DeleteQuiet(fbPath + "_1", "POU");   // backstop: issue #98 _1 duplicate
            try { if (File.Exists(xml)) File.Delete(xml); } catch { }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task OpenFixtureAsync()
    {
        var open = await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?>
            {
                ["slnPath"] = _fixtureSln,
                ["closeCurrentFirst"] = true,
            });
        Assert.False(open.IsError == true, GetText(open));

        // The just-(re)opened solution can momentarily report a non-ready PLC
        // tree — seen after an activate_config ran earlier in the same pass (it
        // used to run in Stage XAE; since issue #195 it runs in Stage XAR). A
        // runtime restart temporarily leaves the PLC tree in a
        // locked/transitional state where CreateChild fails. Wait here until
        // twincat_plc_list returns real POUs, matching the same resilience
        // idiom used by PlcCodeIntegrationTests (issue #66 family).
        await PlcProjectReadiness.AwaitAsync(_client!);
    }

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
        Assert.DoesNotContain("[Error]", text, StringComparison.Ordinal);
        return text;
    }

    // twincat_plc_create with the expected phrase of its result ("Created FUNCTION_BLOCK", "Added METHOD", ...).
    private async Task CreateOk(Dictionary<string, object?> args, string expect)
    {
        var t = await OkAsync("twincat_plc_create", args);
        Assert.Contains(expect, t, StringComparison.OrdinalIgnoreCase);
    }

    // Writes to one of OUR throwaway elements only (never MAIN); the result names the element and kind.
    private async Task WriteOk(string path, string kind, string? declaration, string? implementation)
    {
        var args = new Dictionary<string, object?> { ["path"] = path, ["kind"] = kind };
        if (declaration is not null) args["declaration"] = declaration;
        if (implementation is not null) args["implementation"] = implementation;
        var t = await OkAsync("twincat_plc_write", args);
        Assert.StartsWith($"Wrote {kind} '{path}'.", t);
    }

    // A member must be readable without error and come back in the standard envelope with the
    // sections its subtype has: an ACTION only an implementation, a PROPERTY only a declaration,
    // a METHOD both (an interface METHOD only its declaration). Returns the read text.
    private async Task<string> ReadMemberOk(string memberPath, string subtype)
    {
        var t = await OkAsync("twincat_plc_read",
            new Dictionary<string, object?> { ["path"] = memberPath, ["kind"] = "MEMBER" });
        Assert.StartsWith($"Read MEMBER/{subtype} '{memberPath}'", t);
        Assert.Matches(@"Revision: [0-9a-f]{16}", t.Split('\n')[0]);
        switch (subtype)
        {
            case "ACTION":
                Assert.Contains("--- Implementation ---", t);
                Assert.DoesNotContain("--- Declaration ---", t);
                break;
            case "PROPERTY":
                Assert.Contains("--- Declaration ---", t);
                Assert.DoesNotContain("--- Implementation ---", t);
                break;
            default:
                Assert.Contains("--- Declaration ---", t);
                break;
        }
        return t;
    }

    private async Task<string> ListMembersAsync(string pouPath) =>
        await OkAsync("twincat_plc_list", new Dictionary<string, object?> { ["parentPath"] = pouPath });

    private static int CountLines(string listing, string kindToken) =>
        Regex.Matches(listing, $@"^\s*{Regex.Escape(kindToken)}\s", RegexOptions.Multiline).Count;

    // Best-effort teardown delete that actually sticks (issue #94). It (1) persists
    // the delete with a Save All — an unsaved tree edit is lost when the next
    // scenario reopens the fixture with closeCurrentFirst, leaving the node behind;
    // and (2) surfaces a delete that did not take (logged, not thrown, so a flaky
    // teardown can't fail an otherwise-passing test) — the suite-end
    // PouSpecCleanupFixture sweeps any survivor. A "not found" means the node is
    // already gone, which is the desired outcome, so it is treated as success.
    private async Task DeleteQuiet(string path, string kind)
    {
        try
        {
            var t = GetText(await _client!.CallToolAsync("twincat_plc_delete",
                new Dictionary<string, object?> { ["path"] = path, ["kind"] = kind }));

            await _client!.CallToolAsync("twincat_save_project",
                new Dictionary<string, object?>());

            if (t.StartsWith("[Error]", StringComparison.Ordinal) &&
                !t.Contains("not found", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"  ⚠ teardown: delete '{path}' did not take: {t}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠ teardown: delete '{path}' threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Read <c>twincat_plc_list(parentPath)</c> with a bounded retry. The tree can
    /// lag a just-completed rename+rebuild (issue #73, same suite-order family
    /// as #66): the renamed member is not yet reflected in the first listing.
    /// Polls until <paramref name="accept"/> holds or the budget is exhausted,
    /// then returns the last listing so a never-converged read still produces a
    /// meaningful assertion failure message.
    /// </summary>
    private async Task<string> ListMembersUntil(
        string pouPath, Func<string, bool> accept, int maxAttempts = 5)
    {
        string members = string.Empty;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            members = GetText(await _client!.CallToolAsync("twincat_plc_list",
                new Dictionary<string, object?> { ["parentPath"] = pouPath }));
            if (accept(members))
                return members;
            if (attempt < maxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(2));
        }
        return members;
    }

    /// <summary>
    /// Rebuild the PLC with a bounded retry until <paramref name="accept"/>
    /// holds. A build issued immediately after a tree mutation (rename/delete)
    /// can compile the stale pre-mutation project and report the wrong verdict
    /// (issue #66 state-bleed/timing family); polling rides out the
    /// save/propagate window. Returns the last build text so a never-converged
    /// build still yields a meaningful assertion message.
    /// </summary>
    private async Task<string> BuildUntil(
        Func<string, bool> accept, int maxAttempts = 6)
    {
        string build = string.Empty;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            build = GetText(await _client!.CallToolAsync("twincat_build_plc",
                new Dictionary<string, object?>()));
            if (accept(build))
                return build;
            if (attempt < maxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(3));
        }
        return build;
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}
