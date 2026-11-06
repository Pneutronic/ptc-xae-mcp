using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// twincat_plc_move on the coordinator: every refusal happens before the logout and before anything is exported or
// deleted; the success path exports, deletes, imports the rewritten file and verifies; a failure after the delete
// re-imports the original into its old folder.
public sealed class PlcMoveCoordinatorTests : IDisposable
{
    private const string Root = "TIPC^Plc^Plc Project";
    private const string Pous = Root + "^POUs";
    private const string Target = Pous + "^Target";
    private const string Fb = Pous + "^FB_X";
    private const string MembersFolder = Fb + "^Members";
    private const string Reset = MembersFolder + "^Reset";
    private const string Enabled = Fb + "^Enabled";
    private const string EnabledGet = Enabled + "^Get";
    private const string Run = Fb + "^Run";
    private const string Other = Pous + "^FB_Other";
    private const string Gvl = Root + "^GVLs^GVL_Main";

    private const string ExportXml =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<project xmlns=\"http://www.plcopen.org/xml/tc6_0200\">\r\n  <addData>\r\n" +
        "    <data name=\"http://www.3s-software.com/plcopenxml/projectstructure\" handleUnknown=\"discard\">\r\n" +
        "      <ProjectStructure>\r\n" +
        "        <Object Name=\"FB_X\" ObjectId=\"04d45a1f-b0f0-4187-8258-5f6be033085c\">\r\n" +
        "          <Folder Name=\"Members\">\r\n" +
        "            <Object Name=\"Reset\" ObjectId=\"b53af92c-60bb-48c4-aaa2-8bfb7cf4bdfc\" />\r\n" +
        "          </Folder>\r\n" +
        "          <Object Name=\"Enabled\" ObjectId=\"7b8e93e3-8545-4177-ac94-4092230dbcc1\" />\r\n" +
        "          <Object Name=\"Run\" ObjectId=\"11111111-2222-3333-4444-555555555555\" />\r\n" +
        "        </Object>\r\n" +
        "      </ProjectStructure>\r\n    </data>\r\n  </addData>\r\n</project>";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptc-move-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // A small PLC tree the fake serves; Publish() pushes it into the fake, MoveNodes() relocates it like an import would.
    private sealed class Tree
    {
        public FakeTwinCatSession Fake { get; } = new() { IsConnected = true, SolutionOpen = true, PlcOpenExportXml = ExportXml };
        public Dictionary<string, (int Type, PouBody? Body)> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Tree()
        {
            Node(Root, PlcItemType.PlcApp);
            Node(Pous, PlcItemType.Folder);
            Node(Target, PlcItemType.Folder);
            Node(Root + "^GVLs", PlcItemType.Folder);
            Node(Gvl, PlcItemType.Gvl, new PouBody("VAR_GLOBAL\nEND_VAR", "", ""));
            Node(Fb, PlcItemType.PouFunctionBlock, new PouBody("FUNCTION_BLOCK FB_X\nVAR\nEND_VAR", "x := 1;", "ST"));
            Node(MembersFolder, PlcItemType.Folder);
            Node(Reset, PlcItemType.Method, new PouBody("METHOD Reset : BOOL", "Reset := TRUE;", "ST"));
            Node(Enabled, PlcItemType.Property, new PouBody("PROPERTY Enabled : BOOL", "", "ST"));
            Node(EnabledGet, PlcItemType.PropertyGet, new PouBody("VAR\nEND_VAR", "Enabled := TRUE;", "ST"));
            Node(Run, PlcItemType.Method, new PouBody("METHOD Run : BOOL", "Run := FALSE;", "ST"));
            Node(Other, PlcItemType.PouFunctionBlock, new PouBody("FUNCTION_BLOCK FB_Other", "", "ST"));
            Node(Other + "^Stuff", PlcItemType.Folder);
            Publish();
        }

        public void Node(string path, int type, PouBody? body = null) => Nodes[path] = (type, body);

        public void Publish()
        {
            Fake.MembersByPou.Clear();
            Fake.FoldersByPou.Clear();
            foreach (var (path, node) in Nodes)
            {
                Fake.PlcNodeItemTypes[path] = node.Type;
                Fake.MissingPlcPaths.Remove(path);
                if (node.Body is not null)
                {
                    Fake.PouBodiesByPath[path] = node.Body;
                    Fake.GvlDeclarationsByPath[path] = node.Body.Declaration;
                }
                if (node.Type is not (PlcItemType.PouFunctionBlock or PlcItemType.Interface)) continue;
                var below = Nodes.Where(n => n.Key.StartsWith(path + "^", StringComparison.OrdinalIgnoreCase)).ToList();
                Fake.MembersByPou[path] = below.Where(n => n.Value.Type != PlcItemType.Folder)
                    .Select(n => new PouMemberEntry(n.Key, n.Key[(n.Key.LastIndexOf('^') + 1)..], PlcMemberKind.FromItemType(n.Value.Type)!, n.Value.Type))
                    .ToList();
                Fake.FoldersByPou[path] = below.Where(n => n.Value.Type == PlcItemType.Folder).Select(n => n.Key).ToList();
            }
        }

        public void MoveNodes(string from, string to)
        {
            string Map(string p) => p.Equals(from, StringComparison.OrdinalIgnoreCase) || p.StartsWith(from + "^", StringComparison.OrdinalIgnoreCase)
                ? to + p[from.Length..] : p;
            var moved = Nodes.ToDictionary(n => Map(n.Key), n => n.Value, StringComparer.OrdinalIgnoreCase);
            foreach (string old in Nodes.Keys.Where(k => !moved.ContainsKey(k)))
            {
                Fake.MissingPlcPaths.Add(old);
                Fake.PouBodiesByPath.Remove(old);
            }
            Nodes.Clear();
            foreach (var (p, n) in moved) Nodes[p] = n;
            Publish();
        }
    }

    private PlcObjectCoordinator Coord(Tree t) => new(t.Fake) { MoveDirectory = _dir };

    private static void AssertNothingHappened(FakeTwinCatSession fake)
    {
        fake.CallLog.Should().NotContain(c => c.StartsWith("LogoutPlc", StringComparison.Ordinal));
        fake.CallLog.Should().NotContain(c => c.StartsWith("PlcOpen", StringComparison.Ordinal));
        fake.CallLog.Should().NotContain(c => c.StartsWith("DeletePlc", StringComparison.Ordinal));
    }

    // ---- refusals before the logout -------------------------------------------

    public static TheoryData<string, string, string, string> Refusals => new()
    {
        { Fb, "BANANA", Target, "Unknown kind 'BANANA'" },
        { Fb, "FOLDER", Target, "kind FOLDER cannot be moved" },
        { Fb, "DUT", Target, "is a POU, not a DUT" },
        { Fb, "POU", Pous + "^Nope", "targetFolderPath not found" },
        { Fb, "POU", Other, "is not a FOLDER but a POU" },
        { Fb, "POU", Pous, "is already in" },
        { Fb, "POU", Other + "^Stuff", "lies below '" + Other + "', which is not a folder" },
        { Fb, "POU", "TIPC^Plc2^Plc2 Project^POUs", "is not in the PLC project" },
        { Reset, "MEMBER", Other + "^Stuff", "moves only inside its own POU/INTERFACE" },
        { Reset, "MEMBER", Target, "moves only inside its own POU/INTERFACE" },
        { Reset, "MEMBER", MembersFolder, "is already in" },
        { Run, "MEMBER", Fb, "is already in" },
        { Run, "MEMBER", Reset, "is not a FOLDER but a MEMBER" },
        { EnabledGet, "MEMBER", MembersFolder, "property accessor" },
        { Gvl, "GVL", Root + "^GVLs", "is already in" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Move_Refusals_HappenBeforeTheLogout(string path, string kind, string target, string expected)
    {
        var t = new Tree();
        t.Fake.MissingPlcPaths.Add(Pous + "^Nope");

        string result = Coord(t).Move(path, kind, target);

        result.Should().StartWith("[Error]").And.Contain(expected);
        AssertNothingHappened(t.Fake);
    }

    [Theory]
    [InlineData("", "POU", Target, "path is required")]
    [InlineData(Fb, "POU", "  ", "targetFolderPath is required")]
    public void Move_MissingArguments_AreRefused(string path, string kind, string target, string expected)
    {
        var t = new Tree();
        Coord(t).Move(path, kind, target).Should().StartWith("[Error]").And.Contain(expected);
        AssertNothingHappened(t.Fake);
    }

    [Fact]
    public void Move_NameAlreadyInTarget_IsRefused()
    {
        var t = new Tree();
        t.Node(Target + "^FB_X", PlcItemType.Folder);
        t.Publish();

        Coord(t).Move(Fb, "POU", Target).Should().Contain("already holds an element named 'FB_X'");
        AssertNothingHappened(t.Fake);
    }

    [Fact]
    public void Move_FolderWithADot_IsRefused()
    {
        var t = new Tree();
        t.Node(Pous + "^v1.2", PlcItemType.Folder);
        t.Publish();
        t.Fake.MissingPlcPaths.Add(Pous + "^v1.2^FB_X");

        Coord(t).Move(Fb, "POU", Pous + "^v1.2").Should().Contain("has a '.' in its name");
        AssertNothingHappened(t.Fake);
    }

    [Fact]
    public void Move_UnreadableElement_IsRefusedBeforeTheLogout()
    {
        var t = new Tree();
        t.Fake.MissingPlcPaths.Add(Target + "^FB_X");
        t.Fake.ReadPouMemberThrows = new InvalidOperationException("boom");

        Coord(t).Move(Fb, "POU", Target).Should().StartWith("[Error] Could not read").And.Contain("Nothing was changed");
        AssertNothingHappened(t.Fake);
    }

    [Fact]
    public void Move_NoSolution_IsRefused()
    {
        var t = new Tree();
        t.Fake.SolutionOpen = false;

        Coord(t).Move(Fb, "POU", Target).Should().StartWith("[Error] No solution is currently open.\nNext: ");
        AssertNothingHappened(t.Fake);
    }

    // ---- success ---------------------------------------------------------------

    [Fact]
    public void Move_Pou_ExportsDeletesImportsAndVerifies()
    {
        var t = new Tree();
        t.Fake.MissingPlcPaths.Add(Target + "^FB_X");
        t.Fake.OnPlcOpenImportInto = _ => t.MoveNodes(Fb, Target + "^FB_X");

        string result = Coord(t).Move(Fb, "pou", Target);

        result.Should().StartWith($"Moved POU '{Fb}' to '{Target}^FB_X' (object GUID kept, 4 members, verified by read-back).");
        result.Should().NotContain("[Warn]");
        var log = t.Fake.CallLog;
        int logout = log.FindIndex(c => c.StartsWith("LogoutPlc", StringComparison.Ordinal));
        int export = log.IndexOf($"PlcOpenExportFrom({Root}, POUs.FB_X)");
        int delete = log.IndexOf($"DeletePlcObject({Fb})");
        int import = log.FindIndex(c => c.StartsWith($"PlcOpenImportInto({Root}, ", StringComparison.Ordinal) && c.EndsWith(".moved.xml)", StringComparison.Ordinal));
        logout.Should().BeGreaterThan(-1);
        export.Should().BeGreaterThan(logout);
        delete.Should().BeGreaterThan(export);
        import.Should().BeGreaterThan(delete);
        log.Should().Contain($"PlcOpenExportFrom({Root}, POUs.Target.FB_X)", "the moved object is exported again to compare the GUIDs");

        t.Fake.PlcOpenImportedXml.Should().ContainSingle()
            .Which.Should().Contain("<Folder Name=\"POUs\"><Folder Name=\"Target\"><Object Name=\"FB_X\"");
        string exportFile = result[(result.IndexOf("Export of the original: ", StringComparison.Ordinal) + "Export of the original: ".Length)..];
        File.Exists(exportFile).Should().BeTrue("the original export is kept");
        File.ReadAllText(exportFile).Should().Be(ExportXml);
    }

    [Fact]
    public void Move_MemberIntoNewSubfolderOfItsPou_ReimportsThePouInItsOwnFolder()
    {
        var t = new Tree();
        string methods = Fb + "^Methods";
        t.Node(methods, PlcItemType.Folder);
        t.Publish();
        t.Fake.MissingPlcPaths.Add(methods + "^Reset");
        t.Fake.OnPlcOpenImportInto = _ => t.MoveNodes(Reset, methods + "^Reset");

        string result = Coord(t).Move(Reset, "MEMBER", methods);

        result.Should().StartWith($"Moved MEMBER '{Reset}' to '{methods}^Reset' (object GUID kept, 4 members, verified by read-back).");
        t.Fake.CallLog.Should().Contain($"DeletePlcObject({Fb})", "a member cannot be exported alone: its POU is moved as a whole");
        string imported = t.Fake.PlcOpenImportedXml.Should().ContainSingle().Subject;
        imported.Should().Contain("<Folder Name=\"POUs\"><Object Name=\"FB_X\"", "the POU stays in its own folder");
        imported.Should().Contain("<Folder Name=\"Methods\"><Object Name=\"Reset\"");
    }

    [Fact]
    public void Move_LostEmptyFolder_IsAWarning()
    {
        var t = new Tree();
        t.Node(Fb + "^Empty", PlcItemType.Folder);
        t.Publish();
        t.Fake.MissingPlcPaths.Add(Target + "^FB_X");
        t.Fake.OnPlcOpenImportInto = _ =>
        {
            t.Nodes.Remove(Fb + "^Empty");
            t.Fake.MissingPlcPaths.Add(Fb + "^Empty");
            t.MoveNodes(Fb, Target + "^FB_X");
        };

        string result = Coord(t).Move(Fb, "POU", Target);

        result.Should().StartWith("Moved POU").And.Contain($"[Warn] Folder '{Target}^FB_X^Empty' did not come back");
    }

    // ---- failures ----------------------------------------------------------------

    [Fact]
    public void Move_ExportFails_ChangesNothing()
    {
        var t = new Tree();
        t.Fake.MissingPlcPaths.Add(Target + "^FB_X");
        t.Fake.PlcOpenExportFromThrows = new InvalidOperationException("Couldn't create ExportObjects");

        string result = Coord(t).Move(Fb, "POU", Target);

        result.Should().StartWith("[Error] The PLCopen export").And.Contain("Couldn't create ExportObjects").And.Contain("Nothing was changed");
        t.Fake.CallLog.Should().NotContain(c => c.StartsWith("DeletePlc", StringComparison.Ordinal));
        t.Fake.CallLog.Should().NotContain(c => c.StartsWith("PlcOpenImport", StringComparison.Ordinal));
    }

    [Fact]
    public void Move_StructureWithoutTheElement_ChangesNothing()
    {
        var t = new Tree();
        t.Fake.MissingPlcPaths.Add(Target + "^FB_X");
        t.Fake.PlcOpenExportXml = "<project />";

        Coord(t).Move(Fb, "POU", Target).Should().Contain("has no project structure").And.Contain("Nothing was changed");
        t.Fake.CallLog.Should().NotContain(c => c.StartsWith("DeletePlc", StringComparison.Ordinal));
    }

    [Fact]
    public void Move_ImportFails_RestoresTheOriginalInItsOldFolder()
    {
        var t = new Tree();
        t.Fake.MissingPlcPaths.Add(Target + "^FB_X");
        t.Fake.PlcOpenImportIntoThrows.Enqueue(new InvalidOperationException("import refused"));
        var snapshot = t.Nodes.ToDictionary(n => n.Key, n => n.Value, StringComparer.OrdinalIgnoreCase);
        t.Fake.OnPlcOpenImportInto = _ =>
        {
            // The restore import puts the original tree back.
            foreach (var (p, n) in snapshot) t.Nodes[p] = n;
            t.Publish();
        };

        string result = Coord(t).Move(Fb, "POU", Target);

        result.Should().StartWith($"[Error] Moving POU '{Fb}' to '{Target}^FB_X' failed after the original was deleted: the import")
            .And.Contain("import refused")
            .And.Contain($"Restored: '{Fb}' is back where it was, its text unchanged (read back).")
            .And.Contain(".restore.xml");
        t.Fake.CallLog.Where(c => c.StartsWith("PlcOpenImportInto", StringComparison.Ordinal)).Should().HaveCount(2);
        t.Fake.CallLog.Last(c => c.StartsWith("PlcOpenImportInto", StringComparison.Ordinal)).Should().EndWith(".restore.xml)");
        t.Fake.PlcOpenImportedXml.Should().ContainSingle()
            .Which.Should().Contain("<Folder Name=\"POUs\"><Object Name=\"FB_X\"", "the restore file puts the original into its old folder");
    }

    [Fact]
    public void Move_ChangedTextAfterImport_RemovesTheCopyAndRestores()
    {
        var t = new Tree();
        t.Fake.MissingPlcPaths.Add(Target + "^FB_X");
        var snapshot = t.Nodes.ToDictionary(n => n.Key, n => n.Value, StringComparer.OrdinalIgnoreCase);
        int imports = 0;
        t.Fake.OnPlcOpenImportInto = _ =>
        {
            if (++imports == 1)
            {
                t.MoveNodes(Fb, Target + "^FB_X");
                t.Node(Target + "^FB_X^Run", PlcItemType.Method, new PouBody("METHOD Run : BOOL", "Run := TRUE; // changed", "ST"));
                t.Publish();
                return;
            }
            foreach (var (p, n) in snapshot) t.Nodes[p] = n;
            t.Publish();
        };

        string result = Coord(t).Move(Fb, "POU", Target);

        result.Should().StartWith("[Error] Moving POU").And.Contain("the read-back after the import differs from before")
            .And.Contain($"'{Target}^FB_X^Run' has revision")
            .And.Contain($"Removed the imported copy ('{Target}^FB_X')")
            .And.Contain("Restored:");
        t.Fake.CallLog.Should().Contain($"DeletePlcObject({Target}^FB_X)");
        result.Should().NotStartWith("Moved");
    }

    [Fact]
    public void Move_SomethingElseAtTheNewPath_IsNotASuccess()
    {
        var t = new Tree();
        t.Fake.MissingPlcPaths.Add(Target + "^FB_X");
        int imports = 0;
        t.Fake.OnPlcOpenImportInto = _ =>
        {
            if (++imports > 1) { t.Node(Fb, PlcItemType.PouFunctionBlock, new PouBody("FUNCTION_BLOCK FB_X\nVAR\nEND_VAR", "x := 1;", "ST")); t.Publish(); return; }
            t.MoveNodes(Fb, Target + "^FB_X");
            t.Node(Target + "^FB_X", PlcItemType.Folder);
            t.Publish();
        };

        string result = Coord(t).Move(Fb, "POU", Target);

        result.Should().StartWith("[Error] Moving POU").And.Contain("resolves after the import, but not as the moved element but a FOLDER")
            .And.Contain($"Removed the imported copy ('{Target}^FB_X')");
    }

    [Fact]
    public void Move_RestoreImportFails_SaysTheElementIsGone()
    {
        var t = new Tree();
        t.Fake.MissingPlcPaths.Add(Target + "^FB_X");
        t.Fake.PlcOpenImportIntoThrows.Enqueue(new InvalidOperationException("first"));
        t.Fake.PlcOpenImportIntoThrows.Enqueue(new InvalidOperationException("second"));

        string result = Coord(t).Move(Fb, "POU", Target);

        result.Should().StartWith("[Error]").And.Contain("Restore FAILED").And.Contain("second")
            .And.Contain($"'{Fb}' is NOT in the project now").And.Contain("twincat_plcopen_import");
    }

    [Fact]
    public void Move_DeleteDoesNotLand_ImportsNothing()
    {
        var t = new Tree();
        t.Fake.MissingPlcPaths.Add(Target + "^FB_X");
        t.Fake.NoOpMutations.Add(nameof(FakeTwinCatSession.DeletePlcObject));

        string result = Coord(t).Move(Fb, "POU", Target);

        result.Should().StartWith("[Error]").And.Contain("still resolves after the delete").And.Contain("was not moved");
        t.Fake.CallLog.Should().NotContain(c => c.StartsWith("PlcOpenImport", StringComparison.Ordinal));
    }
}
