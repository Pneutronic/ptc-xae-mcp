using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>hmi-views: create/change HMI project files. Invariant as for hmi-server: no
/// AddHmiItem/WriteHmiFile/ImportHmiFile without Confirmed/AutoConfirmed, success only after a
/// read-back, and everything validated before the engineer is asked.</summary>
public sealed class HmiViewsTests : IDisposable
{
    private const string Prj = "PlantHmi";
    private readonly string _tmp = Directory.CreateTempSubdirectory("hmi-views-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private static FakeTwinCatSession Session() => new()
    {
        IsConnected = true,
        SolutionOpen = true,
        HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1, [new HmiProjectEntry(Prj, @"C:\Temp\x\" + Prj, true, false)], null),
    };

    private static bool Wrote(FakeTwinCatSession f) => f.CallLog.Any(c =>
        c.StartsWith("AddHmiItem", StringComparison.Ordinal) || c.StartsWith("WriteHmiFile", StringComparison.Ordinal) ||
        c.StartsWith("ImportHmiFile", StringComparison.Ordinal) || c.StartsWith("DeleteHmiItem", StringComparison.Ordinal));

    private string TempFile(string name, string content)
    {
        string p = Path.Combine(_tmp, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    // ── pure helpers ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("UserControls/X/X.usercontrol", "UserControls/X/X.usercontrol")]
    [InlineData(@"UserControls\X\X.usercontrol", "UserControls/X/X.usercontrol")]
    [InlineData(" Functions/A.js/ ", "Functions/A.js")]
    [InlineData("/abs/path", null)]
    [InlineData(@"C:\Fixtures\x.view", null)]
    [InlineData("a/../b", null)]
    [InlineData("a/./b", null)]
    [InlineData("a//b", null)]
    [InlineData("a/b?.js", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Paths_Normalize(string? input, string? expected) => Assert.Equal(expected, HmiPaths.Normalize(input));

    [Fact]
    public void Paths_EmptyFolderAllowedOnlyWhenAsked()
    {
        Assert.Equal("", HmiPaths.Normalize("", allowEmpty: true));
        Assert.Null(HmiPaths.Normalize(""));
    }

    [Fact]
    public void Kinds_UserControlBringsItsJson_AndExtensionsComeFromTheVendorTable()
    {
        var (main, deps) = HmiItemKinds.Paths(HmiItemKinds.Find("usercontrol")!, "UserControls/Cam", "Cam.usercontrol");
        Assert.Equal("UserControls/Cam/Cam.usercontrol", main);
        Assert.Equal(["UserControls/Cam/Cam.usercontrol.json"], deps);
        Assert.Equal(".txt", HmiItemKinds.Find("TEXT")!.Extension);           // live: the template ends in .txt
        Assert.True(HmiItemKinds.Find("html")!.ViaImport);                      // AddItem names it x.html.html
        Assert.True(HmiItemKinds.Find("view")!.ViaImport);                      // AddItem names it x.view.view
        Assert.False(HmiItemKinds.Find("usercontrol")!.ViaImport);
        Assert.Equal(["Functions/F.function.json"], HmiItemKinds.Paths(HmiItemKinds.Find("function_js")!, "Functions", "F").Dependents);
        Assert.Null(HmiItemKinds.Find("page"));
    }

    [Fact]
    public void KeepSchema_TakesTheProjectValue_AndLeavesTheRestAlone()
    {
        string current = "{ \"$schema\": \"./../../Packages/Framework.14.3.500/Schema/X.json\", \"parameters\": [] }";
        string incoming = "{\n  \"$schema\": \"(placeholder)\",\n  \"parameters\": [ {\"name\": \"a$1\"} ]\n}";

        string result = HmiText.KeepSchema(current, incoming);

        Assert.Contains("\"$schema\": \"./../../Packages/Framework.14.3.500/Schema/X.json\"", result);
        Assert.Contains("\"name\": \"a$1\"", result);
        Assert.DoesNotContain("placeholder", result);
    }

    [Fact]
    public void KeepSchema_NoSchemaOnEitherSide_ReturnsTheNewTextUnchanged()
    {
        Assert.Equal("{\"a\":1}", HmiText.KeepSchema("<div></div>", "{\"a\":1}"));
        Assert.Equal("{\"a\":1}", HmiText.KeepSchema("{\"$schema\":\"x\"}", "{\"a\":1}"));
    }

    [Fact]
    public void Text_SameIgnoresLineEndingsAndTrailingWhitespace_DiffAndFirstDifference()
    {
        Assert.True(HmiText.Same("a\r\nb\r\n", "a\nb"));
        Assert.Equal((1, 1), HmiText.LineDiff("a\nb\nc", "a\nx\nc"));
        Assert.Equal((2, 0), HmiText.LineDiff("a", "a\nb\nc"));
        Assert.Equal(2, HmiText.FirstDifference("a\nb", "a\nX"));
    }

    // ── read_file ────────────────────────────────────────────────────────────

    [Fact]
    public void ReadFile_InvalidPath_NoCalls()
    {
        var fake = Session();
        Assert.StartsWith("[Error] Invalid project path", new HmiCoordinator(fake).ReadFile(null, "../x").Summary);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void ReadFile_ReturnsSourceAndCounts()
    {
        var fake = Session();
        fake.HmiFiles["Desktop.view"] = "<div>\n</div>";
        var (summary, json) = new HmiCoordinator(fake).ReadFile(null, "Desktop.view");
        Assert.Contains("12 characters, 2 lines", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("<div>\n</div>", doc.RootElement.GetProperty("source").GetString());
    }

    // ── add_item ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("page", "Cam")]
    [InlineData("usercontrol", "a/Cam")]
    [InlineData("usercontrol", " ")]
    public void AddItem_InvalidInput_NoCallsNoPopup(string kind, string name)
    {
        var fake = Session();
        var popup = new FakeHmiConfirmer();
        Assert.StartsWith("[Error]", new HmiCoordinator(fake, popup).AddItem(null, "UserControls", name, kind, 120).Summary);
        Assert.Empty(fake.CallLog);
        Assert.Empty(popup.Requests);
    }

    [Fact]
    public void AddItem_ExistingDependent_RefusedWithoutPopup()
    {
        var fake = Session();
        fake.HmiFiles["UserControls/Cam/Cam.usercontrol.json"] = "{}";
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).AddItem(null, "UserControls/Cam", "Cam", "usercontrol", 120);

        Assert.Contains("already exists", summary);
        Assert.Empty(popup.Requests);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void AddItem_Rejected_NothingCreated()
    {
        var fake = Session();
        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer(HmiConfirmOutcome.Rejected))
            .AddItem(null, "UserControls/Cam", "Cam", "usercontrol", 120);
        Assert.Contains("Nothing was written", summary);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void AddItem_Confirmed_CreatesMainAndDependent_PopupNamesBoth()
    {
        var fake = Session();
        var popup = new FakeHmiConfirmer();

        var (summary, json) = new HmiCoordinator(fake, popup).AddItem(null, "UserControls/Cam", "Cam", "usercontrol", 120);

        Assert.StartsWith("Created 'UserControls/Cam/Cam.usercontrol' from template UserControl", summary);
        Assert.Contains($"AddHmiItem({Prj}, UserControls/Cam, Cam, UserControl)", fake.CallLog);
        var request = Assert.Single(popup.Requests).Request;
        Assert.Equal(HmiWriteKind.AddItem, request.Kind);
        Assert.Contains("Cam.usercontrol.json", request.NewValue);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("created").GetArrayLength());
    }

    [Theory]
    [InlineData("html", "Docs", "Hilfe", "Docs/Hilfe.html")]
    [InlineData("text", "", "Notizen", "Notizen.txt")]
    public void AddItem_KindsWithBrokenTemplateNames_AreImportedEmpty(string kind, string folder, string name, string expected)
    {
        var fake = Session();
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).AddItem(null, folder, name, kind, 120);

        Assert.StartsWith($"Created '{expected}' as an empty file (imported)", summary);
        Assert.Equal("", fake.HmiFiles[expected]);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("AddHmiItem", StringComparison.Ordinal));
        Assert.Contains("via import", Assert.Single(popup.Requests).Request.NewValue);
    }

    [Fact]
    public void AddItem_View_GetsTheVendorSkeletonWithItsOwnId()
    {
        var fake = Session();

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).AddItem(null, "Views", "Wartung", "view", 120);

        Assert.StartsWith("Created 'Views/Wartung.view' from the vendor view skeleton (imported)", summary);
        string view = fake.HmiFiles["Views/Wartung.view"];
        Assert.Contains("id=\"Wartung\"", view);
        Assert.Contains("data-tchmi-type=\"TcHmi.Controls.System.TcHmiView\"", view);
        Assert.DoesNotContain("$TargetIdentifier$", view);
    }

    // ── write_file ───────────────────────────────────────────────────────────

    [Fact]
    public void WriteFile_NeedsExactlyOneSource()
    {
        var c = new HmiCoordinator(Session(), new FakeHmiConfirmer());
        Assert.StartsWith("[Error] Pass exactly one", c.WriteFile(null, "a.view", null, null, true, 120).Summary);
        Assert.StartsWith("[Error] Pass exactly one", c.WriteFile(null, "a.view", "x", @"C:\x.txt", true, 120).Summary);
    }

    [Fact]
    public void WriteFile_MissingTarget_PointsToAddItem()
    {
        var (summary, _) = new HmiCoordinator(Session(), new FakeHmiConfirmer()).WriteFile(null, "X.view", "<div/>", null, true, 120);
        Assert.Contains("twincat_hmi_add_item", summary);
    }

    [Fact]
    public void WriteFile_Unchanged_NothingToDo_NoPopup()
    {
        var fake = Session();
        fake.HmiFiles["A.view"] = "<div>\r\n</div>";
        var popup = new FakeHmiConfirmer();
        var (summary, _) = new HmiCoordinator(fake, popup).WriteFile(null, "A.view", "<div>\n</div>\n", null, true, 120);
        Assert.StartsWith("Nothing to do", summary);
        Assert.Empty(popup.Requests);
    }

    [Fact]
    public void WriteFile_KeepsTheFilesLineEndings_WhenTheSourceHasOthers()
    {
        // A CRLF file written from an LF source (git show): one changed line must not convert the whole file.
        var fake = Session();
        fake.HmiFiles["A.js"] = "function a() {\r\n  return 1;\r\n}\r\n";
        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).WriteFile(null, "A.js", "function a() {\n  return 2;\n}\n", null, true, 120);
        Assert.StartsWith("Wrote 'A.js'", summary);
        Assert.Equal("function a() {\r\n  return 2;\r\n}\r\n", fake.HmiFiles["A.js"]);
        Assert.Contains("+1/−1 lines", summary);
    }

    [Fact]
    public void WriteFile_TakesTheLineEndingsFromTheFileOnDisk_NotFromTheEngineText()
    {
        // The engine may hand back LF for a CRLF file; the file on disk decides.
        var fake = Session();
        fake.HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1, [new HmiProjectEntry(Prj, _tmp, true, false)], null);
        TempFile(@"Functions\A.js", "a\r\nb\r\n");
        fake.HmiFiles["Functions/A.js"] = "a\nb\n";

        var popup = new FakeHmiConfirmer();
        Assert.StartsWith("Nothing to do", new HmiCoordinator(fake, popup).WriteFile(null, "Functions/A.js", "a\nb\n", null, true, 120).Summary);
        Assert.Empty(popup.Requests);

        new HmiCoordinator(fake, popup).WriteFile(null, "Functions/A.js", "a\nc\n", null, true, 120);
        Assert.Equal("a\r\nc\r\n", fake.HmiFiles["Functions/A.js"]);
    }

    [Fact]
    public void DeleteItem_StaleEntryOnlyOnDisk_IsReported()
    {
        var fake = DeleteSession();
        fake.HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1, [new HmiProjectEntry(Prj, _tmp, true, false)], null);
        TempFile(@"Properties\tchmiconfig.json", "{ \"content\": [ { \"url\": \"Docs/Training/a.html.html\" } ] }");

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).DeleteItem(null, "Docs/Training/a.html.html", 120);

        Assert.Contains("[Warning] Properties/tchmiconfig.json still lists 'Docs/Training/a.html.html'", summary);
    }

    [Fact]
    public void WriteFile_MixedLineEndings_AreNormalisedToTheSource()
    {
        // Same text, but the file mixes CRLF and LF: that is a change, and the source's line endings win.
        var fake = Session();
        fake.HmiFiles["A.js"] = "a\r\nb\nc\r\n";
        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).WriteFile(null, "A.js", "a\r\nb\r\nc\r\n", null, true, 120);
        Assert.StartsWith("Wrote 'A.js'", summary);
        Assert.Equal("a\r\nb\r\nc\r\n", fake.HmiFiles["A.js"]);
    }

    [Theory]
    [InlineData("a\r\nb", "x\ny\n", "x\r\ny\r\n")]
    [InlineData("a\nb", "x\r\ny\r\n", "x\ny\n")]
    [InlineData("a\r\nb\nc", "x\ny\r\n", "x\ny\r\n")]   // mixed: the replacement as it is
    [InlineData("one line", "x\ny\r\n", "x\ny\r\n")]    // no line break: the replacement as it is
    public void Text_MatchLineEndings(string current, string replacement, string expected) =>
        Assert.Equal(expected, HmiText.MatchLineEndings(current, replacement));

    [Fact]
    public void WriteFile_Json_KeepsTheProjectSchema()
    {
        var fake = Session();
        fake.AddHmiItem(Prj, "UserControls/Cam", "Cam", "UserControl");
        fake.CallLog.Clear();

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).WriteFile(null, "UserControls/Cam/Cam.usercontrol.json",
            "{\n  \"$schema\": \"(placeholder)\",\n  \"parameters\": [ { \"name\": \"data-tchmi-curve\" } ]\n}", null, true, 120);

        Assert.StartsWith("Wrote 'UserControls/Cam/Cam.usercontrol.json'", summary);
        string written = fake.HmiFiles["UserControls/Cam/Cam.usercontrol.json"];
        Assert.Contains(FakeTwinCatSession.FakeProjectSchema, written);
        Assert.Contains("data-tchmi-curve", written);
    }

    [Fact]
    public void WriteFile_KeepSchemaOff_WritesVerbatim()
    {
        var fake = Session();
        fake.AddHmiItem(Prj, "UserControls/Cam", "Cam", "UserControl");
        new HmiCoordinator(fake, new FakeHmiConfirmer()).WriteFile(null, "UserControls/Cam/Cam.usercontrol.json",
            "{ \"$schema\": \"mine\" }", null, keepSchema: false, 120);
        Assert.Equal("{ \"$schema\": \"mine\" }", fake.HmiFiles["UserControls/Cam/Cam.usercontrol.json"]);
    }

    [Fact]
    public void WriteFile_Rejected_NothingWritten()
    {
        var fake = Session();
        fake.HmiFiles["A.view"] = "old";
        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer(HmiConfirmOutcome.TimedOut))
            .WriteFile(null, "A.view", "new", null, true, 30);
        Assert.Contains("within 30 s", summary);
        Assert.Equal("old", fake.HmiFiles["A.view"]);
    }

    [Fact]
    public void WriteFile_EngineAltersText_ReadBackFails()
    {
        var fake = Session();
        fake.HmiFiles["A.view"] = "old";
        fake.HmiFileWriteTransform = s => s + "\n<!-- reformatted -->";

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).WriteFile(null, "A.view", "new", null, true, 120);

        Assert.StartsWith("[Error] 'A.view' was written, but the read-back differs from line 2", summary);
    }

    [Fact]
    public void WriteFile_FromSourceFile_AndPopupShowsSizes()
    {
        var fake = Session();
        fake.HmiFiles["Functions/F.js"] = "a\nb";
        var popup = new FakeHmiConfirmer();
        string file = TempFile("F.js", "a\nB\nc");

        var (summary, _) = new HmiCoordinator(fake, popup).WriteFile(null, "Functions/F.js", null, file, true, 120);

        Assert.StartsWith("Wrote 'Functions/F.js'", summary);
        Assert.Equal("a\nB\nc", fake.HmiFiles["Functions/F.js"]);
        Assert.Equal("5 characters (+2 / −1 lines)", Assert.Single(popup.Requests).Request.NewValue);
    }

    [Theory]
    [InlineData("Properties/tchmiconfig.json")]
    [InlineData("properties/TcHmiConfig.json")]
    public void WriteFile_TcHmiConfig_IsRefusedWithoutPopup(string path)
    {
        var popup = new FakeHmiConfirmer();
        var (summary, _) = new HmiCoordinator(Session(), popup).WriteFile(null, path, "{}", null, false, 120);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("in-memory model", summary, StringComparison.Ordinal);
        Assert.Contains("twincat_hmi_add_internal_symbol", summary, StringComparison.Ordinal);
        Assert.Empty(popup.Requests);
    }

    // ── import_file / build ──────────────────────────────────────────────────

    [Fact]
    public void ImportFile_RelativeOrMissingSource_Refused()
    {
        var c = new HmiCoordinator(Session(), new FakeHmiConfirmer());
        Assert.StartsWith("[Error] sourcePath must be an absolute path", c.ImportFile(null, "doc.html", "Docs", null, 120).Summary);
        Assert.StartsWith("[Error] sourcePath not found", c.ImportFile(null, Path.Combine(_tmp, "nope.html"), "Docs", null, 120).Summary);
    }

    [Fact]
    public void ImportFile_Confirmed_CopiesIntoFolder()
    {
        var fake = Session();
        string html = TempFile("machine-docs.html", "<!doctype html><title>Plant</title>");
        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).ImportFile(null, html, "Docs/Training", null, 120);
        Assert.Contains("as 'Docs/Training/machine-docs.html'", summary);
        Assert.Equal("<!doctype html><title>Plant</title>", fake.HmiFiles["Docs/Training/machine-docs.html"]);
    }

    [Fact]
    public void ImportFile_TargetExists_RefusedWithoutPopup()
    {
        var fake = Session();
        fake.HmiFiles["Docs/a.html"] = "x";
        var popup = new FakeHmiConfirmer();
        var (summary, _) = new HmiCoordinator(fake, popup).ImportFile(null, TempFile("a.html", "y"), "Docs", null, 120);
        Assert.Contains("already exists", summary);
        Assert.Empty(popup.Requests);
    }

    // #247 — live 2026-10-10: TE2000 adds a .js, then raises "Der aufrufende Thread kann nicht auf dieses
    // Objekt zugreifen …". The session returns the item with that message; the coordinator checks the copy.
    private const string ThreadError = "AddExistingItem('lib.js') in 'Lib' failed: Der aufrufende Thread kann nicht auf dieses Objekt zugreifen, da sich das Objekt im Besitz eines anderen Threads befindet.";

    private FakeTwinCatSession SessionWithProjectDir(out string projectDir)
    {
        projectDir = Path.Combine(_tmp, "prj");
        Directory.CreateDirectory(projectDir);
        var f = Session();
        f.HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1, [new HmiProjectEntry(Prj, projectDir, true, false)], null);
        return f;
    }

    [Fact]
    public void ImportFile_EngineErrorButIdenticalCopy_IsSuccessWithTheEngineMessage()
    {
        var fake = SessionWithProjectDir(out _);
        fake.HmiImportEngineError = ThreadError;

        var (summary, json) = new HmiCoordinator(fake, new FakeHmiConfirmer()) { ReadBackDelays = [TimeSpan.Zero] }
            .ImportFile(null, TempFile("src/lib.js", "var lib = 1;"), "Lib", null, 120);

        Assert.StartsWith("Imported", summary);
        Assert.Contains("as 'Lib/lib.js'", summary);
        Assert.Contains("verified: in the project and byte-identical on disk", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(ThreadError, doc.RootElement.GetProperty("engineError").GetString());
        Assert.True(doc.RootElement.GetProperty("verifiedOnDisk").GetBoolean());
    }

    [Fact]
    public void ImportFile_EngineErrorAndDifferentCopy_IsAnError()
    {
        var fake = SessionWithProjectDir(out _);
        fake.HmiImportEngineError = ThreadError;
        fake.HmiImportDiskContent = "truncated";

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).ImportFile(null, TempFile("src/lib.js", "var lib = 1;"), "Lib", null, 120);

        Assert.StartsWith("[Error] 'Lib/lib.js' is in the project, but the file on disk differs from the source", summary);
        Assert.Contains(ThreadError, summary);
    }

    [Fact]
    public void ImportFile_EngineErrorAndNoFileOnDisk_IsAnError()
    {
        var fake = Session();   // project directory does not exist → nothing to compare
        fake.HmiImportEngineError = ThreadError;

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).ImportFile(null, TempFile("lib.js", "x"), "Lib", null, 120);

        Assert.StartsWith("[Error] 'Lib/lib.js' is in the project, but its file was not found on disk", summary);
    }

    [Fact]
    public void ImportFile_GenuineFailure_StaysAnError()
    {
        var fake = Session();
        fake.HmiImportFails = ThreadError;

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).ImportFile(null, TempFile("lib.js", "x"), "Lib", null, 120);

        Assert.StartsWith("[Error] AddExistingItem('lib.js') in 'Lib' failed", summary);
    }

    [Theory]
    [InlineData("JavaScript", "Listed in dependencyFiles as JavaScript; run twincat_hmi_build")]
    [InlineData(null, "Not in dependencyFiles of Properties/tchmiconfig.json")]
    public void ImportFile_Js_ReportsWhatTchmiconfigListsOnDisk(string? type, string expected)
    {
        var fake = SessionWithProjectDir(out string dir);
        string entry = type is null ? "" : $"{{ \"name\": \"Lib/lib.js\", \"description\": \"\", \"type\": \"{type}\" }}";
        Directory.CreateDirectory(Path.Combine(dir, "Properties"));
        File.WriteAllText(Path.Combine(dir, "Properties", "tchmiconfig.json"),
            $"{{ \"dependencyFiles\": [ {{ \"name\": \"Fonts/Fonts.css\", \"type\": \"Stylesheet\" }}{(type is null ? "" : ", " + entry)} ] }}");

        var (summary, json) = new HmiCoordinator(fake, new FakeHmiConfirmer()) { ReadBackDelays = [TimeSpan.Zero] }
            .ImportFile(null, TempFile("src/lib.js", "1"), "Lib", null, 120);

        Assert.Contains(expected, summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(type, doc.RootElement.GetProperty("dependencyType").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("engineError").ValueKind);
    }

    [Fact]
    public void ImportFile_Html_SaysNothingAboutDependencyFiles()
    {
        var (summary, _) = new HmiCoordinator(Session(), new FakeHmiConfirmer()).ImportFile(null, TempFile("p.html", "<p/>"), "Docs", null, 120);
        Assert.DoesNotContain("dependencyFiles", summary);
    }

    [Fact]
    public void Build_ReportsFailureWithTheErrorListHint()
    {
        var fake = Session();
        fake.HmiBuildResult = new HmiOperationResult(false, "TS2304: Cannot find name 'x'");
        var (summary, _) = new HmiCoordinator(fake).Build(null, null);
        Assert.StartsWith("[Error] Build of 'PlantHmi' (Debug) failed: TS2304", summary);
        Assert.Contains("twincat_get_error_list", summary);
    }

    // ── file open in an XAE editor tab (the XAE would block on "reload?") ────

    [Fact]
    public void WriteFile_FileOpenInEditor_RefusedBeforeThePopup()
    {
        var fake = Session();
        fake.HmiFiles["Functions/F.js"] = "old";
        fake.HmiOpenFiles.Add("Functions/F.js");
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).WriteFile(null, "Functions/F.js", "new", null, true, 120);

        Assert.StartsWith("[Error] 'Functions/F.js' is open in an editor tab of the XAE", summary);
        Assert.Empty(popup.Requests);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Bundle_ExistingFileOpenInEditor_RefusedBeforeThePopup()
    {
        var fake = Session();
        string manifest = CamBundle();
        fake.HmiFiles["Functions/PtcCamCurve.js"] = "old";
        fake.HmiFiles["Functions/PtcCamCurve.function.json"] = "{}";
        fake.HmiOpenFiles.Add("Functions/PtcCamCurve.js");
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).ApplyBundle(null, manifest, 120);

        Assert.StartsWith("[Error] 'Functions/PtcCamCurve.js' is open in an editor tab", summary);
        Assert.Contains("Nothing was written", summary);
        Assert.Empty(popup.Requests);
    }

    // ── orphan files on disk (the XAE would block on "overwrite?") ───────────

    private FakeTwinCatSession SessionOnDisk(string orphan)
    {
        string dir = Path.Combine(_tmp, "prj");
        TempFile(Path.Combine("prj", orphan.Replace('/', Path.DirectorySeparatorChar)), "left over");
        var f = Session();
        f.HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1, [new HmiProjectEntry(Prj, dir, true, false)], null);
        return f;
    }

    [Fact]
    public void AddItem_OrphanFileOnDisk_RefusedBeforeThePopup()
    {
        var fake = SessionOnDisk("Docs/Hilfe.html");
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).AddItem(null, "Docs", "Hilfe", "html", 120);

        Assert.StartsWith("[Error] 'Docs/Hilfe.html' exists on disk but not in the project", summary);
        Assert.Contains("would ask whether to overwrite it", summary);
        Assert.Empty(popup.Requests);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void AddItem_OrphanDependentOnDisk_RefusedBeforeThePopup()
    {
        var fake = SessionOnDisk("UserControls/Cam/Cam.usercontrol.json");
        var popup = new FakeHmiConfirmer();
        var (summary, _) = new HmiCoordinator(fake, popup).AddItem(null, "UserControls/Cam", "Cam", "usercontrol", 120);
        Assert.StartsWith("[Error] 'UserControls/Cam/Cam.usercontrol.json' exists on disk", summary);
        Assert.Empty(popup.Requests);
    }

    [Fact]
    public void ImportFile_OrphanTargetOnDisk_RefusedBeforeThePopup()
    {
        var fake = SessionOnDisk("Docs/doc.html");
        string src = TempFile("src/doc.html", "<p/>");
        var popup = new FakeHmiConfirmer();
        var (summary, _) = new HmiCoordinator(fake, popup).ImportFile(null, src, "Docs", null, 120);
        Assert.StartsWith("[Error] 'Docs/doc.html' exists on disk but not in the project", summary);
        Assert.Empty(popup.Requests);
    }

    [Fact]
    public void Bundle_OrphanOnDisk_RefusedBeforeThePopup()
    {
        var fake = SessionOnDisk("Functions/PtcCamCurve.function.json");
        var popup = new FakeHmiConfirmer();
        var (summary, _) = new HmiCoordinator(fake, popup).ApplyBundle(null, CamBundle(), 120);
        Assert.StartsWith("[Error] 'Functions/PtcCamCurve.function.json' exists on disk", summary);
        Assert.Contains("Nothing was written", summary);
        Assert.Empty(popup.Requests);
        Assert.False(Wrote(fake));
    }

    // ── delete_item ──────────────────────────────────────────────────────────

    private static FakeTwinCatSession DeleteSession()
    {
        var f = Session();
        f.HmiProjectInfoByName[Prj] = new HmiProjectInfo(Prj, @"C:\Temp\x\" + Prj, true, false,
            new Dictionary<string, string?> { ["StartupView"] = "Views/Desktop.view" }, [], null, false);
        f.HmiFiles["Views/Desktop.view"] = "<div/>";
        f.HmiFiles["Views/Other.view"] = "<div/>";
        f.HmiFiles["Docs/Training/a.html"] = "<p/>";
        f.HmiFiles["Docs/Training/a.html.html"] = "";
        f.HmiFiles["Docs/Training/Bilder/b.txt"] = "";
        return f;
    }

    [Theory]
    [InlineData("")]
    [InlineData("../Docs")]
    [InlineData(@"C:\Temp\x.html")]
    public void DeleteItem_InvalidPath_NoCallsNoPopup(string path)
    {
        var fake = DeleteSession();
        var popup = new FakeHmiConfirmer();
        Assert.StartsWith("[Error] Invalid project path", new HmiCoordinator(fake, popup).DeleteItem(null, path, 120).Summary);
        Assert.Empty(fake.CallLog);
        Assert.Empty(popup.Requests);
    }

    [Theory]
    [InlineData("Server/TcHmiSrv/TcHmiSrv.Config.default.json", "project infrastructure ('Server')")]
    [InlineData("properties", "project infrastructure ('properties')")]
    [InlineData("Packages", "project infrastructure ('Packages')")]
    [InlineData("Views/Desktop.view", "the project's startup view")]
    [InlineData("views", "a folder containing the startup view 'Views/Desktop.view'")]
    public void DeleteItem_ProtectedItems_RefusedWithoutPopup(string path, string expected)
    {
        var fake = DeleteSession();
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).DeleteItem(null, path, 120);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains(expected, summary);
        Assert.Contains("Nothing was deleted", summary);
        Assert.Empty(popup.Requests);
        Assert.False(Wrote(fake));
        Assert.True(fake.HmiFiles.ContainsKey("Views/Desktop.view"));
    }

    [Fact]
    public void DeleteItem_Missing_ReportedWithoutPopup()
    {
        var fake = DeleteSession();
        var popup = new FakeHmiConfirmer();
        var (summary, _) = new HmiCoordinator(fake, popup).DeleteItem(null, "Docs/nope.html", 120);
        Assert.Equal($"[Error] 'Docs/nope.html' not found in '{Prj}'. Nothing was deleted.", summary);
        Assert.Empty(popup.Requests);
    }

    [Fact]
    public void DeleteItem_Rejected_NothingDeleted()
    {
        var fake = DeleteSession();
        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer(HmiConfirmOutcome.Rejected))
            .DeleteItem(null, "Docs/Training/a.html.html", 120);
        Assert.Contains("Nothing was written", summary);
        Assert.False(Wrote(fake));
        Assert.True(fake.HmiFiles.ContainsKey("Docs/Training/a.html.html"));
    }

    [Fact]
    public void DeleteItem_File_Confirmed_DeletedAndVerified()
    {
        var fake = DeleteSession();
        var popup = new FakeHmiConfirmer();

        var (summary, json) = new HmiCoordinator(fake, popup).DeleteItem(null, @"Docs\Training\a.html.html", 120);

        Assert.StartsWith("Deleted 'Docs/Training/a.html.html' (file)", summary);
        Assert.Contains("verified: no longer in the project", summary);
        Assert.False(fake.HmiFiles.ContainsKey("Docs/Training/a.html.html"));
        Assert.True(fake.HmiFiles.ContainsKey("Docs/Training/a.html"));
        var request = Assert.Single(popup.Requests).Request;
        Assert.Equal(HmiWriteKind.DeleteItem, request.Kind);
        Assert.Equal("file", request.CurrentValue);
        Assert.Contains("from disk", request.NewValue);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("isFile").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("staleConfigEntries").GetArrayLength());
        Assert.DoesNotContain("[Warning]", summary);
    }

    [Fact]
    public void DeleteItem_StaleContentEntryInTheConfig_IsReported()
    {
        // TE2000 keeps a deleted page's content entry in tchmiconfig.json (its in-memory model); the result says so.
        var fake = DeleteSession();
        fake.HmiFiles["Properties/tchmiconfig.json"] =
            "{ \"content\": [ { \"url\": \"Docs/Training/a.html.html\", \"preload\": false }, { \"url\": \"Docs/Other.content\" } ] }";

        var (summary, json) = new HmiCoordinator(fake, new FakeHmiConfirmer()).DeleteItem(null, "Docs/Training", 120);

        Assert.StartsWith("Deleted 'Docs/Training'", summary);
        Assert.Contains("[Warning] Properties/tchmiconfig.json still lists 'Docs/Training/a.html.html'", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["Docs/Training/a.html.html"],
            doc.RootElement.GetProperty("staleConfigEntries").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData("{ \"content\": [ { \"url\": \"Pages/A.content\" } ] }", "Pages/A.content", "Pages/A.content")]
    [InlineData("{ \"content\": [ { \"url\": \"Pages/AB.content\" } ] }", "Pages/A", "")]              // a sibling, not below
    [InlineData("{ \"x\": { \"url\": \"pages/sub/b.view\" } }", "Pages/Sub", "pages/sub/b.view")]
    [InlineData("not json", "Pages/A.content", "")]
    public void Text_UrlsPointingAt(string json, string path, string expected) =>
        Assert.Equal(expected, string.Join(",", HmiText.UrlsPointingAt(json, path)));

    [Fact]
    public void DeleteItem_Folder_PopupShowsTheCount_AndEverythingBelowIsGone()
    {
        var fake = DeleteSession();
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).DeleteItem(null, "Docs/Training", 120);

        Assert.StartsWith("Deleted 'Docs/Training' (folder with 3 item(s))", summary);
        Assert.Equal("folder with 3 entries", Assert.Single(popup.Requests).Request.CurrentValue);
        Assert.DoesNotContain(fake.HmiFiles.Keys, k => k.StartsWith("Docs/Training", StringComparison.Ordinal));
        Assert.True(fake.HmiFiles.ContainsKey("Views/Other.view"));
    }

    [Fact]
    public void DeleteItem_EngineReportsSuccessButKeepsTheItem_NotTreatedAsSuccess()
    {
        var fake = DeleteSession();
        fake.HmiDeleteKeepsItem = true;
        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).DeleteItem(null, "Views/Other.view", 120);
        Assert.StartsWith("[Error] DeleteChild('Views/Other.view') reported success, but the item still exists", summary);
    }

    [Fact]
    public void DeleteItem_EngineRefuses_ReportsItsReason()
    {
        var fake = DeleteSession();
        fake.HmiDeleteFails.Add("Views/Other.view");
        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).DeleteItem(null, "Views/Other.view", 120);
        Assert.Equal("[Error] DeleteChild('Views/Other.view') was refused: fake refusal.", summary);
    }

    [Fact]
    public void DeleteItem_PopupHeadlineSaysDelete()
    {
        var request = new HmiWriteRequest(HmiWriteKind.DeleteItem, Prj, @"C:\Temp", "Docs/a.html", null, "(deleted)", "file", false);
        Assert.Contains("DELETE", HmiConfirmText.Headline(request));
    }

    // ── apply_bundle ─────────────────────────────────────────────────────────

    private string CamBundle(bool withKinds = true)
    {
        TempFile("bundle/PtcCamCurve.usercontrol", "<div id=\"PtcCamCurve\"></div>");
        TempFile("bundle/PtcCamCurve.usercontrol.json", "{\n  \"$schema\": \"(placeholder)\",\n  \"parameters\": []\n}");
        TempFile("bundle/PtcCamCurve.js", "(function(){})();");
        TempFile("bundle/PtcCamCurve.function.json", "{\n  \"$schema\": \"(placeholder)\",\n  \"function\": {}\n}");
        string kindUc = withKinds ? ", \"kind\": \"usercontrol\"" : "";
        string kindFn = withKinds ? ", \"kind\": \"function_js\"" : "";
        return TempFile("bundle/manifest.json", "{ \"name\": \"PtcCamCurve\", \"entries\": [" +
            "{ \"path\": \"UserControls/PtcCamCurve/PtcCamCurve.usercontrol\"" + kindUc + ", \"source\": \"PtcCamCurve.usercontrol\" }," +
            "{ \"path\": \"UserControls/PtcCamCurve/PtcCamCurve.usercontrol.json\", \"source\": \"PtcCamCurve.usercontrol.json\" }," +
            "{ \"path\": \"Functions/PtcCamCurve.js\"" + kindFn + ", \"source\": \"PtcCamCurve.js\" }," +
            "{ \"path\": \"Functions/PtcCamCurve.function.json\", \"source\": \"PtcCamCurve.function.json\" } ] }");
    }

    [Fact]
    public void Bundle_CreatesWritesAndKeepsSchemas_WithOnePopup()
    {
        var fake = Session();
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).ApplyBundle(null, CamBundle(), 120);

        Assert.StartsWith("Applied bundle 'PtcCamCurve' to 'PlantHmi': 4 file(s)", summary);
        var request = Assert.Single(popup.Requests).Request;
        Assert.Equal(HmiWriteKind.ApplyBundle, request.Kind);
        Assert.Contains("create (usercontrol) + write", request.NewValue);
        Assert.Contains("write (created by template)", request.NewValue);
        Assert.Equal("<div id=\"PtcCamCurve\"></div>", fake.HmiFiles["UserControls/PtcCamCurve/PtcCamCurve.usercontrol"]);
        Assert.Contains(FakeTwinCatSession.FakeProjectSchema, fake.HmiFiles["UserControls/PtcCamCurve/PtcCamCurve.usercontrol.json"]);
        Assert.Contains("../.hmiframework/Schema/FunctionDescription.Schema.json", fake.HmiFiles["Functions/PtcCamCurve.function.json"]);
        Assert.Equal("(function(){})();", fake.HmiFiles["Functions/PtcCamCurve.js"]);
    }

    [Fact]
    public void Bundle_MissingFileWithoutKind_RefusedBeforePopup()
    {
        var fake = Session();
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).ApplyBundle(null, CamBundle(withKinds: false), 120);

        Assert.Contains("does not exist and has no kind", summary);
        Assert.Empty(popup.Requests);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Bundle_Rejected_NothingWritten()
    {
        var fake = Session();
        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer(HmiConfirmOutcome.Rejected)).ApplyBundle(null, CamBundle(), 120);
        Assert.Contains("Nothing was written", summary);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Bundle_StopsAtFirstError_AndListsWhatWasWritten()
    {
        var fake = Session();
        fake.HmiAddItemFails.Add("Functions/PtcCamCurve.js");

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).ApplyBundle(null, CamBundle(), 120);

        Assert.StartsWith("[Error] creating 'Functions/PtcCamCurve.js' failed", summary);
        Assert.Contains("Written and verified before the stop: UserControls/PtcCamCurve/PtcCamCurve.usercontrol, " +
                        "UserControls/PtcCamCurve/PtcCamCurve.usercontrol.json.", summary);
        Assert.DoesNotContain("final content not verified", summary);   // the template's .json was written afterwards
    }

    [Fact]
    public void Bundle_StopAfterCreate_NamesTheFilesLeftWithTemplateContent()
    {
        var fake = Session();
        fake.HmiFileWriteResult = new HmiOperationResult(false, "refused");

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).ApplyBundle(null, CamBundle(), 120);

        Assert.StartsWith("[Error] SetSource('UserControls/PtcCamCurve/PtcCamCurve.usercontrol') was refused", summary);
        Assert.Contains("Nothing of the bundle was written.", summary);
        Assert.Contains("Created, final content not verified: UserControls/PtcCamCurve/PtcCamCurve.usercontrol, " +
                        "UserControls/PtcCamCurve/PtcCamCurve.usercontrol.json.", summary);
    }

    [Fact]
    public void Bundle_HtmlEntry_IsImportedUnderItsOwnName_NotCreatedFromTheTemplate()
    {
        // Live (TE2000 14.17.2): AddItem(GeneralBlankHtml) names the file "x.html.html".
        TempFile("doc/page.html", "<!doctype html><title>Training</title>");
        string manifest = TempFile("doc/manifest.json",
            "{ \"name\": \"Doc\", \"entries\": [ { \"path\": \"Docs/Training/page.html\", \"kind\": \"html\", \"source\": \"page.html\" } ] }");
        var fake = Session();

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).ApplyBundle(null, manifest, 120);

        Assert.StartsWith("Applied bundle 'Doc' to 'PlantHmi': 1 file(s)", summary);
        Assert.Contains($"ImportHmiFile({Prj}, Docs/Training, page.html)", fake.CallLog);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("AddHmiItem", StringComparison.Ordinal));
        Assert.Equal("<!doctype html><title>Training</title>", fake.HmiFiles["Docs/Training/page.html"]);
        Assert.False(fake.HmiFiles.ContainsKey("Docs/Training/page.html.html"));
    }

    [Theory]
    [InlineData("{ \"entries\": [] }", "non-empty \"entries\"")]
    [InlineData("{ \"entries\": [ { \"path\": \"../x.view\", \"source\": \"a\" } ] }", "invalid or missing \"path\"")]
    [InlineData("{ \"entries\": [ { \"path\": \"x.view\" } ] }", "missing \"source\"")]
    [InlineData("{ \"entries\": [ { \"path\": \"x.view\", \"source\": \"nope.view\" } ] }", "source not found")]
    [InlineData("{ \"entries\": [ { \"path\": \"x.view\", \"source\": \"a.view\", \"kind\": \"page\" } ] }", "kind must be one of")]
    [InlineData("{ \"entries\": [ { \"path\": \"x.view\", \"source\": \"a.view\" }, { \"path\": \"X.view\", \"source\": \"a.view\" } ] }", "listed twice")]
    [InlineData("not json", "not valid JSON")]
    public void Bundle_InvalidManifest_RefusedBeforeAnyCall(string manifest, string expected)
    {
        TempFile("m/a.view", "<div/>");
        string path = TempFile("m/manifest.json", manifest);
        var fake = Session();

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).ApplyBundle(null, path, 120);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains(expected, summary);
        Assert.Empty(fake.CallLog);
    }
}
