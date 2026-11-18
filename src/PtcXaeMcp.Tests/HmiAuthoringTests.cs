using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>hmi-authoring: components, binding check, localization, placing user controls, scaffolding.
/// Pure parts run on in-memory snapshots; the coordinator runs on a temp project directory (disk reads) plus the
/// fake engineering (ReadHmiFile/WriteHmiFile), and writes only after the engineer confirmed.</summary>
public sealed class HmiAuthoringTests : IDisposable
{
    private const string Prj = "PlantHmi";
    private readonly string _dir = Directory.CreateTempSubdirectory("hmi-authoring-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ── sample project ────────────────────────────────────────────────────────

    private const string Config = """
        {
          "$schema": "",
          "themes": { "Base": { "resources": [] } },
          "activeTheme": "Base",
          "startupView": "Desktop.view",
          "symbols": { "internal": { "Counter": { "type": "tchmi:general#/definitions/Number", "value": 0 } } },
          "languages": { "de": "Localization\\de.localization", "en": "Localization\\en.localization" }
        }
        """;

    private const string De = """
        {
          "$schema": "./x.json",
          "locale": "de",
          "localizedText": {
            "Language": "Deutsch",
            "Start": "Start"
          }
        }
        """;

    private const string En = """
        {
          "$schema": "./x.json",
          "locale": "en",
          "localizedText": {
            "Language": "English"
          }
        }
        """;

    private const string DesktopSource = """
        <div id="Desktop" data-tchmi-type="TcHmi.Controls.System.TcHmiView" data-tchmi-top="0" data-tchmi-left="0">
          <div id="Logo" data-tchmi-type="TcHmi.Controls.Beckhoff.TcHmiImage" data-tchmi-src="Images/Logo.svg">
          </div>
          <div id="Title" data-tchmi-type="TcHmi.Controls.Beckhoff.TcHmiTextblock" data-tchmi-text="%l%Start%/l%">
          </div>
          <div id="Speed" data-tchmi-type="TcHmi.Controls.Beckhoff.TcHmiNumericInput" data-tchmi-value="%s%PLC1.MAIN.nSpeed|BindingMode=TwoWay%/s%">
          </div>
        </div>
        """;

    private const string GaugeUc = """
        <div id="Gauge" data-tchmi-type="TcHmi.Controls.System.TcHmiUserControl" data-tchmi-creator-viewport-width="320" data-tchmi-creator-viewport-height="200">
          <div id="Surface" data-tchmi-type="TcHmi.Controls.System.TcHmiHtmlHost" data-tchmi-tooltip="%f%TcHmi.Functions.Ptc.Gauge(%ctrl%Surface%/ctrl%, &quot;update&quot;, %pp%Value%/pp%, %pp%Limits%/pp%)%/f%">
          </div>
        </div>
        """;

    private const string GaugeJson = """
        {
          "$schema": "./s.json",
          "parameters": [
            { "name": "data-tchmi-value", "propertyName": "Value", "displayName": "Value", "type": "tchmi:general#/definitions/Number",
              "bindable": true, "requiredOnCompile": true, "defaultValue": null, "category": "Gauge", "description": "current value" },
            { "name": "data-tchmi-limits", "propertyName": "Limits", "displayName": "Limits", "type": "tchmi:general#/definitions/Array",
              "bindable": true, "requiredOnCompile": false, "defaultValue": null },
            { "name": "data-tchmi-unit-text", "propertyName": "UnitText", "type": "tchmi:general#/definitions/String",
              "bindable": false, "requiredOnCompile": false, "defaultValue": "mm" }
          ],
          "description": "A gauge"
        }
        """;

    private const string GaugeFunction = """
        {
          "function": { "name": "Gauge", "namespace": "TcHmi.Functions.Ptc", "description": "renders",
            "returnValue": { "type": "tchmi:general#/definitions/String" },
            "arguments": [ { "name": "surface", "type": "tchmi:framework#/definitions/Control", "required": true } ] }
        }
        """;

    // A CRLF checkout must not change what the tests see.
    private static readonly string Desktop = DesktopSource.ReplaceLineEndings("\n");

    private static Dictionary<string, string> SampleFiles() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Properties/tchmiconfig.json"] = Config,
        ["Localization/de.localization"] = De,
        ["Localization/en.localization"] = En,
        ["Desktop.view"] = Desktop,
        ["UserControls/Gauge/Gauge.usercontrol"] = GaugeUc,
        ["UserControls/Gauge/Gauge.usercontrol.json"] = GaugeJson,
        ["Functions/Gauge.function.json"] = GaugeFunction,
    }.ToDictionary(kv => kv.Key, kv => kv.Value.ReplaceLineEndings("\n"), StringComparer.OrdinalIgnoreCase);

    private static HmiProjectSnapshot Snap(Dictionary<string, string>? files = null) =>
        new(@"C:\Temp\PlantHmi", files ?? SampleFiles(), []);

    private static HmiSymbolSources Sources(params string[] mapped) =>
        new(new HashSet<string>(mapped, StringComparer.Ordinal), "test", new HashSet<string>(["ADS", "TcHmiSrv"], StringComparer.Ordinal),
            new HashSet<string>(["Counter"], StringComparer.Ordinal));

    // ── markup ────────────────────────────────────────────────────────────────

    [Fact]
    public void Markup_ParsesNestedControls_ScriptAttributes_AndPositions()
    {
        string text = "<div id=\"Root\" data-tchmi-type=\"T.View\">\n  <div id=\"A\" data-tchmi-type=\"T.Host\" data-tchmi-x=\"a &amp; b\">\n" +
                      "    <script data-tchmi-target-attribute=\"data-tchmi-list\" type=\"application/json\">\n[1, 2]\n</script>\n  </div>\n" +
                      "  <!-- <div id=\"Commented\"></div> -->\n</div>\n";
        var doc = HmiMarkup.Parse(text);

        Assert.Empty(doc.Problems);
        Assert.Equal(["Root", "A"], doc.Elements.Select(e => e.Id));
        Assert.Equal("Root", doc.Root!.Id);
        var a = doc.Elements[1];
        Assert.Same(doc.Root, a.Parent);
        Assert.Equal(2, a.Line);
        Assert.Equal("a & b", a.Value("data-tchmi-x"));
        Assert.Equal("[1, 2]", a.Value("data-tchmi-list"));
        Assert.True(a.Has("data-tchmi-list"));
        Assert.Equal("</div>", text[a.CloseStart..a.End]);
        Assert.Equal(text.TrimEnd().Length, doc.Root.End);
    }

    [Fact]
    public void Markup_ReportsUnclosedAndSecondRoot()
    {
        var doc = HmiMarkup.Parse("<div id=\"A\"></div>\n<div id=\"B\">\n");
        Assert.Contains(doc.Problems, p => p.Contains("second top-level element", StringComparison.Ordinal));
        Assert.Contains(doc.Problems, p => p.Contains("never closed", StringComparison.Ordinal));
    }

    [Fact]
    public void Bindings_FindNestedExpressions_OptionsSubPathsAndTriggerFunctions()
    {
        string text = "<div data-tchmi-tooltip=\"%f%TcHmi.Functions.Ptc.Gauge(%ctrl%Surface::Width%/ctrl%, &quot;x&quot;, %s%PLC1.MAIN.st::nValue|BindingMode=TwoWay%/s%)%/f%\">\n" +
                      "<script type=\"application/json\">[{ \"fn\": \"TcHmi.Functions.Beckhoff.ToUpper\" }]</script>\n" +
                      "%i%Counter%/i% %l%Start%/l% %pp%Value%/pp% %s%PLC1.MAIN.arr[2]%/s%";
        var b = HmiMarkup.Bindings(text);

        Assert.Contains(b, x => x is { Kind: "f", Target: "TcHmi.Functions.Ptc.Gauge", Line: 1 });
        Assert.Contains(b, x => x is { Kind: "ctrl", Target: "Surface" });
        Assert.Contains(b, x => x is { Kind: "s", Target: "PLC1.MAIN.st" });
        Assert.Contains(b, x => x is { Kind: "f", Target: "TcHmi.Functions.Beckhoff.ToUpper", Line: 2 });
        Assert.Contains(b, x => x is { Kind: "i", Target: "Counter", Line: 3 });
        Assert.Contains(b, x => x is { Kind: "l", Target: "Start" });
        Assert.Contains(b, x => x is { Kind: "pp", Target: "Value" });
        Assert.Contains(b, x => x is { Kind: "s", Target: "PLC1.MAIN.arr" });
    }

    // ── components ────────────────────────────────────────────────────────────

    [Fact]
    public void Components_IndexUserControlsWithParameters_FunctionsLanguagesAndUses()
    {
        string content = "<div id=\"Main\" data-tchmi-type=\"TcHmi.Controls.System.TcHmiContent\">\n" +
                         "  <div id=\"G1\" data-tchmi-type=\"TcHmi.Controls.System.TcHmiUserControlHost\" data-tchmi-target-user-control=\"UserControls/Gauge/Gauge.usercontrol\" data-tchmi-value=\"1\"></div>\n</div>";
        var files = SampleFiles();
        files["Contents/Main.content"] = content;
        var index = HmiComponents.Build(Snap(files));

        Assert.Equal("Desktop.view", index.StartupView);
        Assert.Equal(["Base"], index.Themes);
        Assert.Equal(["de", "en"], index.Languages.Select(l => l.Locale));
        Assert.All(index.Languages, l => Assert.True(l.Exists));
        var uc = Assert.Single(index.UserControls);
        Assert.Equal("UserControls/Gauge/Gauge.usercontrol", uc.Path);
        Assert.Equal((320, 200), (uc.DesignWidth, uc.DesignHeight));
        Assert.Equal(["Value", "Limits", "UnitText"], uc.Parameters.Select(p => p.PropertyName));
        Assert.True(uc.Parameters[0].RequiredOnCompile);
        Assert.Equal("\"mm\"", uc.Parameters[2].DefaultJson);
        var fn = Assert.Single(index.Functions);
        Assert.Equal("TcHmi.Functions.Ptc.Gauge", fn.FullName);
        var main = index.Pages.Single(p => p.Path == "Contents/Main.content");
        Assert.Equal("content", main.Kind);
        Assert.Contains(main.Uses, u => u is { Kind: "usercontrol", Target: "UserControls/Gauge/Gauge.usercontrol" });
        Assert.Contains(index.Pages.Single(p => p.Kind == "usercontrol").Uses, u => u is { Kind: "function", Target: "TcHmi.Functions.Ptc.Gauge" });
    }

    [Fact]
    public void Components_UserControlWithoutJson_IsReportedNotGuessed()
    {
        var files = SampleFiles();
        files.Remove("UserControls/Gauge/Gauge.usercontrol.json");
        var uc = Assert.Single(HmiComponents.Build(Snap(files)).UserControls);
        Assert.Empty(uc.Parameters);
        Assert.Contains("missing", uc.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Components_Load_ReadsAuthoredFilesAndSkipsBuildOutputAndServer()
    {
        Write("Desktop.view", Desktop);
        Write("Properties/tchmiconfig.json", Config);
        Write("Server/TcHmiSrv/x.view", "<div></div>");
        Write("bin/liveview.view", "<div></div>");
        Write("Images/logo.svg", "<svg/>");
        var snap = HmiComponents.Load(_dir);
        Assert.Equal(["Desktop.view", "Properties/tchmiconfig.json"], snap.Files.Keys.Order(StringComparer.Ordinal));
    }

    // ── binding check ─────────────────────────────────────────────────────────

    [Fact]
    public void Check_MappedSymbolsResolve_UnmappedAreErrorsWithASuggestion()
    {
        var snap = Snap();
        var index = HmiComponents.Build(snap);

        var ok = HmiBindingCheck.Check(snap, index, Sources("PLC1.MAIN.nSpeed"));
        Assert.DoesNotContain(ok.Findings, f => f.Kind == "s" && f.Severity == "error");

        var bad = HmiBindingCheck.Check(snap, index, Sources("PLC1.MAIN.nSpeeed"));
        var f = Assert.Single(bad.Findings, x => x.Kind == "s");
        Assert.Equal(("error", "Desktop.view", 6), (f.Severity, f.File, f.Line));
        Assert.Contains("did you mean 'PLC1.MAIN.nSpeeed'", f.Message, StringComparison.Ordinal);

        var caseOnly = HmiBindingCheck.Check(snap, index, Sources("plc1.main.nspeed"));
        Assert.Contains(caseOnly.Findings, x => x.Message.Contains("case-sensitive", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_UnknownServer_DomainSymbolsAndFrameworkFunctions_AreNotErrors()
    {
        var files = SampleFiles();
        files["Contents/C.content"] = "<div id=\"C\" data-tchmi-type=\"T\" data-tchmi-x=\"%s%ADS.Config::RUNTIMES%/s%\" " +
                                      "data-tchmi-y=\"%f%TcHmi.Functions.Beckhoff.ToUpper(%s%ListSymbols%/s%)%/f%\"></div>";
        var snap = Snap(files);
        var unknown = new HmiSymbolSources(null, "nowhere", new HashSet<string>(["ADS"]), new HashSet<string>());
        var r = HmiBindingCheck.Check(snap, HmiComponents.Build(snap), unknown);

        Assert.Contains(r.Findings, f => f is { Kind: "s", Severity: "warning", Reference: "PLC1.MAIN.nSpeed" });
        Assert.Contains(r.Findings, f => f is { Kind: "s", Severity: "info", Reference: "ADS.Config" });
        Assert.Contains(r.Findings, f => f is { Kind: "f", Severity: "info" });
        Assert.DoesNotContain(r.Findings, f => f.Reference == "ListSymbols");
        Assert.DoesNotContain(r.Findings, f => f.Severity == "error" && f.Kind is "s" or "f");
    }

    [Fact]
    public void Check_UserControlRules_IdsTextsAndHosts()
    {
        var files = SampleFiles();
        files["UserControls/Gauge/Gauge.usercontrol"] = GaugeUc.Replace("%pp%Limits%/pp%", "%pp%Limit%/pp%", StringComparison.Ordinal);
        files["Desktop.view"] = Desktop.Replace("</div>\n</div>",
            "</div>\n  <div id=\"Logo\" data-tchmi-type=\"X\"></div>\n" +
            "  <div id=\"G\" data-tchmi-type=\"TcHmi.Controls.System.TcHmiUserControlHost\" data-tchmi-target-user-control=\"UserControls/Gauge/Gauge.usercontrol\" data-tchmi-unit-text=\"%s%PLC1.MAIN.nSpeed%/s%\"></div>\n" +
            "  <div id=\"H\" data-tchmi-type=\"TcHmi.Controls.System.TcHmiUserControlHost\" data-tchmi-target-user-control=\"UserControls/Gauges/Gauge.usercontrol\"></div>\n" +
            "  <div id=\"R\" data-tchmi-type=\"TcHmi.Controls.System.TcHmiRegion\" data-tchmi-target-content=\"Contents/Missing.content\"></div>\n" +
            "  <div id=\"T\" data-tchmi-type=\"X\" data-tchmi-text=\"%pp%Value%/pp% %ctrl%Nope%/ctrl% %i%Countr%/i%\"></div>\n</div>", StringComparison.Ordinal);
        var snap = Snap(files);
        var r = HmiBindingCheck.Check(snap, HmiComponents.Build(snap), Sources("PLC1.MAIN.nSpeed"));
        string Msg(string kind, string reference) => r.Findings.Single(f => f.Kind == kind && f.Reference == reference).Message;

        Assert.Contains("is not a parameter", Msg("pp", "Limit"), StringComparison.Ordinal);
        Assert.Contains("did you mean 'Limits'", Msg("pp", "Limit"), StringComparison.Ordinal);
        Assert.Contains("only valid inside a .usercontrol", Msg("pp", "Value"), StringComparison.Ordinal);
        Assert.Contains("missing in language 'en'", Msg("l", "Start"), StringComparison.Ordinal);
        Assert.Contains("used 2 times", Msg("id", "Logo"), StringComparison.Ordinal);
        Assert.Contains("required parameter 'Value'", Msg("usercontrol", "Value"), StringComparison.Ordinal);
        Assert.Contains("not bindable", Msg("usercontrol", "UnitText"), StringComparison.Ordinal);
        Assert.Contains("did you mean 'UserControls/Gauge/Gauge.usercontrol'", Msg("usercontrol", "UserControls/Gauges/Gauge.usercontrol"), StringComparison.Ordinal);
        Assert.Contains("not a page", Msg("content", "Contents/Missing.content"), StringComparison.Ordinal);
        Assert.Contains("no control with id 'Nope'", Msg("ctrl", "Nope"), StringComparison.Ordinal);
        Assert.Contains("did you mean 'Counter'", Msg("i", "Countr"), StringComparison.Ordinal);
        Assert.Equal("error", r.Findings[0].Severity);                    // errors first
        Assert.DoesNotContain(r.Findings, f => f is { Kind: "ctrl", Reference: "Surface" });
    }

    [Fact]
    public void Check_OnlyPaths_LimitsTheFilesButResolvesAcrossTheProject()
    {
        var snap = Snap();
        var r = HmiBindingCheck.Check(snap, HmiComponents.Build(snap), Sources(), ["UserControls/Gauge/Gauge.usercontrol"]);
        Assert.Equal(1, r.FilesChecked);
        Assert.All(r.Findings, f => Assert.Equal("UserControls/Gauge/Gauge.usercontrol", f.File));
        Assert.DoesNotContain(r.Findings, f => f.Kind == "f" && f.Severity != "info");     // project function resolves
    }

    // ── localization ──────────────────────────────────────────────────────────

    [Fact]
    public void Localization_Analyze_MissingUndefinedUnused()
    {
        var files = SampleFiles();
        files["Desktop.view"] += "\n<!-- --> %l%Stop%/l%";
        var snap = Snap(files);
        var r = HmiLocalization.Analyze(snap, HmiComponents.Build(snap));

        Assert.Equal(["Language", "Start"], r.AllKeys);
        Assert.Equal(["Start"], r.MissingByLanguage["en"]);
        Assert.Empty(r.MissingByLanguage["de"]);
        Assert.Contains("\"key\":\"Stop\"", JsonSerializer.Serialize(r.UsedButUndefined), StringComparison.Ordinal);
        Assert.Equal(["Language"], r.DefinedButUnused);
    }

    [Fact]
    public void Localization_Apply_KeepsOrderAndSchema_AppendsSorted_Removes()
    {
        var (edit, error) = HmiLocalization.Apply(De, new Dictionary<string, string?>
        {
            ["Zeta"] = "Z", ["Start"] = "Los", ["Language"] = null, ["Alpha"] = "Ä", ["Gone"] = null,
        });
        Assert.Null(error);
        Assert.Equal(["Alpha", "Zeta"], edit!.Added);
        Assert.Equal(["Start"], edit.Changed);
        Assert.Equal(["Language"], edit.Removed);
        Assert.Equal(["Gone"], edit.AlreadyAbsent);
        Assert.Contains("\"$schema\": \"./x.json\"", edit.Text, StringComparison.Ordinal);
        Assert.Contains("\"Alpha\": \"Ä\"", edit.Text, StringComparison.Ordinal);     // not \u00C4
        var keys = JsonDocument.Parse(edit.Text).RootElement.GetProperty("localizedText").EnumerateObject().Select(p => p.Name);
        Assert.Equal(["Start", "Alpha", "Zeta"], keys);
    }

    [Theory]
    [InlineData(null, "texts is required")]
    [InlineData("[]", "keyed by locale")]
    [InlineData("{\"de\": \"x\"}", "must be an object")]
    [InlineData("{\"de\": {\"a%b\": \"x\"}}", "invalid key")]
    [InlineData("{\"de\": {\"a\": 1}}", "string or null")]
    [InlineData("{\"de\": {}}", "no key")]
    public void Localization_ParseChanges_RefusesBadInput(string? json, string expected)
    {
        var (changes, error) = HmiLocalization.ParseChanges(json);
        Assert.Null(changes);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    // ── placement ─────────────────────────────────────────────────────────────

    private static HmiUserControlInfo Gauge() =>
        HmiComponents.Build(Snap()).UserControl("UserControls/Gauge/Gauge.usercontrol")!;

    private static Dictionary<string, JsonElement> Params(string json) => HmiPlacement.ParseParameters(json).Values!;

    [Fact]
    public void Place_AppendsAHostAsLastChildOfTheRoot_WithParameterAttributesAndScripts()
    {
        var (r, error) = HmiPlacement.Place(Desktop, Gauge(), "Gauge1", new HmiLayout(10, 20, null, null, "px", "px"),
            Params("{\"value\": \"%s%PLC1.MAIN.nSpeed%/s%\", \"Limits\": [0, 100], \"UnitText\": \"m/s & more\"}"), replace: false);
        Assert.Null(error);

        var doc = HmiMarkup.Parse(r!.Text);
        Assert.Empty(doc.Problems);
        var host = doc.Elements.Single(e => e.Id == "Gauge1");
        Assert.Same(doc.Root, host.Parent);
        Assert.Same(host, doc.Root!.Children.Last());
        Assert.Equal(HmiMarkup.UserControlHostType, host.Type);
        Assert.Equal("UserControls/Gauge/Gauge.usercontrol", host.Value(HmiMarkup.TargetUserControlAttribute));
        Assert.Equal("320", host.Value("data-tchmi-width"));                  // design size of the user control
        Assert.Equal("200", host.Value("data-tchmi-height"));
        Assert.Equal("%s%PLC1.MAIN.nSpeed%/s%", host.Value("data-tchmi-value"));
        Assert.Equal("[0,100]", host.Value("data-tchmi-limits"));
        Assert.Equal("m/s & more", host.Value("data-tchmi-unit-text"));
        Assert.Equal(host.Line, r.Line);
        Assert.Contains("\n  <div id=\"Gauge1\"", r.Text, StringComparison.Ordinal);   // sibling indentation
        Assert.EndsWith("</div>\n</div>", r.Text.TrimEnd(), StringComparison.Ordinal);
        Assert.StartsWith(Desktop.TrimEnd()[..Desktop.TrimEnd().LastIndexOf("</div>", StringComparison.Ordinal)], r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Place_ReplacesTheExistingHost_AndRefusesEverythingElse()
    {
        var gauge = Gauge();
        var layout = new HmiLayout(0, 0, 50, 50, "%", "%");
        string once = HmiPlacement.Place(Desktop, gauge, "G", layout, Params("{\"Value\": 1}"), false).Result!.Text;

        var (twice, _) = HmiPlacement.Place(once, gauge, "G", layout, Params("{\"Value\": 2}"), replace: true);
        var doc = HmiMarkup.Parse(twice!.Text);
        Assert.Single(doc.Elements, e => e.Id == "G");
        Assert.Equal("2", doc.Elements.Single(e => e.Id == "G").Value("data-tchmi-value"));
        Assert.True(twice.Replaced);

        Assert.Contains("already exists", HmiPlacement.Place(once, gauge, "G", layout, Params("{\"Value\": 1}"), false).Error, StringComparison.Ordinal);
        Assert.Contains("not a user-control host", HmiPlacement.Place(Desktop, gauge, "Logo", layout, Params("{\"Value\": 1}"), true).Error, StringComparison.Ordinal);
        Assert.Contains("no control with id", HmiPlacement.Place(Desktop, gauge, "New", layout, Params("{\"Value\": 1}"), true).Error, StringComparison.Ordinal);
        Assert.Contains("Parameters: Value, Limits, UnitText", HmiPlacement.Place(Desktop, gauge, "N", layout, Params("{\"Valu\": 1}"), false).Error, StringComparison.Ordinal);
        Assert.Contains("required parameter(s) not set: Value", HmiPlacement.Place(Desktop, gauge, "N", layout, Params("{}"), false).Error, StringComparison.Ordinal);
        Assert.Contains("not bindable", HmiPlacement.Place(Desktop, gauge, "N", layout, Params("{\"Value\": 1, \"UnitText\": \"%s%X%/s%\"}"), false).Error, StringComparison.Ordinal);
        Assert.Contains("not a valid control id", HmiPlacement.Place(Desktop, gauge, "1x", layout, Params("{\"Value\": 1}"), false).Error, StringComparison.Ordinal);
        Assert.Contains("'px' or '%'", HmiPlacement.Place(Desktop, gauge, "N", layout with { WidthUnit = "em" }, Params("{\"Value\": 1}"), false).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Place_IntoAnEmptyRootWithCrLf_IndentsBelowTheRoot()
    {
        string empty = "<div id=\"C\" data-tchmi-type=\"TcHmi.Controls.System.TcHmiContent\">\r\n</div>\r\n";
        var (r, error) = HmiPlacement.Place(empty, Gauge(), "G", new HmiLayout(0, 0, null, null, "px", "px"), Params("{\"Value\": 3}"), false);
        Assert.Null(error);
        Assert.Contains("\r\n  <div id=\"G\"", r!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", r.Text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        var doc = HmiMarkup.Parse(r.Text);
        Assert.Same(doc.Root, doc.Elements[1].Parent);
    }

    // ── scaffold ──────────────────────────────────────────────────────────────

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void Scaffold_RowPerField_ByValueKind_NestedHeaders_ArraysSkipped()
    {
        var value = Json("{\"bOn\": true, \"nSpeed\": 12.5, \"sName\": \"A\", \"stPos\": {\"x\": 1, \"y\": 2}, \"aLog\": [1,2]}");
        var r = HmiScaffold.Build("PLC1.MAIN.st", value, "Axis", readOnly: false, maxDepth: 3);

        var doc = HmiMarkup.Parse(r.Text);
        Assert.Empty(doc.Problems);
        Assert.Equal("Axis", doc.Root!.Id);
        Assert.Equal(HmiScaffold.Content, doc.Root.Type);
        Assert.Equal(6, r.Rows);   // 3 scalars + header + 2 nested
        Assert.Equal(HmiScaffold.Checkbox, doc.Elements.Single(e => e.Id == "Axis_bOn").Type);
        Assert.Equal("%s%PLC1.MAIN.st::bOn%/s%", doc.Elements.Single(e => e.Id == "Axis_bOn").Value(HmiScaffold.StateSymbolAttribute));
        Assert.Equal("%s%PLC1.MAIN.st::nSpeed|BindingMode=TwoWay%/s%", doc.Elements.Single(e => e.Id == "Axis_nSpeed").Value(HmiScaffold.ValueAttribute));
        Assert.Equal(HmiScaffold.Textbox, doc.Elements.Single(e => e.Id == "Axis_sName").Type);
        Assert.Equal("stPos", doc.Elements.Single(e => e.Id == "Axis_stPos_Header").Value(HmiScaffold.TextAttribute));
        Assert.Equal("%s%PLC1.MAIN.st::stPos::x|BindingMode=TwoWay%/s%", doc.Elements.Single(e => e.Id == "Axis_stPos_x").Value(HmiScaffold.ValueAttribute));
        Assert.Contains(r.Skipped, s => s.StartsWith("PLC1.MAIN.st::aLog: array", StringComparison.Ordinal));
        Assert.Equal(doc.Elements.Count, doc.Elements.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void Scaffold_ReadOnly_UsesTextblocksAndDisabledCheckboxes_AndHonoursDepth()
    {
        var r = HmiScaffold.Build("S", Json("{\"b\": false, \"n\": 1, \"inner\": {\"deep\": {\"x\": 1}}}"), "V", readOnly: true, maxDepth: 1);
        var doc = HmiMarkup.Parse(r.Text);
        Assert.Equal("false", doc.Elements.Single(e => e.Id == "V_b").Value("data-tchmi-is-enabled"));
        Assert.Equal(HmiScaffold.Textblock, doc.Elements.Single(e => e.Id == "V_n").Type);
        Assert.DoesNotContain(r.Bindings, b => b.Contains("TwoWay", StringComparison.Ordinal));
        Assert.Contains(r.Skipped, s => s.Contains("maxDepth", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("False", "false")]
    [InlineData("True", "true")]
    [InlineData("12", "12")]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("hello", "\"hello\"")]
    public void Scaffold_ParseValue_NormalisesServerValues(string raw, string json) =>
        Assert.Equal(json, HmiScaffold.ParseValue(raw)!.Value.GetRawText());

    // ── live view ─────────────────────────────────────────────────────────────

    [Fact]
    public void LiveView_UrlFromServerPortAndNewestLiveviewFile()
    {
        Assert.Equal(3000, HmiCoordinator.ServerPort("<PropertyGroup><HmiCommunicationServerPort>3000</HmiCommunicationServerPort></PropertyGroup>"));
        Assert.Null(HmiCoordinator.ServerPort("<HmiCommunicationServerPort>99999</HmiCommunicationServerPort>"));

        Write("P.hmiproj", "<Project><HmiCommunicationServerPort> 3010 </HmiCommunicationServerPort></Project>");
        Assert.Contains("twincat_hmi_build", HmiCoordinator.LiveViewUrlIn(_dir).Error, StringComparison.Ordinal);
        Write("liveview_OLD.html", "x");
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "liveview_OLD.html"), DateTime.UtcNow.AddHours(-1));
        Write("liveview_NEW.html", "x");
        Assert.Equal("http://127.0.0.1:3010/liveview_NEW.html", HmiCoordinator.LiveViewUrlIn(_dir).Url);
    }

    [Fact]
    public void LiveView_TranslatesOnlyTheCdpEventsThatMatter()
    {
        static System.Text.Json.Nodes.JsonObject P(string json) => (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!;

        var ex = CdpBrowser.Translate("Runtime.exceptionThrown", P("{\"exceptionDetails\":{\"text\":\"Uncaught\",\"url\":\"http://x/a.js\",\"lineNumber\":4,\"exception\":{\"description\":\"TypeError: x is undefined\\n at f\"}}}"));
        Assert.Equal(new CdpEvent("exception", "error", "TypeError: x is undefined", "http://x/a.js", 5), ex);
        var warn = CdpBrowser.Translate("Runtime.consoleAPICalled", P("{\"type\":\"warning\",\"args\":[{\"type\":\"string\",\"value\":\"slow\"},{\"type\":\"number\",\"value\":3}]}"));
        Assert.Equal(("console", "warning", "slow 3"), (warn!.Kind, warn.Level, warn.Text));
        Assert.Null(CdpBrowser.Translate("Runtime.consoleAPICalled", P("{\"type\":\"log\",\"args\":[]}")));
        Assert.Equal("HTTP 404", CdpBrowser.Translate("Network.responseReceived", P("{\"response\":{\"status\":404,\"url\":\"http://x/b.js\"}}"))!.Text);
        Assert.Null(CdpBrowser.Translate("Network.responseReceived", P("{\"response\":{\"status\":200}}")));
        Assert.Null(CdpBrowser.Translate("Network.loadingFailed", P("{\"canceled\":true,\"errorText\":\"x\"}")));
        Assert.Null(CdpBrowser.Translate("Page.loadEventFired", P("{}")));
    }

    [Fact]
    public void LiveView_ReportGroupsRepeatsAndWarnsOnErrors()
    {
        var o = new HmiLiveViewOptions("http://127.0.0.1:3000/liveview_X.html", null, 30000, 0, 1280, 800, null, "1+1");
        var events = new[]
        {
            new CdpEvent("console", "error", "Symbol not mapped", "http://127.0.0.1:3000/a.js", 3),
            new CdpEvent("console", "error", "Symbol not mapped", "http://127.0.0.1:3000/a.js", 3),
            new CdpEvent("console", "warning", "slow", null, null),
        };
        var controls = System.Text.Json.Nodes.JsonNode.Parse("{\"total\": 4, \"zeroSize\": [\"A\"]}");
        var (summary, json) = HmiLiveViewCheck.Report(o, "chrome.exe", "ready", 900, null, "Desktop", null, controls, events, null, "2", null, 1500);

        Assert.StartsWith("[Warn] ", summary, StringComparison.Ordinal);
        Assert.Contains("view 'Desktop' ready after 900 ms, 4 control(s), 1 without size; 1 error(s), 1 warning(s)", summary, StringComparison.Ordinal);
        Assert.Contains("expression → 2", summary, StringComparison.Ordinal);
        Assert.Contains("console: Symbol not mapped (/a.js:3)", summary, StringComparison.Ordinal);
        Assert.Contains("\"count\": 2", json, StringComparison.Ordinal);

        var (clean, _) = HmiLiveViewCheck.Report(o with { Expression = null }, "chrome.exe", "ready", 900, null, "Desktop", null, controls, [], null, null, null, 1500);
        Assert.DoesNotContain("[Warn]", clean, StringComparison.Ordinal);
        Assert.Contains("login page", HmiLiveViewCheck.Report(o, "c", "login", 1, null, null, null, null, [], null, null, null, 1).Summary, StringComparison.Ordinal);
    }

    // ── coordinator ───────────────────────────────────────────────────────────

    private void Write(string rel, string text)
    {
        string p = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    /// <summary>The sample project on disk AND in the fake engineering.</summary>
    private FakeTwinCatSession Project()
    {
        var f = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1, [new HmiProjectEntry(Prj, _dir, true, false)], null),
        };
        foreach (var (path, text) in SampleFiles())
        {
            Write(path, text);
            f.HmiFiles[path] = text;
        }
        return f;
    }

    private static bool Wrote(FakeTwinCatSession f) =>
        f.CallLog.Any(c => c.StartsWith("WriteHmiFile", StringComparison.Ordinal) || c.StartsWith("AddHmiItem", StringComparison.Ordinal));

    [Fact]
    public void Coordinator_ListComponentsAndCheckBindings_ReadFromDisk()
    {
        var f = Project();
        f.HmiMappedSymbols.Add(new HmiMappedSymbol("PLC1.MAIN.nSpeed", "ADS", null, null, false, false));

        var (summary, json) = new HmiCoordinator(f).ListComponents(Prj);
        Assert.Contains("1 view(s), 0 content(s), 1 user control(s), 1 function(s), languages de/en", summary, StringComparison.Ordinal);
        Assert.Contains("\"propertyName\": \"Value\"", json, StringComparison.Ordinal);

        var (check, _) = new HmiCoordinator(f).CheckBindings(Prj, null, includeInfo: false);
        Assert.Contains("1 error(s)", check, StringComparison.Ordinal);                   // %l%Start missing in en
        Assert.Contains("engineering server (1 mapped)", check, StringComparison.Ordinal);

        f.ListHmiMappedSymbolsThrows = new InvalidOperationException("server down");
        var (unchecked_, _) = new HmiCoordinator(f).CheckBindings(Prj, ["Desktop.view"], false);
        Assert.Contains("did not answer", unchecked_, StringComparison.Ordinal);
        Assert.Contains("1 warning(s)", unchecked_, StringComparison.Ordinal);

        Assert.StartsWith("[Error]", new HmiCoordinator(f).CheckBindings(Prj, ["Images/a.png"], false).Summary, StringComparison.Ordinal);
        Assert.False(Wrote(f));
    }

    [Fact]
    public void Coordinator_SetLocalizedTexts_RefusesUnknownLanguageBeforeAsking_ThenWritesEveryFile()
    {
        var f = Project();
        var confirmer = new FakeHmiConfirmer();
        var c = new HmiCoordinator(f, confirmer);

        var (refused, _) = c.SetLocalizedTexts(Prj, "{\"fr\": {\"Start\": \"Démarrer\"}}", 60);
        Assert.Contains("'fr' is not a language", refused, StringComparison.Ordinal);
        Assert.Empty(confirmer.Requests);

        var (done, _) = c.SetLocalizedTexts(Prj, "{\"de\": {\"Stop\": \"Halt\"}, \"en\": {\"Start\": \"Start\", \"Stop\": \"Stop\"}}", 60);
        Assert.Contains("2 file(s)", done, StringComparison.Ordinal);
        Assert.Single(confirmer.Requests);
        Assert.Contains("\"Stop\": \"Halt\"", f.HmiFiles["Localization/de.localization"], StringComparison.Ordinal);
        Assert.Contains("\"Start\": \"Start\"", f.HmiFiles["Localization/en.localization"], StringComparison.Ordinal);

        var (nothing, _) = c.SetLocalizedTexts(Prj, "{\"de\": {\"Start\": \"Start\"}}", 60);
        Assert.StartsWith("Nothing to do", nothing, StringComparison.Ordinal);
        Assert.Single(confirmer.Requests);
    }

    [Fact]
    public void Coordinator_PlaceUserControl_WritesOnlyAfterConfirmation_AndVerifies()
    {
        var f = Project();
        var rejecting = new FakeHmiConfirmer(HmiConfirmOutcome.Rejected);
        var (rejected, _) = new HmiCoordinator(f, rejecting).PlaceUserControl(Prj, "Desktop.view", "UserControls/Gauge/Gauge.usercontrol",
            "G1", 0, 0, null, null, null, null, "{\"Value\": 5}", false, 60);
        Assert.StartsWith("[Error]", rejected, StringComparison.Ordinal);
        Assert.False(Wrote(f));

        var (placed, json) = new HmiCoordinator(f, new FakeHmiConfirmer()).PlaceUserControl(Prj, "Desktop.view",
            "UserControls/Gauge/Gauge.usercontrol", "G1", 0, 0, null, null, null, null, "{\"Value\": 5}", false, 60);
        Assert.Contains("verified by read-back", placed, StringComparison.Ordinal);
        Assert.Contains("id=\"G1\"", f.HmiFiles["Desktop.view"], StringComparison.Ordinal);
        Assert.Contains("\"replaced\": false", json, StringComparison.Ordinal);

        var (unknownUc, _) = new HmiCoordinator(f, new FakeHmiConfirmer()).PlaceUserControl(Prj, "Desktop.view",
            "UserControls/Gauge/Gage.usercontrol", "G2", 0, 0, null, null, null, null, null, false, 60);
        Assert.Contains("did you mean", unknownUc, StringComparison.Ordinal);

        f.HmiOpenFiles.Add("Desktop.view");
        var (open, _) = new HmiCoordinator(f, new FakeHmiConfirmer()).PlaceUserControl(Prj, "Desktop.view",
            "UserControls/Gauge/Gauge.usercontrol", "G3", 0, 0, null, null, null, null, "{\"Value\": 5}", false, 60);
        Assert.Contains("open in an editor tab", open, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_Scaffold_DryRunNeedsNoServer_WriteCreatesTheContent()
    {
        var f = Project();
        var (dry, dryJson) = new HmiCoordinator(f).ScaffoldSymbolView(Prj, "%s%PLC1.MAIN.st%/s%", "Contents/St.content", false, 3,
            "{\"b\": true, \"n\": 2}", dryRun: true, overwrite: false, 60);
        Assert.Contains("dry run", dry, StringComparison.Ordinal);
        Assert.Contains("PLC1.MAIN.st::b", dryJson, StringComparison.Ordinal);
        Assert.False(Wrote(f));

        f.HmiSymbolValues["PLC1.MAIN.st"] = "{\"b\": true, \"n\": 2}";
        var confirmer = new FakeHmiConfirmer();
        var (written, _) = new HmiCoordinator(f, confirmer).ScaffoldSymbolView(Prj, "PLC1.MAIN.st", "Contents/St.content", false, 3,
            null, dryRun: false, overwrite: false, 60);
        Assert.Contains("Scaffolded 'Contents/St.content'", written, StringComparison.Ordinal);
        Assert.Contains("value shape from the HMI server", written, StringComparison.Ordinal);
        Assert.Contains("AddHmiItem(PlantHmi, Contents, St, Content)", f.CallLog);
        Assert.Contains("data-tchmi-state-symbol=\"%s%PLC1.MAIN.st::b%/s%\"", f.HmiFiles["Contents/St.content"], StringComparison.Ordinal);

        var (exists, _) = new HmiCoordinator(f, confirmer).ScaffoldSymbolView(Prj, "PLC1.MAIN.st", "Contents/St.content", false, 3,
            null, false, overwrite: false, 60);
        Assert.Contains("already exists", exists, StringComparison.Ordinal);

        Assert.StartsWith("[Error]", new HmiCoordinator(f).ScaffoldSymbolView(Prj, "PLC1.MAIN.st", "St.view", false, 3, null, true, false, 60).Summary,
            StringComparison.Ordinal);
    }
}
