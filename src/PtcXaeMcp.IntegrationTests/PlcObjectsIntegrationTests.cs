using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// feature/plc-objects, live: the generic twincat_plc_* verbs against the neutral fixture
/// (<see cref="FixtureLayout"/>). Elements the tests create carry a time-stamped name and are deleted in a
/// finally block; the fixture's own elements are only read, or written back unchanged.
/// <para>The code-editing scenarios of <see cref="PlcCodeIntegrationTests"/> and the member scenarios of
/// <see cref="PouMembersIntegrationTests"/> run through the same verbs.</para>
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class PlcObjectsIntegrationTests : IAsyncLifetime
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

    private void SkipIfNotReady() => LiveXae.GuardReady(_ready);

    private async Task OpenFixtureAsync()
    {
        var open = await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.IsError == true, GetText(open));
        await PlcProjectReadiness.AwaitAsync(_client!);
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    // A protocol-level failure ("An error occurred invoking ...", e.g. a missing required argument) does not start with
    // "[Error]"; it is mapped to one here so that no test can mistake it for a tool answer.
    private async Task<string> CallAsync(string tool, params (string Key, object? Value)[] args)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in args) dict[key] = value;
        var result = await _client!.CallToolAsync(tool, dict);
        string text = GetText(result);
        return result.IsError == true && !text.StartsWith("[Error]", StringComparison.Ordinal) ? "[Error] (protocol) " + text : text;
    }

    private async Task<string> OkAsync(string tool, params (string Key, object? Value)[] args)
    {
        string text = await CallAsync(tool, args);
        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), $"{tool} failed: {text}");
        return text;
    }

    private async Task TryDeleteAsync(string path, string kind, bool recursive = false)
    {
        try
        {
            await CallAsync("twincat_plc_delete", ("path", path), ("kind", kind), ("recursive", recursive));
        }
        catch { /* best-effort teardown */ }
    }

    private static string Stamp() => DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);

    private static string Normalise(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    // ── kind gate ─────────────────────────────────────────────────────────────

    [Trait("Description", "kind passt nicht zum Knoten (GVL als DUT gelesen/geschrieben) → '[Error] ... is a GVL, not a DUT', Deklaration danach unverändert.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task WrongKind_IsRefused_AndChangesNothing()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        string gvl = FixtureLayout.FixtureGvlPath;

        string before = await OkAsync("twincat_plc_read", ("path", gvl), ("kind", "GVL"));

        string read = await CallAsync("twincat_plc_read", ("path", gvl), ("kind", "DUT"));
        Assert.StartsWith("[Error]", read);
        Assert.Contains("is a GVL, not a DUT", read);

        string write = await CallAsync("twincat_plc_write", ("path", gvl), ("kind", "DUT"), ("declaration", "VAR_GLOBAL\nEND_VAR"));
        Assert.StartsWith("[Error]", write);
        Assert.Contains("is a GVL, not a DUT", write);

        string del = await CallAsync("twincat_plc_delete", ("path", gvl), ("kind", "POU"));
        Assert.Contains("is a GVL, not a POU", del);

        string after = await OkAsync("twincat_plc_read", ("path", gvl), ("kind", "GVL"));
        Assert.Equal(Normalise(before), Normalise(after));
    }

    // ── list ──────────────────────────────────────────────────────────────────

    [Trait("Description", "plc_list nach kind → POU/FUNCTION_BLOCK, DUT/ENUM|STRUCT|ALIAS, INTERFACE, GVL/NORMAL mit Subtyp; Fehlerfälle liefern [Error] (nur lesend).")]
    [SkippableFact(Timeout = 300_000)]
    public async Task List_ByKind_ShowsSubtypes()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string pous = await OkAsync("twincat_plc_list", ("kind", "POU"));
        Assert.Contains("POU/PROGRAM", pous);
        Assert.Contains("POU/FUNCTION_BLOCK", pous);
        Assert.Contains(FixtureLayout.MainPath, pous);
        Assert.Contains(FixtureLayout.StepChainPouPath, pous);

        string duts = await OkAsync("twincat_plc_list", ("kind", "DUT"));
        Assert.Contains("DUT/ENUM", duts);
        Assert.Contains("DUT/STRUCT", duts);
        Assert.Contains("DUT/ALIAS", duts);

        string itf = await OkAsync("twincat_plc_list", ("kind", "INTERFACE"));
        Assert.Contains(FixtureLayout.InterfacePath, itf);

        string gvls = await OkAsync("twincat_plc_list", ("kind", "GVL"));
        Assert.Contains("GVL/NORMAL", gvls);
        Assert.Contains(FixtureLayout.FixtureGvlPath, gvls);

        string bad = await CallAsync("twincat_plc_list", ("kind", "MEMBER"));
        Assert.StartsWith("[Error]", bad);
    }

    [Trait("Description", "plc_list unter FB_FolderMembers → Ordner 'Members', PROPERTY Enabled mit Get/Set und METHOD Reset aus dem Unterordner (nur lesend).")]
    [SkippableFact(Timeout = 300_000)]
    public async Task List_UnderAPou_ReachesMembersAndFoldersInsideFolders()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string pou = FixtureLayout.FolderMembersPouPath;
        string folder = $"{pou}^{FixtureLayout.FolderMembersFolder}";
        string list = await OkAsync("twincat_plc_list", ("parentPath", pou));

        Assert.Contains($"FOLDER", list);
        Assert.Contains(folder, list);
        Assert.Contains($"MEMBER/PROPERTY", list);
        Assert.Contains($"{folder}^Enabled", list);
        Assert.Contains($"{folder}^Enabled^Get", list);
        Assert.Contains($"{folder}^Enabled^Set", list);
        Assert.Contains($"MEMBER/METHOD", list);
        Assert.Contains($"{folder}^Reset", list);

        string onlyMembers = await OkAsync("twincat_plc_list", ("kind", "MEMBER"), ("parentPath", pou));
        Assert.DoesNotContain("FOLDER", onlyMembers);
        string onlyFolders = await OkAsync("twincat_plc_list", ("kind", "FOLDER"), ("parentPath", pou));
        Assert.Contains(folder, onlyFolders);
        Assert.DoesNotContain("MEMBER/", onlyFolders);
    }

    // ── read / write of existing fixture elements ─────────────────────────────

    [Trait("Description", "Bestehendes Fixture-DUT über plc_read lesen und unverändert über plc_write zurückschreiben → gleicher Text (schließt die Lücke: DUT-Schreiben über den Tool-Einstieg).")]
    [SkippableFact(Timeout = 300_000)]
    public async Task ExistingDut_ReadAndWriteBackUnchanged()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        string dut = FixtureLayout.StructDutPath;

        string read = await OkAsync("twincat_plc_read", ("path", dut), ("kind", "DUT"));
        Assert.Contains("DUT/STRUCT", read);
        Assert.Contains("--- Declaration ---", read);
        Assert.DoesNotContain("--- Implementation ---", read);

        string declaration = DeclarationOf(read);
        string write = await OkAsync("twincat_plc_write", ("path", dut), ("kind", "DUT"), ("declaration", declaration));
        Assert.Contains($"Wrote DUT '{dut}'", write);

        string again = await OkAsync("twincat_plc_read", ("path", dut), ("kind", "DUT"));
        Assert.Equal(Normalise(declaration), Normalise(DeclarationOf(again)));

        // A section the element does not have is refused before anything is written.
        string impl = await CallAsync("twincat_plc_write", ("path", dut), ("kind", "DUT"), ("implementation", "x := 1;"));
        Assert.StartsWith("[Error]", impl);
        Assert.Contains("no implementation section", impl);
    }

    [Trait("Description", "plc_read meldet eine Revision; plc_write mit veralteter Revision wird abgelehnt und ändert nichts, mit der gelesenen Revision schreibt es und meldet die neue.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Write_WithAStaleRevision_IsRefused_WithTheReadRevision_Writes()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        string dut = FixtureLayout.StructDutPath;

        string read = await OkAsync("twincat_plc_read", ("path", dut), ("kind", "DUT"));
        string revision = RevisionOf(read);
        string declaration = DeclarationOf(read);

        string stale = await CallAsync("twincat_plc_write", ("path", dut), ("kind", "DUT"),
            ("declaration", declaration + "\n// never written"), ("expectedRevision", "0000000000000000"));
        Assert.StartsWith($"[Error] '{dut}' changed since it was read", stale);
        Assert.Contains($"now {revision}", stale);
        string unchanged = await OkAsync("twincat_plc_read", ("path", dut), ("kind", "DUT"));
        Assert.Equal(revision, RevisionOf(unchanged));
        Assert.DoesNotContain("never written", unchanged);

        string write = await OkAsync("twincat_plc_write", ("path", dut), ("kind", "DUT"),
            ("declaration", declaration), ("expectedRevision", revision));
        Assert.Matches("Revision: [0-9a-f]{16}", write);
    }

    [Trait("Description", "plc_create mit implementation schreibt den Rumpf gleich mit; Umbenennen einer Methode auf den Namen ihres Eingangs wird vor der Änderung abgelehnt.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Create_WithABody_AndRename_OntoAnInputName_IsRefused()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        string fb = FixtureLayout.PousFolder + "^FB_CreateBodyProbe";
        string method = fb + "^M_Run";

        await OkAsync("twincat_plc_create", ("kind", "POU"), ("subtype", "FUNCTION_BLOCK"),
            ("parentPath", FixtureLayout.PousFolder), ("name", "FB_CreateBodyProbe"));
        try
        {
            string created = await OkAsync("twincat_plc_create", ("kind", "MEMBER"), ("subtype", "METHOD"), ("parentPath", fb),
                ("name", "M_Run"), ("returnType", "BOOL"),
                ("declaration", "METHOD M_Run : BOOL\nVAR_INPUT\n\tbGo : BOOL;\nEND_VAR\n"), ("implementation", "M_Run := bGo;"));
            Assert.Contains($"Wrote MEMBER '{method}'", created);
            Assert.Contains("M_Run := bGo;", await OkAsync("twincat_plc_read", ("path", method), ("kind", "MEMBER")));

            string refused = await CallAsync("twincat_plc_rename", ("path", method), ("kind", "MEMBER"), ("newName", "bGo"));
            Assert.StartsWith($"[Error] '{method}' cannot be renamed to 'bGo': 'bGo' is already a variable", refused);
            await OkAsync("twincat_plc_read", ("path", method), ("kind", "MEMBER"));
        }
        finally
        {
            await TryDeleteAsync(fb, "POU");
        }
    }

    private static string RevisionOf(string read)
    {
        var m = System.Text.RegularExpressions.Regex.Match(read, "Revision: ([0-9a-f]{16})");
        Assert.True(m.Success, $"no revision in: {read}");
        return m.Groups[1].Value;
    }

    [Trait("Description", "plc_read INTERFACE und POU: INTERFACE ohne Implementierungsabschnitt, POU mit beiden Abschnitten und Sprache (nur lesend).")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Read_InterfaceAndPou_ReturnOnlyTheirSections()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string itf = await OkAsync("twincat_plc_read", ("path", FixtureLayout.InterfacePath), ("kind", "INTERFACE"));
        Assert.Contains("--- Declaration ---", itf);
        Assert.DoesNotContain("--- Implementation ---", itf);

        string pou = await OkAsync("twincat_plc_read", ("path", FixtureLayout.StepChainPouPath), ("kind", "POU"));
        Assert.Contains("POU/FUNCTION_BLOCK", pou);
        Assert.Contains("--- Declaration ---", pou);
        Assert.Contains("--- Implementation ---", pou);
        Assert.Contains("CASE Step OF", pou);
    }

    // ── lifecycle per element ─────────────────────────────────────────────────

    [Trait("Description", "STRUCT-DUT: anlegen → list_entries → Feld hinzufügen/ändern/entfernen → lesen → umbenennen → löschen; kind-Fehlzuordnung ändert nichts.")]
    [SkippableFact(Timeout = 480_000)]
    public async Task StructDut_FullLifecycle()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"ST_PlcObj_{Stamp()}";
        string renamed = name + "_R";
        string path = $"{FixtureLayout.DutsFolder}^{name}";
        string renamedPath = $"{FixtureLayout.DutsFolder}^{renamed}";
        const string decl = "TYPE ST_X :\nSTRUCT\n\tnA : INT;\n\tnB : INT := 2; // second\nEND_STRUCT\nEND_TYPE";
        string current = path;

        try
        {
            string create = await OkAsync("twincat_plc_create", ("kind", "DUT"), ("subtype", "STRUCT"),
                ("parentPath", FixtureLayout.DutsFolder), ("name", name), ("declaration", decl.Replace("ST_X", name, StringComparison.Ordinal)));
            Assert.Contains($"Created DUT STRUCT '{name}'", create);

            string entries = await OkAsync("twincat_plc_list_entries", ("path", path), ("kind", "DUT"));
            Assert.Contains("nA : INT", entries);
            Assert.Contains("nB : INT := 2", entries);
            Assert.Contains("// second", entries);

            await OkAsync("twincat_plc_edit_entries", ("path", path), ("kind", "DUT"), ("op", "add"), ("name", "fC"),
                ("type", "REAL"), ("initialValue", "1.5"), ("comment", "added"));
            await OkAsync("twincat_plc_edit_entries", ("path", path), ("kind", "DUT"), ("op", "set"), ("name", "nA"),
                ("type", "DINT"), ("attributes", new[] { "hide" }));
            await OkAsync("twincat_plc_edit_entries", ("path", path), ("kind", "DUT"), ("op", "remove"), ("name", "nB"));

            string read = await OkAsync("twincat_plc_read", ("path", path), ("kind", "DUT"));
            Assert.Contains("fC : REAL := 1.5", read);
            Assert.Contains("nA : DINT", read);
            Assert.Contains("{attribute 'hide'}", read);
            Assert.DoesNotContain("nB", read);

            // Wrong kind on an existing node changes nothing.
            string wrong = await CallAsync("twincat_plc_edit_entries", ("path", path), ("kind", "GVL"), ("op", "remove"), ("name", "fC"));
            Assert.Contains("is a DUT, not a GVL", wrong);
            Assert.Contains("fC : REAL", await OkAsync("twincat_plc_read", ("path", path), ("kind", "DUT")));

            string rename = await OkAsync("twincat_plc_rename", ("path", path), ("kind", "DUT"), ("newName", renamed));
            Assert.Contains(renamedPath, rename);
            current = renamedPath;
            Assert.Contains("fC : REAL", await OkAsync("twincat_plc_read", ("path", renamedPath), ("kind", "DUT")));

            string del = await OkAsync("twincat_plc_delete", ("path", renamedPath), ("kind", "DUT"));
            Assert.Contains("Deleted", del);
            current = string.Empty;
            string gone = await CallAsync("twincat_plc_read", ("path", renamedPath), ("kind", "DUT"));
            Assert.StartsWith("[Error]", gone);
        }
        finally
        {
            if (current.Length > 0) await TryDeleteAsync(current, "DUT");
            await TryDeleteAsync(path, "DUT");
        }
    }

    [Trait("Description", "ENUM-DUT: Werte hinzufügen (Komma-Behandlung), ändern, letzten entfernen; Basistyp und Attribut bleiben unberührt.")]
    [SkippableFact(Timeout = 480_000)]
    public async Task EnumDut_ValuesAddSetRemove()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"E_PlcObj_{Stamp()}";
        string path = $"{FixtureLayout.DutsFolder}^{name}";

        try
        {
            await OkAsync("twincat_plc_create", ("kind", "DUT"), ("subtype", "ENUM"),
                ("parentPath", FixtureLayout.DutsFolder), ("name", name),
                ("declaration", $"{{attribute 'qualified_only'}}\nTYPE {name} :\n(\n\tRed := 0,\n\tGreen := 1\n) UINT;\nEND_TYPE"));

            await OkAsync("twincat_plc_edit_entries", ("path", path), ("kind", "DUT"), ("op", "add"), ("name", "Blue"), ("value", "2"));
            await OkAsync("twincat_plc_edit_entries", ("path", path), ("kind", "DUT"), ("op", "set"), ("name", "Green"), ("value", "10"));

            string list = await OkAsync("twincat_plc_list_entries", ("path", path), ("kind", "DUT"));
            Assert.Contains("Red := 0", list);
            Assert.Contains("Green := 10", list);
            Assert.Contains("Blue := 2", list);

            await OkAsync("twincat_plc_edit_entries", ("path", path), ("kind", "DUT"), ("op", "remove"), ("name", "Blue"));
            string read = await OkAsync("twincat_plc_read", ("path", path), ("kind", "DUT"));
            Assert.Contains("qualified_only", read);
            Assert.Contains("UINT", read);
            Assert.DoesNotContain("Blue", read);
            Assert.Matches(new Regex(@"Green := 10\s*\)"), read);
        }
        finally { await TryDeleteAsync(path, "DUT"); }
    }

    [Trait("Description", "GVL: normale GVL mit 'VAR_GLOBAL PERSISTENT'-Abschnitt über edit_entries (Subtyp NORMAL); PARAMLIST wird als Parameterliste (629) angelegt, nicht als Persistent-Liste.")]
    [SkippableFact(Timeout = 480_000)]
    public async Task Gvl_PersistentSection_AndParameterList()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"GVL_PlcObj_{Stamp()}";
        string path = $"{FixtureLayout.GvlsFolder}^{name}";
        string plName = $"PL_PlcObj_{Stamp()}";
        string plPath = $"{FixtureLayout.GvlsFolder}^{plName}";

        try
        {
            await OkAsync("twincat_plc_create", ("kind", "GVL"), ("parentPath", FixtureLayout.GvlsFolder), ("name", name),
                ("declaration", "VAR_GLOBAL\n\tgA : INT;\nEND_VAR"));
            string add = await OkAsync("twincat_plc_edit_entries", ("path", path), ("kind", "GVL"), ("op", "add"), ("name", "gP"),
                ("section", "VAR_GLOBAL PERSISTENT"), ("type", "DINT"));
            Assert.Contains("new section 'VAR_GLOBAL PERSISTENT'", add);

            string entries = await OkAsync("twincat_plc_list_entries", ("path", path), ("kind", "GVL"));
            Assert.Contains("[VAR_GLOBAL] gA", entries);
            Assert.Contains("[VAR_GLOBAL PERSISTENT] gP", entries);

            string listed = await OkAsync("twincat_plc_list", ("kind", "GVL"));
            Assert.Matches(new Regex($@"GVL/NORMAL\s+{Regex.Escape(path)}"), listed);

            // Parameter list: a different element (ItemType 629), not "persistent".
            await OkAsync("twincat_plc_create", ("kind", "GVL"), ("subtype", "PARAMLIST"),
                ("parentPath", FixtureLayout.GvlsFolder), ("name", plName));
            string listed2 = await OkAsync("twincat_plc_list", ("kind", "GVL"));
            Assert.Matches(new Regex($@"GVL/PARAMLIST\s+{Regex.Escape(plPath)}"), listed2);
            string pl = await OkAsync("twincat_plc_read", ("path", plPath), ("kind", "GVL"));
            Assert.DoesNotContain("PERSISTENT", pl);
        }
        finally
        {
            await TryDeleteAsync(path, "GVL");
            await TryDeleteAsync(plPath, "GVL");
        }
    }

    [Trait("Description", "FUNCTION_BLOCK: anlegen mit Deklaration → Variable mit Attribut in VAR_INPUT, neuer Abschnitt VAR_TEMP → METHOD/PROPERTY anlegen, Implementierung schreiben, umbenennen, löschen.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task FunctionBlock_EntriesAndMembers_FullLifecycle()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"FB_PlcObj_{Stamp()}";
        string path = $"{FixtureLayout.PousFolder}^{name}";

        try
        {
            await OkAsync("twincat_plc_create", ("kind", "POU"), ("subtype", "FUNCTION_BLOCK"),
                ("parentPath", FixtureLayout.PousFolder), ("name", name),
                ("declaration", $"FUNCTION_BLOCK {name}\nVAR_INPUT\n\tbEnable : BOOL;\nEND_VAR\nVAR\n\tnCount : INT;\nEND_VAR"));

            await OkAsync("twincat_plc_edit_entries", ("path", path), ("kind", "POU"), ("op", "add"), ("name", "rSpeed"),
                ("section", "VAR_INPUT"), ("type", "REAL"), ("initialValue", "1.5"), ("attributes", new[] { "hide" }));
            await OkAsync("twincat_plc_edit_entries", ("path", path), ("kind", "POU"), ("op", "add"), ("name", "nTmp"),
                ("section", "VAR_TEMP"), ("type", "INT"));

            string entries = await OkAsync("twincat_plc_list_entries", ("path", path), ("kind", "POU"));
            Assert.Contains("[VAR_INPUT] rSpeed : REAL := 1.5", entries);
            Assert.Contains("{attribute 'hide'}", entries);
            Assert.Contains("[VAR_TEMP] nTmp : INT", entries);

            // Members: a METHOD (declaration + implementation) and a PROPERTY with a Get accessor.
            string method = await OkAsync("twincat_plc_create", ("kind", "MEMBER"), ("subtype", "METHOD"),
                ("parentPath", path), ("name", "Run"), ("returnType", "BOOL"));
            string methodPath = $"{path}^Run";
            Assert.Contains(methodPath, method);
            await OkAsync("twincat_plc_edit_entries", ("path", methodPath), ("kind", "MEMBER"), ("op", "add"), ("name", "bGo"),
                ("section", "VAR_INPUT"), ("type", "BOOL"));
            await OkAsync("twincat_plc_write", ("path", methodPath), ("kind", "MEMBER"), ("implementation", "Run := bGo;"));
            string methodRead = await OkAsync("twincat_plc_read", ("path", methodPath), ("kind", "MEMBER"));
            Assert.Contains("bGo : BOOL", methodRead);
            Assert.Contains("Run := bGo;", methodRead);

            await OkAsync("twincat_plc_create", ("kind", "MEMBER"), ("subtype", "PROPERTY"),
                ("parentPath", path), ("name", "Speed"), ("returnType", "REAL"), ("accessors", "GET"));
            string members = await OkAsync("twincat_plc_list", ("parentPath", path));
            Assert.Contains("MEMBER/METHOD", members);
            Assert.Contains("MEMBER/PROPERTY", members);
            Assert.Contains("MEMBER/GET", members);
            Assert.DoesNotContain("MEMBER/SET", members);

            // Get/Set are fixed names; a rename is refused before any logout.
            string accessorRename = await CallAsync("twincat_plc_rename", ("path", $"{path}^Speed^Get"), ("kind", "MEMBER"), ("newName", "Read"));
            Assert.Contains("fixed names", accessorRename);

            string renamed = await OkAsync("twincat_plc_rename", ("path", methodPath), ("kind", "MEMBER"), ("newName", "Start"));
            Assert.Contains($"{path}^Start", renamed);
        }
        finally { await TryDeleteAsync(path, "POU"); }
    }

    [Trait("Description", "FOLDER: anlegen, leeren Ordner löschen; gefüllten Ordner nur mit recursive=true löschen (ohne bleibt der Inhalt erhalten).")]
    [SkippableFact(Timeout = 480_000)]
    public async Task Folder_CreateDeleteEmpty_AndRecursive()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string empty = $"Fld_Empty_{Stamp()}";
        string full = $"Fld_Full_{Stamp()}";
        string emptyPath = $"{FixtureLayout.PousFolder}^{empty}";
        string fullPath = $"{FixtureLayout.PousFolder}^{full}";
        string innerPath = $"{fullPath}^FB_Inner";

        try
        {
            await OkAsync("twincat_plc_create", ("kind", "FOLDER"), ("parentPath", FixtureLayout.PousFolder), ("name", empty));
            Assert.Contains(emptyPath, await OkAsync("twincat_plc_list", ("kind", "FOLDER")));
            Assert.Contains("Deleted FOLDER", await OkAsync("twincat_plc_delete", ("path", emptyPath), ("kind", "FOLDER")));

            await OkAsync("twincat_plc_create", ("kind", "FOLDER"), ("parentPath", FixtureLayout.PousFolder), ("name", full));
            await OkAsync("twincat_plc_create", ("kind", "POU"), ("subtype", "FUNCTION_BLOCK"), ("parentPath", fullPath), ("name", "FB_Inner"));

            string refused = await CallAsync("twincat_plc_delete", ("path", fullPath), ("kind", "FOLDER"));
            Assert.StartsWith("[Error]", refused);
            Assert.Contains("recursive=true", refused);
            Assert.DoesNotContain("[Error]", await OkAsync("twincat_plc_read", ("path", innerPath), ("kind", "POU")));

            Assert.Contains("Deleted FOLDER", await OkAsync("twincat_plc_delete", ("path", fullPath), ("kind", "FOLDER"), ("recursive", true)));
            Assert.StartsWith("[Error]", await CallAsync("twincat_plc_read", ("path", innerPath), ("kind", "POU")));

            // A POU is not a folder: recursive does not apply to it.
            string notFolder = await CallAsync("twincat_plc_delete", ("path", FixtureLayout.MainPath), ("kind", "POU"), ("recursive", true));
            Assert.Contains("recursive does not apply", notFolder);
        }
        finally
        {
            await TryDeleteAsync(emptyPath, "FOLDER", recursive: true);
            await TryDeleteAsync(fullPath, "FOLDER", recursive: true);
        }
    }

    // ── graphical implementations, transitions, elements without content ─────

    [Trait("Description", "LD-POU: plc_read liefert die Implementierung als XAE-XML; plc_write nimmt XML mit passender Wurzel (Read-Back exakt), lehnt ST-Text vor dem Logout ab, Deklaration bleibt schreibbar.")]
    [SkippableFact(Timeout = 480_000)]
    public async Task GraphicalPou_ImplementationIsXml_ReadableAndWritable()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"PRG_PlcObjLd_{Stamp()}";
        string path = $"{FixtureLayout.PousFolder}^{name}";

        try
        {
            await OkAsync("twincat_plc_create", ("kind", "POU"), ("subtype", "PROGRAM"),
                ("parentPath", FixtureLayout.PousFolder), ("name", name), ("implLanguage", "LD"));

            string read = await OkAsync("twincat_plc_read", ("path", path), ("kind", "POU"));
            Assert.Contains("(LD)", read);
            string xml = ImplementationOf(read).Trim();
            Assert.StartsWith("<NWL>", xml);
            Assert.Contains("<v n=\"NetworkListComment\">\"\"</v>", xml);

            string changed = xml.Replace("<v n=\"NetworkListComment\">\"\"</v>", "<v n=\"NetworkListComment\">\"live comment\"</v>", StringComparison.Ordinal);
            string write = await OkAsync("twincat_plc_write", ("path", path), ("kind", "POU"), ("implementation", changed));
            Assert.Contains($"Wrote POU '{path}'", write);
            Assert.Contains("live comment", await OkAsync("twincat_plc_read", ("path", path), ("kind", "POU")));

            // ST text into an LD POU: refused before the logout, the XML stays.
            string st = await CallAsync("twincat_plc_write", ("path", path), ("kind", "POU"), ("implementation", "x := 1;"));
            Assert.StartsWith("[Error]", st);
            Assert.Contains("is implemented in LD", st);
            Assert.Contains("<NWL>", st);
            string wrongRoot = await CallAsync("twincat_plc_write", ("path", path), ("kind", "POU"), ("implementation", "<SFC/>"));
            Assert.Contains("the XML root is <SFC>", wrongRoot);
            Assert.Contains("live comment", await OkAsync("twincat_plc_read", ("path", path), ("kind", "POU")));

            // The declaration of a graphical POU is text and stays editable.
            await OkAsync("twincat_plc_edit_entries", ("path", path), ("kind", "POU"), ("op", "add"), ("name", "nA"),
                ("section", "VAR"), ("type", "INT"));
            Assert.Contains("nA : INT", await OkAsync("twincat_plc_read", ("path", path), ("kind", "POU")));
        }
        finally { await TryDeleteAsync(path, "POU"); }
    }

    [Trait("Description", "SFC-POU: TRANSITION anlegen (ST, nur Implementierung), Implementierung schreiben/lesen, als MEMBER/TRANSITION auflisten, umbenennen, löschen; ACTION in einer Sprache anlegen.")]
    [SkippableFact(Timeout = 480_000)]
    public async Task SfcPou_TransitionAndAction_Lifecycle()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"PRG_PlcObjSfc_{Stamp()}";
        string path = $"{FixtureLayout.PousFolder}^{name}";

        try
        {
            await OkAsync("twincat_plc_create", ("kind", "POU"), ("subtype", "PROGRAM"),
                ("parentPath", FixtureLayout.PousFolder), ("name", name), ("implLanguage", "SFC"));

            string create = await OkAsync("twincat_plc_create", ("kind", "MEMBER"), ("subtype", "TRANSITION"),
                ("parentPath", path), ("name", "TR_Go"));
            Assert.Contains($"{path}^TR_Go", create);
            string trPath = $"{path}^TR_Go";

            await OkAsync("twincat_plc_write", ("path", trPath), ("kind", "MEMBER"), ("implementation", "TRUE"));
            string read = await OkAsync("twincat_plc_read", ("path", trPath), ("kind", "MEMBER"));
            Assert.Contains("MEMBER/TRANSITION", read);
            Assert.Contains("TRUE", ImplementationOf(read));
            Assert.DoesNotContain("--- Declaration ---", read);

            // A declaration for a transition is refused before the logout.
            string decl = await CallAsync("twincat_plc_write", ("path", trPath), ("kind", "MEMBER"), ("declaration", "VAR\nEND_VAR"));
            Assert.Contains("has no declaration section", decl);

            string members = await OkAsync("twincat_plc_list", ("parentPath", path));
            Assert.Contains("MEMBER/TRANSITION", members);
            Assert.Contains(trPath, members);

            await OkAsync("twincat_plc_create", ("kind", "MEMBER"), ("subtype", "ACTION"),
                ("parentPath", path), ("name", "ACT_Run"), ("implLanguage", "ST"));
            Assert.Contains("MEMBER/ACTION", await OkAsync("twincat_plc_list", ("parentPath", path)));

            // TC 4026 asks "Automatisches Umbenennen über Refactoring" here; the tool answers it (default "Nein")
            // instead of blocking every later call with "operation in progress".
            string renamed = await OkAsync("twincat_plc_rename", ("path", trPath), ("kind", "MEMBER"), ("newName", "TR_Done"));
            Assert.Contains($"{path}^TR_Done", renamed);
            Assert.Contains("Refactoring prompt:", renamed);
            Assert.DoesNotContain("answered 'Ja'", renamed);
            Assert.Contains("Deleted", await OkAsync("twincat_plc_delete", ("path", $"{path}^TR_Done"), ("kind", "MEMBER")));
        }
        finally { await TryDeleteAsync(path, "POU"); }
    }

    [Trait("Description", "METHOD umbenennen, die ein PROGRAM aufruft: updateReferences=true beantwortet den Refactoring-Dialog mit 'Ja' (Aufrufer umgeschrieben), Standard 'Nein' lässt den Aufrufer unverändert; kein Dialog bleibt offen.")]
    [SkippableFact(Timeout = 480_000)]
    public async Task Rename_RefactoringPrompt_AnsweredAsAsked()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string stamp = Stamp();
        string fb = $"FB_PlcObjRen_{stamp}", prg = $"PRG_PlcObjRen_{stamp}";
        string fbPath = $"{FixtureLayout.PousFolder}^{fb}", prgPath = $"{FixtureLayout.PousFolder}^{prg}";

        try
        {
            await OkAsync("twincat_plc_create", ("kind", "POU"), ("subtype", "FUNCTION_BLOCK"), ("parentPath", FixtureLayout.PousFolder), ("name", fb));
            await OkAsync("twincat_plc_create", ("kind", "MEMBER"), ("subtype", "METHOD"), ("parentPath", fbPath), ("name", "M_Old"), ("returnType", "BOOL"));
            await OkAsync("twincat_plc_create", ("kind", "POU"), ("subtype", "PROGRAM"), ("parentPath", FixtureLayout.PousFolder), ("name", prg));
            await OkAsync("twincat_plc_write", ("path", prgPath), ("kind", "POU"),
                ("declaration", $"PROGRAM {prg}\r\nVAR\r\n\tfbX : {fb};\r\nEND_VAR"), ("implementation", "fbX.M_Old();"));

            string yes = await OkAsync("twincat_plc_rename", ("path", $"{fbPath}^M_Old"), ("kind", "MEMBER"), ("newName", "M_Yes"), ("updateReferences", true));
            Assert.Contains($"{fbPath}^M_Yes", yes);
            // #239: some XAE installations rename (and rewrite callers) without ever showing the prompt.
            Skip.If(yes.Contains("Refactoring prompt: none appeared", StringComparison.Ordinal),
                "This XAE shows no 'rename via refactoring' prompt (it refactors on its own) — nothing to answer; see #239.");
            Assert.Contains("answered 'Ja'", yes);
            Assert.Contains("fbX.M_Yes();", ImplementationOf(await OkAsync("twincat_plc_read", ("path", prgPath), ("kind", "POU"))));

            string no = await OkAsync("twincat_plc_rename", ("path", $"{fbPath}^M_Yes"), ("kind", "MEMBER"), ("newName", "M_No"));
            Assert.Contains($"{fbPath}^M_No", no);
            Assert.Contains("answered 'Nein'", no);
            Assert.Contains("fbX.M_Yes();", ImplementationOf(await OkAsync("twincat_plc_read", ("path", prgPath), ("kind", "POU"))));
        }
        finally
        {
            await TryDeleteAsync(prgPath, "POU");
            await TryDeleteAsync(fbPath, "POU");
        }
    }

    private string PousDir =>
        Path.Combine(Path.GetDirectoryName(_fixtureSln)!, FixtureLayout.SolutionName, FixtureLayout.PlcName, "POUs");

    // #23 c: after a rename and a save, the file on disk must carry the new name (seen once: the XAE did not mark
    // the element changed, Save skipped it, and the disk kept the old name while COM showed the new one).
    [Trait("Description", "#23c: METHOD (abwechselnd mit/ohne updateReferences), ACTION und PROPERTY in 5 Runden umbenennen und speichern, dann den FB selbst → die Datei auf der Platte trägt jedes Mal den neuen Namen, keinen alten.")]
    [SkippableFact(Timeout = 900_000)]
    public async Task Rename_TheFileOnDiskFollows_AfterEverySave()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string stamp = Stamp();
        string fb = $"FB_RenDisk_{stamp}";
        string fbPath = $"{FixtureLayout.PousFolder}^{fb}";
        string current = fbPath;

        try
        {
            await OkAsync("twincat_plc_create", ("kind", "POU"), ("subtype", "FUNCTION_BLOCK"), ("parentPath", FixtureLayout.PousFolder), ("name", fb));
            await OkAsync("twincat_plc_create", ("kind", "MEMBER"), ("subtype", "METHOD"), ("parentPath", fbPath), ("name", "M_R0"), ("returnType", "BOOL"));
            await OkAsync("twincat_plc_create", ("kind", "MEMBER"), ("subtype", "ACTION"), ("parentPath", fbPath), ("name", "ACT_R0"), ("implLanguage", "ST"));
            await OkAsync("twincat_plc_create", ("kind", "MEMBER"), ("subtype", "PROPERTY"), ("parentPath", fbPath), ("name", "P_R0"), ("returnType", "INT"));
            await OkAsync("twincat_save_project");

            string file = Path.Combine(PousDir, fb + ".TcPOU");
            Assert.True(File.Exists(file), $"no file {file}");

            for (int round = 1; round <= 5; round++)
            {
                bool updateReferences = round % 2 == 0;
                foreach (var prefix in new[] { "M_R", "ACT_R", "P_R" })
                    await OkAsync("twincat_plc_rename", ("path", $"{fbPath}^{prefix}{round - 1}"), ("kind", "MEMBER"),
                        ("newName", $"{prefix}{round}"), ("updateReferences", updateReferences));
                await OkAsync("twincat_save_project");

                string onDisk = await File.ReadAllTextAsync(file);
                foreach (var prefix in new[] { "M_R", "ACT_R", "P_R" })
                {
                    Assert.True(onDisk.Contains($"Name=\"{prefix}{round}\"", StringComparison.Ordinal),
                        $"round {round} (updateReferences={updateReferences}): {prefix}{round} is not in the saved file");
                    Assert.False(onDisk.Contains($"Name=\"{prefix}{round - 1}\"", StringComparison.Ordinal),
                        $"round {round} (updateReferences={updateReferences}): the saved file still has {prefix}{round - 1}");
                }
            }

            string renamedFb = fb + "_X";
            await OkAsync("twincat_plc_rename", ("path", fbPath), ("kind", "POU"), ("newName", renamedFb));
            current = $"{FixtureLayout.PousFolder}^{renamedFb}";
            await OkAsync("twincat_save_project");

            var declaring = Directory.EnumerateFiles(PousDir, "*.TcPOU")
                .Where(f => File.ReadAllText(f).Contains($"FUNCTION_BLOCK {renamedFb}", StringComparison.Ordinal)).ToList();
            Assert.True(declaring.Count == 1, $"files declaring {renamedFb}: {declaring.Count}");
            Assert.DoesNotContain(Directory.EnumerateFiles(PousDir, "*.TcPOU"),
                f => Regex.IsMatch(File.ReadAllText(f), $@"FUNCTION_BLOCK {Regex.Escape(fb)}\b"));
        }
        finally { await TryDeleteAsync(current, "POU"); }
    }

    [Trait("Description", "Elemente ohne zugänglichen Inhalt (TEXTLIST, IMAGEPOOL, UML, VISU, RECIPE, TASKREF): anlegen, auflisten (mit Subtyp), umbenennen, löschen; read/write/entries antworten [Error] mit Grund; nicht anlegbare Subtypen werden benannt.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task ContentlessElements_Lifecycle_AndContentVerbsRefuse()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string root = FixtureLayout.PlcRoot;
        string stamp = Stamp();
        string tl = $"TL_PlcObj_{stamp}", ip = $"IP_PlcObj_{stamp}", uml = $"UML_PlcObj_{stamp}",
               vm = $"VM_PlcObj_{stamp}", vo = $"VO_PlcObj_{stamp}", rm = $"RM_PlcObj_{stamp}", prg = $"PRG_PlcObjRef_{stamp}";
        string tlPath = $"{root}^{tl}", ipPath = $"{root}^{ip}", umlPath = $"{root}^{uml}", vmPath = FixtureLayout.VisuManagerPath,
               voPath = $"{root}^{vo}", rmPath = $"{root}^{rm}", prgPath = $"{FixtureLayout.PousFolder}^{prg}";
        string taskPath = $"{root}^PlcTask";
        var created = new List<(string Path, string Kind)>();

        try
        {
            await OkAsync("twincat_plc_create", ("kind", "TEXTLIST"), ("parentPath", root), ("name", tl)); created.Add((tlPath, "TEXTLIST"));
            await OkAsync("twincat_plc_create", ("kind", "IMAGEPOOL"), ("parentPath", root), ("name", ip)); created.Add((ipPath, "IMAGEPOOL"));
            await OkAsync("twincat_plc_create", ("kind", "UML"), ("parentPath", root), ("name", uml)); created.Add((umlPath, "UML"));
            // The fixture has its visualization manager (a PLC project takes only one): a second one is refused by the XAE.
            string second = await CallAsync("twincat_plc_create", ("kind", "VISU"), ("subtype", "MANAGER"), ("parentPath", root), ("name", vm));
            Assert.StartsWith("[Error]", second);
            await OkAsync("twincat_plc_create", ("kind", "VISU"), ("subtype", "VISUALIZATION"), ("parentPath", root), ("name", vo)); created.Add((voPath, "VISU"));
            await OkAsync("twincat_plc_create", ("kind", "VISU"), ("subtype", "TARGET"), ("parentPath", vmPath), ("name", $"TargetVisu{stamp}"));
            created.Add(($"{vmPath}^TargetVisu{stamp}", "VISU"));
            await OkAsync("twincat_plc_create", ("kind", "VISU"), ("subtype", "WEB"), ("parentPath", vmPath), ("name", $"WebVisu{stamp}"));
            created.Add(($"{vmPath}^WebVisu{stamp}", "VISU"));
            await OkAsync("twincat_plc_create", ("kind", "RECIPE"), ("subtype", "MANAGER"), ("parentPath", root), ("name", rm)); created.Add((rmPath, "RECIPE"));
            await OkAsync("twincat_plc_create", ("kind", "RECIPE"), ("subtype", "DEFINITION"), ("parentPath", rmPath), ("name", "Rcp_A"));
            await OkAsync("twincat_plc_create", ("kind", "POU"), ("subtype", "PROGRAM"), ("parentPath", FixtureLayout.PousFolder), ("name", prg));
            await OkAsync("twincat_plc_create", ("kind", "TASKREF"), ("parentPath", taskPath), ("name", prg));

            string all = await OkAsync("twincat_plc_list");
            Assert.Matches(new Regex($@"TEXTLIST/LOCAL\s+{Regex.Escape(tlPath)}"), all);
            Assert.Matches(new Regex($@"IMAGEPOOL/LOCAL\s+{Regex.Escape(ipPath)}"), all);
            Assert.Matches(new Regex($@"UML/CLASSDIAGRAM\s+{Regex.Escape(umlPath)}"), all);
            Assert.Matches(new Regex($@"VISU/MANAGER\s+{Regex.Escape(vmPath)}"), all);
            Assert.Matches(new Regex($@"VISU/TARGET\s+{Regex.Escape(vmPath)}\^TargetVisu{stamp}"), all);
            Assert.Matches(new Regex($@"VISU/WEB\s+{Regex.Escape(vmPath)}\^WebVisu{stamp}"), all);
            Assert.Matches(new Regex($@"RECIPE/DEFINITION\s+{Regex.Escape(rmPath)}\^Rcp_A"), all);
            Assert.Matches(new Regex($@"TASKREF/PROGRAM\s+{Regex.Escape(taskPath)}\^{Regex.Escape(prg)}"), all);

            // Content verbs: refused with the reason, nothing changes.
            foreach (var (kind, path) in new[] { ("TEXTLIST", tlPath), ("IMAGEPOOL", ipPath), ("UML", umlPath), ("VISU", vmPath), ("RECIPE", rmPath) })
            {
                string read = await CallAsync("twincat_plc_read", ("path", path), ("kind", kind));
                Assert.StartsWith("[Error]", read);
                Assert.Contains("Automation Interface", read);
                Assert.StartsWith("[Error]", await CallAsync("twincat_plc_write", ("path", path), ("kind", kind), ("declaration", "x")));
                Assert.StartsWith("[Error]", await CallAsync("twincat_plc_list_entries", ("path", path), ("kind", kind)));
            }

            // kind check still applies
            Assert.Contains("is a TEXTLIST, not a UML", await CallAsync("twincat_plc_delete", ("path", tlPath), ("kind", "UML")));

            // Not creatable in XAE: said so, nothing created
            string global = await CallAsync("twincat_plc_create", ("kind", "TEXTLIST"), ("subtype", "GLOBAL"), ("parentPath", root), ("name", "GlobalTextList"));
            Assert.Contains("cannot be created through the Automation Interface", global);

            string renamed = await OkAsync("twincat_plc_rename", ("path", tlPath), ("kind", "TEXTLIST"), ("newName", tl + "_R"));
            Assert.Contains(tlPath + "_R", renamed);
            created.RemoveAll(c => c.Path == tlPath);
            created.Add((tlPath + "_R", "TEXTLIST"));

            foreach (var (path, kind) in created.ToList())
            {
                Assert.Contains("Deleted", await OkAsync("twincat_plc_delete", ("path", path), ("kind", kind)));
                Assert.StartsWith("[Error]", await CallAsync("twincat_plc_read", ("path", path), ("kind", kind)));
                created.Remove((path, kind));
            }
            Assert.Contains("Deleted", await OkAsync("twincat_plc_delete", ("path", $"{taskPath}^{prg}"), ("kind", "TASKREF")));
        }
        finally
        {
            foreach (var (path, kind) in created) await TryDeleteAsync(path, kind);
            await TryDeleteAsync($"{taskPath}^{prg}", "TASKREF");
            await TryDeleteAsync(prgPath, "POU");
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // twincat_plc_read formats: summary line, "--- Declaration ---", text, ["--- Implementation ---", text].
    private static string DeclarationOf(string read)
    {
        const string decl = "--- Declaration ---";
        const string impl = "--- Implementation ---";
        int start = read.IndexOf(decl, StringComparison.Ordinal) + decl.Length;
        int end = read.IndexOf(impl, start, StringComparison.Ordinal);
        return (end < 0 ? read[start..] : read[start..end]).Trim('\r', '\n');
    }

    private static string ImplementationOf(string read)
    {
        const string impl = "--- Implementation ---";
        int i = read.IndexOf(impl, StringComparison.Ordinal);
        return i < 0 ? string.Empty : read[(i + impl.Length)..];
    }

    // ── search / replace_text ─────────────────────────────────────────────────

    private static readonly Regex SearchHit = new(
        @"^\S+ (?<path>\S.*?) — (?<section>declaration|implementation) L(?<line>\d+):(?<col>\d+) \(Revision: (?<rev>[0-9a-f]{16})\)\r?$",
        RegexOptions.Multiline);

    [Trait("Description", "plc_search findet ein Token in Deklaration und Implementierung eines neuen FB mit den Zeilen, die plc_read zeigt — projektweit und mit pathPrefix gleich.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Search_FindsEveryUse_WithTheLinesPlcReadShows()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string stamp = Stamp();
        string name = $"FB_Search_{stamp}";
        string token = $"nSearch{stamp}";
        string path = $"{FixtureLayout.PousFolder}^{name}";
        try
        {
            await OkAsync("twincat_plc_create", ("kind", "POU"), ("subtype", "FUNCTION_BLOCK"),
                ("parentPath", FixtureLayout.PousFolder), ("name", name),
                ("declaration", $"FUNCTION_BLOCK {name}\nVAR\n\t{token} : INT;\nEND_VAR"));
            await OkAsync("twincat_plc_write", ("path", path), ("kind", "POU"),
                ("implementation", $"{token} := {token} + 1;\nIF {token} > 10 THEN\n\t{token} := 0;\nEND_IF"));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string all = await OkAsync("twincat_plc_search", ("pattern", token), ("wholeWord", true));
            sw.Stop();
            Console.WriteLine($"[plc_search] whole fixture: {sw.ElapsedMilliseconds} ms — {all.Split('\n')[0]}");

            var hits = SearchHit.Matches(all).Select(m => (Path: m.Groups["path"].Value, Section: m.Groups["section"].Value,
                Line: int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture))).ToList();
            Assert.Equal(
                new[]
                {
                    (path, "declaration", 3), (path, "implementation", 1), (path, "implementation", 1),
                    (path, "implementation", 2), (path, "implementation", 3),
                },
                hits.Select(h => (h.Path, h.Section, h.Line)));

            // Every reported line is the line plc_read shows.
            string read = await OkAsync("twincat_plc_read", ("path", path), ("kind", "POU"));
            string[] declLines = Normalise(DeclarationOf(read)).Split('\n');
            string[] implLines = Normalise(ImplementationOf(read)).Split('\n');
            foreach (var h in hits)
                Assert.Contains(token, (h.Section == "declaration" ? declLines : implLines)[h.Line - 1]);

            string scoped = await OkAsync("twincat_plc_search", ("pattern", token), ("wholeWord", true), ("pathPrefix", path));
            Assert.Equal(SearchHit.Matches(all).Select(m => m.Value), SearchHit.Matches(scoped).Select(m => m.Value));

            // The paging convention: the last line is the page footer; a second page continues where the first stopped.
            Assert.EndsWith("Page: offset 0, returned 5 of 5; has_more false", all.TrimEnd());
            string second = await OkAsync("twincat_plc_search", ("pattern", token), ("wholeWord", true), ("limit", 2), ("offset", 2));
            Assert.EndsWith("Page: offset 2, returned 2 of 5; has_more true, next_offset 4", second.TrimEnd());
            Assert.Equal(SearchHit.Matches(all).Select(m => m.Value).Skip(2).Take(2), SearchHit.Matches(second).Select(m => m.Value));
        }
        finally
        {
            await TryDeleteAsync(path, "POU");
        }
    }

    [Trait("Description", "plc_replace_text ersetzt genau eine Stelle (Rest byte-gleich), verweigert mehrdeutigen Text und veraltete Revision vor dem Logout; edit_entries verweigert veraltete Revision.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task ReplaceText_ChangesExactlyOnePlace_AndRefusesStaleOrAmbiguousEdits()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string name = $"FB_Replace_{Stamp()}";
        string path = $"{FixtureLayout.PousFolder}^{name}";
        try
        {
            await OkAsync("twincat_plc_create", ("kind", "POU"), ("subtype", "FUNCTION_BLOCK"),
                ("parentPath", FixtureLayout.PousFolder), ("name", name),
                ("declaration", $"FUNCTION_BLOCK {name}\nVAR\n\tnCount : INT;\nEND_VAR"));
            await OkAsync("twincat_plc_write", ("path", path), ("kind", "POU"),
                ("implementation", "nCount := nCount + 1;\nIF nCount > 10 THEN\n\tnCount := 0;\nEND_IF"));

            string before = await OkAsync("twincat_plc_read", ("path", path), ("kind", "POU"));
            string revision = RevisionOf(before);

            string ambiguous = await CallAsync("twincat_plc_replace_text", ("path", path), ("kind", "POU"),
                ("section", "implementation"), ("oldText", "nCount"), ("newText", "nValue"));
            Assert.StartsWith("[Error] oldText occurs", ambiguous);

            string stale = await CallAsync("twincat_plc_replace_text", ("path", path), ("kind", "POU"),
                ("section", "implementation"), ("oldText", "> 10"), ("newText", "> 20"), ("expectedRevision", "0000000000000000"));
            Assert.StartsWith($"[Error] '{path}' changed since it was read", stale);

            string replaced = await OkAsync("twincat_plc_replace_text", ("path", path), ("kind", "POU"),
                ("section", "implementation"), ("oldText", "> 10"), ("newText", "> 20"), ("expectedRevision", revision));
            Assert.Contains("line 2 (verified by read-back)", replaced);
            Assert.Contains("> L2: IF nCount > 20 THEN", replaced);

            string after = await OkAsync("twincat_plc_read", ("path", path), ("kind", "POU"));
            Assert.Equal(Normalise(ImplementationOf(before)).Replace("> 10", "> 20", StringComparison.Ordinal), Normalise(ImplementationOf(after)));
            Assert.Equal(Normalise(DeclarationOf(before)), Normalise(DeclarationOf(after)));
            Assert.Equal(RevisionOf(replaced), RevisionOf(after));

            string staleEntries = await CallAsync("twincat_plc_edit_entries", ("path", path), ("kind", "POU"), ("op", "add"),
                ("name", "bNever"), ("section", "VAR"), ("type", "BOOL"), ("expectedRevision", revision));
            Assert.StartsWith($"[Error] '{path}' changed since it was read", staleEntries);
            Assert.DoesNotContain("bNever", await OkAsync("twincat_plc_read", ("path", path), ("kind", "POU")));
        }
        finally
        {
            await TryDeleteAsync(path, "POU");
        }
    }
}
