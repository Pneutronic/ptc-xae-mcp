using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>hmi-controls: the control catalog from the packages' Description.json files and placing any control,
/// into the root or a container. Description shapes as in framework 14.6.3 / controls 14.8.3.</summary>
public sealed class HmiControlsTests : IDisposable
{
    private const string Prj = "PlantHmi";
    private readonly string _sln = Directory.CreateTempSubdirectory("hmi-controls-tests").FullName;
    private string ProjectDir => Path.Combine(_sln, Prj);

    public void Dispose()
    {
        try { Directory.Delete(_sln, recursive: true); } catch (IOException) { }
    }

    private const string ControlBase = """
        { "name": "TcHmiControl", "namespace": "TcHmi.Controls.System", "visible": false,
          "properties": { "containerControl": false, "geometry": { "width": 0, "height": 0 } },
          "attributes": [
            { "name": "id", "propertyName": "Id", "readOnly": false, "bindable": false },
            { "name": "data-tchmi-left", "propertyName": "Left", "bindable": true },
            { "name": "data-tchmi-is-enabled", "propertyName": "IsEnabled", "displayName": "Is Enabled", "bindable": true },
            { "name": "data-tchmi-rendered-width", "propertyName": "RenderedWidth", "readOnly": true, "bindable": true },
            { "name": "data-tchmi-width-mode", "propertyName": "WidthMode", "bindable": true, "category": "Layout" } ] }
        """;

    private const string ContainerControl = """
        { "name": "TcHmiContainerControl", "namespace": "TcHmi.Controls.System", "base": "TcHmi.Controls.System.TcHmiControl", "visible": false,
          "properties": { "containerControl": true }, "attributes": [] }
        """;

    private const string Container = """
        { "name": "TcHmiContainer", "namespace": "TcHmi.Controls.System", "base": "TcHmi.Controls.System.TcHmiContainerControl",
          "properties": { "containerControl": true, "geometry": { "width": 200, "height": 200 } }, "attributes": [] }
        """;

    private const string Region = """
        { "name": "TcHmiRegion", "namespace": "TcHmi.Controls.System", "base": "TcHmi.Controls.System.TcHmiControl",
          "properties": { "containerControl": false, "geometry": { "width": 500, "height": 500 } },
          "attributes": [ { "name": "data-tchmi-target-content", "propertyName": "TargetContent", "bindable": true } ] }
        """;

    private const string Button = """
        { "name": "TcHmiButton", "namespace": "TcHmi.Controls.Beckhoff", "displayName": "Button", "base": "TcHmi.Controls.System.TcHmiControl",
          "properties": { "containerControl": false, "geometry": { "width": 100, "height": 50 } },
          "attributes": [
            { "name": "data-tchmi-text", "propertyName": "Text", "displayName": "Text", "bindable": true, "type": "tchmi:general#/definitions/String" },
            { "name": "data-tchmi-state-symbol", "propertyName": "StateSymbol", "bindable": false },
            { "name": "data-tchmi-width-mode", "propertyName": "WidthMode", "bindable": true, "category": "Layout" },
            { "name": "data-tchmi-icon", "propertyName": "Icon", "bindable": true, "visible": false } ] }
        """;

    private const string OtherButton = """
        { "name": "TcHmiButton", "namespace": "Acme.Controls", "base": "TcHmi.Controls.System.TcHmiControl", "attributes": [] }
        """;

    private static HmiControlCatalog Catalog(params string[] extra)
    {
        var types = new[] { ControlBase, ContainerControl, Container, Region, Button }.Concat(extra)
            .Select(j => HmiControlCatalog.Parse(j, "test 1.0")!).ToList();
        return new HmiControlCatalog(types, ["test 1.0"], []);
    }

    private static Dictionary<string, JsonElement> Values(string json) => HmiPlacement.ParseParameters(json).Values!;

    private static HmiLayout Layout(int? w = null, int? h = null) => new(10, 20, w, h, "px", "px");

    private const string Page = """
        <div id="Desktop" data-tchmi-type="TcHmi.Controls.System.TcHmiView">
          <div id="Box" data-tchmi-type="TcHmi.Controls.System.TcHmiContainer" data-tchmi-left="0" data-tchmi-top="0">
            <div id="Inner" data-tchmi-type="TcHmi.Controls.Beckhoff.TcHmiButton"></div>
          </div>
          <div id="Ok" data-tchmi-type="TcHmi.Controls.Beckhoff.TcHmiButton"></div>
          <div id="Odd" data-tchmi-type="Vendor.Unknown"></div>
        </div>
        """;

    // ── catalog ───────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsControlsAndSkipsOtherDescriptions()
    {
        var b = HmiControlCatalog.Parse(Button, "Beckhoff.TwinCAT.HMI.Controls 14.8.3")!;
        Assert.Equal(("TcHmi.Controls.Beckhoff.TcHmiButton", "TcHmi.Controls.System.TcHmiControl", false, 100, 50),
            (b.FullName, b.Base, b.Container, b.DesignWidth!.Value, b.DesignHeight!.Value));
        Assert.False(b.OwnAttributes.Single(a => a.PropertyName == "StateSymbol").Bindable);
        Assert.Null(HmiControlCatalog.Parse("{ \"$schema\": \"x\", \"version\": \"14.6.3\", \"events\": [] }", "fw"));
    }

    [Fact]
    public void Find_ByFullOrUniqueShortName_AmbiguousAndUnknownAreExplained()
    {
        var c = Catalog();
        Assert.Equal("TcHmi.Controls.Beckhoff.TcHmiButton", c.Find("tchmibutton").Type!.FullName);
        Assert.Equal("TcHmi.Controls.System.TcHmiRegion", c.Find("TcHmi.Controls.System.TcHmiRegion").Type!.FullName);
        Assert.Contains("did you mean TcHmiButton", c.Find("Butto").Error, StringComparison.Ordinal);
        Assert.Contains("ambiguous", Catalog(OtherButton).Find("TcHmiButton").Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AllAttributes_FollowTheBaseChain_TheControlShadowsItsBase()
    {
        var c = Catalog();
        var all = c.AllAttributes(c.Find("TcHmiButton").Type!);
        Assert.Contains(all, a => a.Name == "data-tchmi-left" && a.DeclaredIn == "TcHmi.Controls.System.TcHmiControl");
        Assert.Equal("TcHmi.Controls.Beckhoff.TcHmiButton", Assert.Single(all, a => a.Name == "data-tchmi-width-mode").DeclaredIn);
        Assert.Equal("data-tchmi-text", HmiControlCatalog.Attribute(all, "text")!.Name);              // short attribute name
        Assert.Equal("data-tchmi-is-enabled", HmiControlCatalog.Attribute(all, "Is Enabled")!.Name);  // displayName
        Assert.True(c.Find("TcHmiContainer").Type!.Container);
    }

    [Fact]
    public void Load_ReadsTheRestoredPackagesOfPackagesConfig()
    {
        Directory.CreateDirectory(ProjectDir);
        File.WriteAllText(Path.Combine(ProjectDir, "packages.config"),
            "<packages><package id=\"Beckhoff.TwinCAT.HMI.Controls\" version=\"14.8.3\" /><package id=\"Missing.Pkg\" version=\"1.0.0\" /></packages>");
        string dir = Path.Combine(_sln, "Packages", "Beckhoff.TwinCAT.HMI.Controls.14.8.3", "runtimes", "native1.12-tchmi", "TcHmiButton");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Description.json"), Button);

        var c = HmiControlCatalog.Load(ProjectDir);
        Assert.Equal("TcHmi.Controls.Beckhoff.TcHmiButton", Assert.Single(c.Types).FullName);
        Assert.Equal(["Beckhoff.TwinCAT.HMI.Controls 14.8.3 (1 control(s))"], c.Sources);
        Assert.Contains(c.Problems, p => p.Contains("Missing.Pkg 1.0.0 is not restored", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_FindsPackagesInTheProjectFolder_WhenTheSlnLiesThere()
    {
        // .sln next to the .hmiproj: TE2000 restores to <projectDir>\Packages\<id>.<version>\.
        Directory.CreateDirectory(ProjectDir);
        File.WriteAllText(Path.Combine(ProjectDir, "packages.config"),
            "<packages><package id=\"Beckhoff.TwinCAT.HMI.Controls\" version=\"14.8.3\" /></packages>");
        string dir = Path.Combine(ProjectDir, "Packages", "Beckhoff.TwinCAT.HMI.Controls.14.8.3", "runtimes", "native1.12-tchmi", "TcHmiButton");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Description.json"), Button);

        var c = HmiControlCatalog.Load(ProjectDir);
        Assert.Equal("TcHmi.Controls.Beckhoff.TcHmiButton", Assert.Single(c.Types).FullName);
        Assert.Empty(c.Problems);
    }

    // ── placement ─────────────────────────────────────────────────────────────

    [Fact]
    public void PlaceControl_AppendsToTheRootWithDesignSizeAndCheckedAttributes()
    {
        var c = Catalog();
        string page = Page.ReplaceLineEndings("\n");
        var (r, e) = HmiPlacement.PlaceControl(page, c, c.Find("TcHmiButton").Type!, "Start", Layout(),
            Values("{\"Text\": \"Start & go\", \"IsEnabled\": \"%s%PLC1.MAIN.bReady%/s%\", \"WidthMode\": \"Content\"}"), null, false);
        Assert.Null(e);
        Assert.Equal("<div id=\"Start\" data-tchmi-type=\"TcHmi.Controls.Beckhoff.TcHmiButton\" data-tchmi-left=\"10\" data-tchmi-left-unit=\"px\" " +
                     "data-tchmi-top=\"20\" data-tchmi-top-unit=\"px\" data-tchmi-width=\"100\" data-tchmi-width-unit=\"px\" data-tchmi-height=\"50\" " +
                     "data-tchmi-height-unit=\"px\" data-tchmi-text=\"Start &amp; go\" data-tchmi-is-enabled=\"%s%PLC1.MAIN.bReady%/s%\" " +
                     "data-tchmi-width-mode=\"Content\">\n  </div>", r!.Markup);
        var doc = HmiMarkup.Parse(r.Text);
        Assert.Empty(doc.Problems);
        Assert.Equal("Desktop", doc.Elements.Single(x => x.Id == "Start").Parent!.Id);
        Assert.Equal(7, r.Line);
    }

    [Fact]
    public void PlaceControl_IntoAContainer_AndARegionWithItsTarget()
    {
        var c = Catalog();
        string page = Page.ReplaceLineEndings("\n");
        var (inBox, e) = HmiPlacement.PlaceControl(page, c, c.Find("TcHmiRegion").Type!, "Reg", Layout(300, 200),
            Values("{\"TargetContent\": \"Contents/A.content\"}"), "Box", false);
        Assert.Null(e);
        var doc = HmiMarkup.Parse(inBox!.Text);
        var reg = doc.Elements.Single(x => x.Id == "Reg");
        Assert.Equal("Box", reg.Parent!.Id);
        Assert.Equal("Contents/A.content", reg.Value("data-tchmi-target-content"));
        Assert.Equal("    ", HmiMarkup.IndentAt(inBox.Text, reg.Start));      // like its sibling 'Inner'

        Assert.Contains("takes no child controls", HmiPlacement.PlaceControl(page, c, c.Find("TcHmiButton").Type!, "X", Layout(), Values("{}"), "Ok", false).Error,
            StringComparison.Ordinal);
        Assert.Contains("do not describe", HmiPlacement.PlaceControl(page, c, c.Find("TcHmiButton").Type!, "X", Layout(), Values("{}"), "Odd", false).Error,
            StringComparison.Ordinal);
        Assert.Contains("no control with id 'Nope'", HmiPlacement.PlaceControl(page, c, c.Find("TcHmiButton").Type!, "X", Layout(), Values("{}"), "Nope", false).Error,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"Txt\": \"a\"}", "'Txt' is not an attribute of TcHmi.Controls.Beckhoff.TcHmiButton — did you mean 'Text'?")]
    [InlineData("{\"Enabled\": true}", "did you mean 'IsEnabled'?")]
    [InlineData("{\"Mode\": 1}", "did you mean WidthMode?")]
    [InlineData("{\"RenderedWidth\": 5}", "is read-only")]
    [InlineData("{\"StateSymbol\": \"%s%A%/s%\"}", "is not bindable")]
    [InlineData("{\"Left\": 5}", "set by the left/top/width/height parameters")]
    [InlineData("{\"Text\": \"a\", \"data-tchmi-text\": \"b\"}", "given twice")]
    public void PlaceControl_RefusesWhatTheDescriptionDoesNotAllow(string attributes, string error)
    {
        var c = Catalog();
        var (r, e) = HmiPlacement.PlaceControl(Page.ReplaceLineEndings("\n"), c, c.Find("TcHmiButton").Type!, "B", Layout(), Values(attributes), null, false);
        Assert.Null(r);
        Assert.Contains(error, e, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaceControl_ReplacesOnlyTheSameType_InItsPlace()
    {
        var c = Catalog();
        string page = Page.ReplaceLineEndings("\n");
        var (r, e) = HmiPlacement.PlaceControl(page, c, c.Find("TcHmiButton").Type!, "Inner", Layout(), Values("{\"Text\": \"neu\"}"), null, true);
        Assert.Null(e);
        Assert.True(r!.Replaced);
        Assert.Equal("Box", HmiMarkup.Parse(r.Text).Elements.Single(x => x.Id == "Inner").Parent!.Id);
        Assert.Contains("not a TcHmi.Controls.System.TcHmiRegion",
            HmiPlacement.PlaceControl(page, c, c.Find("TcHmiRegion").Type!, "Ok", Layout(), Values("{}"), null, true).Error, StringComparison.Ordinal);
        Assert.Contains("already exists", HmiPlacement.PlaceControl(page, c, c.Find("TcHmiButton").Type!, "Ok", Layout(), Values("{}"), null, false).Error,
            StringComparison.Ordinal);
        Assert.Contains("replace keeps the place",
            HmiPlacement.PlaceControl(page, c, c.Find("TcHmiButton").Type!, "Inner", Layout(), Values("{}"), "Desktop", true).Error, StringComparison.Ordinal);
    }

    // ── coordinator ───────────────────────────────────────────────────────────

    private FakeTwinCatSession Project()
    {
        void Write(string rel, string text)
        {
            string p = Path.Combine(_sln, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        Write($"{Prj}/tchmiconfig.json", "{ \"startupView\": \"Desktop.view\" }");
        Write($"{Prj}/Desktop.view", Page);
        Write($"{Prj}/packages.config", "<packages><package id=\"Beckhoff.TwinCAT.HMI.Framework\" version=\"14.6.3\" /></packages>");
        string rt = "Packages/Beckhoff.TwinCAT.HMI.Framework.14.6.3/runtimes/native1.12-tchmi/";
        Write(rt + "Controls/System/TcHmiControl/Description.json", ControlBase);
        Write(rt + "Controls/System/TcHmiContainerControl/Description.json", ContainerControl);
        Write(rt + "Controls/System/TcHmiContainer/Description.json", Container);
        Write(rt + "Controls/System/TcHmiRegion/Description.json", Region);
        Write(rt + "TcHmiButton/Description.json", Button);
        var f = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1, [new HmiProjectEntry(Prj, ProjectDir, true, false)], null),
        };
        f.HmiFiles["Desktop.view"] = Page;
        return f;
    }

    [Fact]
    public void Coordinator_DescribeControls_ListsAndDescribes()
    {
        var f = Project();
        var (list, json) = new HmiCoordinator(f).DescribeControls(Prj, null, null, false);
        Assert.StartsWith("3 control type(s) in the packages of 'PlantHmi'", list, StringComparison.Ordinal);   // base classes hidden
        Assert.Contains("1 container(s)", list, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"TcHmi.Controls.Beckhoff.TcHmiButton\"", json, StringComparison.Ordinal);

        var (one, oneJson) = new HmiCoordinator(f).DescribeControls(Prj, "TcHmiButton", null, false);
        Assert.Contains("no children, design size 100×50", one, StringComparison.Ordinal);
        Assert.Contains("Base chain: TcHmi.Controls.Beckhoff.TcHmiButton → TcHmi.Controls.System.TcHmiControl", one, StringComparison.Ordinal);
        Assert.DoesNotContain("data-tchmi-icon", oneJson, StringComparison.Ordinal);                         // hidden attribute

        var (layout, _) = new HmiCoordinator(f).DescribeControls(Prj, "TcHmiButton", "layout", false);
        Assert.Contains("1 attribute(s) matching 'layout'", layout, StringComparison.Ordinal);
        Assert.StartsWith("[Error] 'Butto' is not a control", new HmiCoordinator(f).DescribeControls(Prj, "Butto", null, false).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_PlaceControl_WritesAfterConfirmation_AndNotesAMissingRegionTarget()
    {
        var f = Project();
        var (rejected, _) = new HmiCoordinator(f, new FakeHmiConfirmer(HmiConfirmOutcome.Rejected)).PlaceControl(Prj, "Desktop.view",
            "TcHmiButton", "Go", null, 0, 0, null, null, null, null, "{\"Text\": \"Go\"}", false, 60);
        Assert.StartsWith("[Error]", rejected, StringComparison.Ordinal);
        Assert.DoesNotContain(f.CallLog, c => c.StartsWith("WriteHmiFile", StringComparison.Ordinal));

        var (placed, json) = new HmiCoordinator(f, new FakeHmiConfirmer()).PlaceControl(Prj, "Desktop.view",
            "TcHmiRegion", "Reg", "Box", 0, 0, 300, 200, null, null, "{\"TargetContent\": \"Contents/Missing.content\"}", false, 60);
        Assert.StartsWith("Placed TcHmiRegion 'Reg' in 'Desktop.view' (inside 'Box')", placed, StringComparison.Ordinal);
        Assert.Contains("verified by read-back", placed, StringComparison.Ordinal);
        Assert.Contains("target content 'Contents/Missing.content' does not exist on disk yet", placed, StringComparison.Ordinal);
        Assert.Contains("id=\"Reg\"", f.HmiFiles["Desktop.view"], StringComparison.Ordinal);
        Assert.Contains("\"parentId\": \"Box\"", json, StringComparison.Ordinal);

        Assert.Contains("use twincat_hmi_place_user_control", new HmiCoordinator(f, new FakeHmiConfirmer()).PlaceControl(Prj, "Desktop.view",
            "TcHmi.Controls.System.TcHmiUserControlHost", "H", null, 0, 0, null, null, null, null, null, false, 60).Summary, StringComparison.Ordinal);
    }
}
