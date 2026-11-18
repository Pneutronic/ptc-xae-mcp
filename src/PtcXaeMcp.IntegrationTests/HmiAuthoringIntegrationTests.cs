using System.Globalization;
using System.Text.Json;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Live tests of the hmi-authoring group against a REAL XAE with the TEMP copy of <see cref="HmiFixture"/>:
/// a user control with a required parameter is created, placed on a new view, checked, built and seen in the
/// live view through a headless browser. Writes need the auto-confirm seam (PTC_XAE_MCP_HMI_AUTOCONFIRM_TEMP=1);
/// without it the chain is skipped — the popup invariant itself is covered by the hmi-views tests.
/// </summary>
[Collection("Stage HMI")]
[Trait("Stage", "HMI")]
public class HmiAuthoringIntegrationTests(HmiFixture fx)
{
    private static readonly string Run = DateTime.Now.ToString("HHmmssfff", CultureInfo.InvariantCulture);

    private static JsonElement Json(string text) =>
        HmiFixture.JsonAfterSummary(text) ?? throw new Xunit.Sdk.XunitException("Expected JSON after the summary, got: " + text);

    private async Task Write(string path, string source)
    {
        string r = await fx.CallAsync("twincat_hmi_write_file", new()
            { ["path"] = path, ["source"] = source, ["confirmTimeoutSeconds"] = 10 }, TimeSpan.FromMinutes(1));
        Assert.True(r.StartsWith($"Wrote '{path}'", StringComparison.Ordinal) || r.StartsWith("Nothing to do", StringComparison.Ordinal), r);
    }

    [SkippableFact(Timeout = 1_200_000)]
    public async Task UserControl_PlaceCheckBuild_AndSeeItInTheLiveView()
    {
        fx.Guard();
        Skip.IfNot(HmiFixture.ExpectAutoConfirm, "needs the TEMP auto-confirm seam for the writes");
        string uc = "McpAuthUc" + Run, ucPath = $"UserControls/McpAuth/{uc}.usercontrol";
        string view = "McpAuthView" + Run, viewPath = $"McpAuth/{view}.view";
        string caption = "Hallo " + Run;

        // 1 a user control whose Textblock shows its required parameter Caption
        string add = await fx.CallAsync("twincat_hmi_add_item", new()
            { ["kind"] = "usercontrol", ["folder"] = "UserControls/McpAuth", ["name"] = uc, ["confirmTimeoutSeconds"] = 10 }, TimeSpan.FromMinutes(1));
        Assert.StartsWith($"Created '{ucPath}'", add);
        await Write(ucPath,
            $"<div id=\"{uc}\" data-tchmi-type=\"TcHmi.Controls.System.TcHmiUserControl\" data-tchmi-top=\"0\" data-tchmi-left=\"0\" " +
            "data-tchmi-width=\"100\" data-tchmi-width-unit=\"%\" data-tchmi-height=\"100\" data-tchmi-height-unit=\"%\" " +
            "data-tchmi-creator-viewport-width=\"300\" data-tchmi-creator-viewport-height=\"60\">\n" +
            "  <div id=\"Label\" data-tchmi-type=\"TcHmi.Controls.Beckhoff.TcHmiTextblock\" data-tchmi-left=\"0\" data-tchmi-top=\"0\" " +
            "data-tchmi-width=\"300\" data-tchmi-height=\"40\" data-tchmi-text=\"%pp%Caption%/pp%\">\n  </div>\n</div>\n");
        await Write(ucPath + ".json", JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["$schema"] = "(replaced by the project schema)",   // write_file keeps the template's "$schema"
            ["parameters"] = new[]
            {
                new
                {
                    name = "data-tchmi-caption", propertyName = "Caption", displayName = "Caption", type = "tchmi:general#/definitions/String",
                    bindable = true, requiredOnCompile = true, defaultValue = (string?)null, visible = true, heritable = true,
                    readOnly = false, category = "McpAuth", description = "Text to show", propertySetterName = "setCaption",
                    propertyGetterName = "getCaption", refTo = "", allowSymbolExpressionsInObject = false, defaultValueInternal = (string?)null,
                },
            },
            ["virtualRights"] = Array.Empty<object>(),
            ["description"] = "integration test control",
        }));

        // 2 the index knows the parameter
        string components = await fx.CallAsync("twincat_hmi_list_components", new() { ["hmiProject"] = fx.ProjectName });
        var ucInfo = Json(components).GetProperty("userControls").EnumerateArray().Single(u => u.GetProperty("path").GetString() == ucPath);
        var param = Assert.Single(ucInfo.GetProperty("parameters").EnumerateArray());
        Assert.Equal("Caption", param.GetProperty("propertyName").GetString());
        Assert.True(param.GetProperty("requiredOnCompile").GetBoolean());

        // 3 a view, the host refused without the required parameter, then placed
        Assert.StartsWith($"Created '{viewPath}'", await fx.CallAsync("twincat_hmi_add_item", new()
            { ["kind"] = "view", ["folder"] = "McpAuth", ["name"] = view, ["confirmTimeoutSeconds"] = 10 }, TimeSpan.FromMinutes(1)));
        string refused = await fx.CallAsync("twincat_hmi_place_user_control", new()
            { ["page"] = viewPath, ["userControl"] = ucPath, ["id"] = "Host1", ["confirmTimeoutSeconds"] = 10 });
        Assert.Contains("required parameter(s) not set: Caption", refused);
        string placed = await fx.CallAsync("twincat_hmi_place_user_control", new()
        {
            ["page"] = viewPath, ["userControl"] = ucPath, ["id"] = "Host1", ["left"] = 20, ["top"] = 20,
            ["parameters"] = JsonSerializer.Serialize(new { Caption = caption }), ["confirmTimeoutSeconds"] = 10,
        }, TimeSpan.FromMinutes(1));
        Assert.StartsWith("Placed 'Host1'", placed);
        Assert.Contains("verified by read-back", placed);

        // 4 the check is clean for these pages, and finds a broken reference
        string clean = await fx.CallAsync("twincat_hmi_check_bindings", new() { ["paths"] = new[] { viewPath, ucPath } });
        Assert.Equal(0, Json(clean).GetProperty("errors").GetInt32());
        string broken = $"McpAuth/Broken{Run}.content";
        Assert.StartsWith($"Created '{broken}'", await fx.CallAsync("twincat_hmi_add_item", new()
            { ["kind"] = "content", ["folder"] = "McpAuth", ["name"] = "Broken" + Run, ["confirmTimeoutSeconds"] = 10 }, TimeSpan.FromMinutes(1)));
        await Write(broken,
            $"<div id=\"Broken{Run}\" data-tchmi-type=\"TcHmi.Controls.System.TcHmiContent\" data-tchmi-width=\"100\" data-tchmi-height=\"100\">\n" +
            "  <div id=\"T\" data-tchmi-type=\"TcHmi.Controls.Beckhoff.TcHmiTextblock\" data-tchmi-text=\"%s%McpAuth.NotMapped%/s% %ctrl%Nope%/ctrl%\">\n  </div>\n</div>\n");
        JsonElement bad = Json(await fx.CallAsync("twincat_hmi_check_bindings", new() { ["paths"] = new[] { broken } }));
        var kinds = bad.GetProperty("findings").EnumerateArray()
            .Where(f => f.GetProperty("severity").GetString() == "error").Select(f => f.GetProperty("kind").GetString()).ToList();
        Assert.Contains("s", kinds);
        Assert.Contains("ctrl", kinds);

        // 5 texts in both languages
        string key = "McpAuth" + Run;
        string texts = await fx.CallAsync("twincat_hmi_set_localized_texts", new()
        {
            ["texts"] = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
                { ["de"] = new() { [key] = "Achse" }, ["en"] = new() { [key] = "Axis" } }),
            ["confirmTimeoutSeconds"] = 10,
        }, TimeSpan.FromMinutes(1));
        Assert.Contains("verified by read-back", texts);
        JsonElement loc = Json(await fx.CallAsync("twincat_hmi_list_localization", new() { ["hmiProject"] = fx.ProjectName }));
        Assert.Contains(key, loc.GetProperty("allKeys").EnumerateArray().Select(k => k.GetString()));

        // 6 build, then the live view renders the parameter value
        string build = await fx.CallAsync("twincat_hmi_build", new() { ["hmiProject"] = fx.ProjectName }, TimeSpan.FromMinutes(12));
        Assert.StartsWith("Built HMI project", build);
        string shot = Path.Combine(Path.GetTempPath(), $"ptc-xae-mcp-liveview-{Run}.png");
        string live = await fx.CallAsync("twincat_hmi_check_liveview", new()
        {
            ["view"] = viewPath, ["screenshotPath"] = shot,
            ["expression"] = "document.body.innerText.indexOf(" + JsonSerializer.Serialize(caption) + ") >= 0",
        }, TimeSpan.FromMinutes(3));
        Skip.If(live.Contains("No Chromium browser found", StringComparison.Ordinal), "no headless browser on this machine");
        JsonElement lv = Json(live);
        Assert.Equal("ready", lv.GetProperty("framework").GetString());
        Assert.Equal(view, lv.GetProperty("view").GetString());
        Assert.True(lv.GetProperty("expression").GetProperty("result").GetBoolean(), live);
        Assert.True(File.Exists(shot), "screenshot written");
        Assert.Contains(lv.GetProperty("controls").GetProperty("types").EnumerateObject().Select(p => p.Name),
            n => n == "TcHmi.Controls.System.TcHmiUserControlHost");
    }

    [SkippableFact(Timeout = 120_000)]
    public async Task Scaffold_DryRun_FromASample_WritesNothing()
    {
        fx.Guard();
        string path = $"McpAuth/Scaffold{Run}.content";
        string r = await fx.CallAsync("twincat_hmi_scaffold_symbol_view", new()
        {
            ["symbol"] = "McpAuth.Axis", ["path"] = path, ["dryRun"] = true,
            ["sampleValue"] = "{\"bOn\": true, \"fSpeed\": 1.5, \"stLimits\": {\"fMin\": 0}, \"aLog\": [1]}",
        });
        Assert.Contains("dry run, nothing written", r);
        JsonElement json = Json(r);
        Assert.Equal(4, json.GetProperty("rows").GetInt32());
        Assert.Contains("%s%McpAuth.Axis::fSpeed|BindingMode=TwoWay%/s%", json.GetProperty("bindings").EnumerateArray().Select(b => b.GetString()));
        Assert.StartsWith("[Error]", await fx.CallAsync("twincat_hmi_read_file", new() { ["path"] = path }));
    }
}
