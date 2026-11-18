using System.Text.Json;
using System.Text.Json.Nodes;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Project datatypes (Properties/tchmi.project.Schema.json): classification by the shapes TE2000 writes,
/// dangling-$ref detection, the diff against tchmi.project.Schema.tpl.json, and the read-only list tool,
/// which fails closed on an unreadable schema file.
/// </summary>
public class HmiProjectDatatypesTests : IDisposable
{
    private const string Project = "HMI_Fixture";

    // The shapes TE2000 writes for a remote type and its sub-types (struct with an enum field and a
    // commented member, an array with LBound 1), plus a user control and a leftover after a delete.
    internal const string Definitions = """
        {
          "PLC_TMC.ST_Rec": {
            "type": "object",
            "properties": {
              "nId": { "$ref": "tchmi:general#/definitions/DINT" },
              "sLabel": { "allOf": [ { "$ref": "tchmi:general#/definitions/STRING%2880%29" }, { "comment": "label" } ] },
              "eMode": { "$ref": "tchmi:project#/definitions/PLC_TMC.E_Mode" }
            }
          },
          "PLC_TMC.E_Mode": {
            "allOf": [
              { "qualified_only": "", "enum": [ "0", "1" ], "options": [ { "value": 0, "label": "Off" }, { "value": 1, "label": "On" } ] },
              { "$ref": "tchmi:general#/definitions/INT16" }
            ]
          },
          "PLC_TMC.ARRAY_1..11_OF-ST_Rec": {
            "type": "array", "items": { "$ref": "tchmi:project#/definitions/PLC_TMC.ST_Rec" },
            "minItems": 11, "maxItems": 11, "startOffset": 1
          },
          "ucLamp": {
            "frameworkControlNamespace": "", "frameworkControlType": "ucLamp", "frameworkInstanceOf": "UserControls/ucLamp.usercontrol",
            "frameworkUserControlConfig": "UserControls/ucLamp.usercontrol.json", "id": "tchmi:framework#/definitions/ucLamp", "type": "object"
          },
          "State": { "$ref": "tchmi:project#/definitions/PLC_TMC.E_Deleted" }
        }
        """;

    private readonly string _root;
    private readonly string _projectDir;

    public HmiProjectDatatypesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ptc-pdt-{Guid.NewGuid():N}");
        _projectDir = Path.Combine(_root, "sln", Project);
        Directory.CreateDirectory(Path.Combine(_projectDir, "Properties"));
        File.WriteAllText(Path.Combine(_projectDir, Project + ".hmiproj"),
            """<?xml version="1.0" encoding="utf-8"?><Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><HmiTmcFiles>[{"FileName":"C:\\x\\PLC.tmc","RootNamespace":"PLC_TMC","IsFromSolution":false}]</HmiTmcFiles></PropertyGroup></Project>""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string SchemaText(string definitions) =>
        $$"""{ "$schema": "http://json-schema.org/draft-04/schema#", "definitions": {{definitions}} }""";

    private void WriteSchema(string text) => File.WriteAllText(HmiProjectDatatypes.SchemaPath(_projectDir), text);
    private void WriteTemplate(string text) => File.WriteAllText(HmiProjectDatatypes.TemplatePath(_projectDir), text);

    private FakeTwinCatSession Session()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ActiveSlnPath = Path.Combine(_root, "sln", "Fixture.sln"),
            HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1, [new HmiProjectEntry(Project, _projectDir, true, false)], null),
        };
        fake.SetAdsRuntimes(FakeTwinCatSession.AdsRuntimeJson());
        return fake;
    }

    private static JsonObject Defs(string definitions) => JsonNode.Parse(definitions)!.AsObject();

    // ── pure rules ──────────────────────────────────────────────────────────

    [Fact]
    public void Describe_ClassifiesTheShapesTe2000Writes()
    {
        var byName = HmiProjectDatatypes.Describe(Defs(Definitions)).ToDictionary(d => d.Name);

        Assert.Equal("struct", byName["PLC_TMC.ST_Rec"].Kind);
        Assert.Equal("enum", byName["PLC_TMC.E_Mode"].Kind);
        Assert.Equal("array", byName["PLC_TMC.ARRAY_1..11_OF-ST_Rec"].Kind);
        Assert.Equal("userControl", byName["ucLamp"].Kind);
        Assert.Equal("alias", byName["State"].Kind);
        Assert.Equal("PLC_TMC", byName["PLC_TMC.ST_Rec"].Namespace);
        Assert.Null(byName["ucLamp"].Namespace);
    }

    [Fact]
    public void Describe_CollectsRefsAndFindsOnlyDanglingProjectRefs()
    {
        var byName = HmiProjectDatatypes.Describe(Defs(Definitions)).ToDictionary(d => d.Name);

        Assert.Equal(
            ["tchmi:general#/definitions/DINT", "tchmi:general#/definitions/STRING%2880%29", "tchmi:project#/definitions/PLC_TMC.E_Mode"],
            byName["PLC_TMC.ST_Rec"].Refs);
        Assert.Empty(byName["PLC_TMC.ST_Rec"].DanglingRefs);
        Assert.Empty(byName["PLC_TMC.ARRAY_1..11_OF-ST_Rec"].DanglingRefs);
        Assert.Equal(["PLC_TMC.E_Deleted"], byName["State"].DanglingRefs);
    }

    [Theory]
    [InlineData("tchmi:project#/definitions/PLC1.ST_X", "PLC1.ST_X")]
    [InlineData("#/definitions/PLC1.ST_X", "PLC1.ST_X")]
    [InlineData("tchmi:project#/definitions/PLC1.T%2880%29", "PLC1.T(80)")]
    [InlineData("tchmi:general#/definitions/DINT", null)]
    [InlineData("tchmi:server#/definitions/ADS-PLC1.ST_X", null)]
    public void ProjectRefTarget_OnlyProjectRefs(string reference, string? expected) =>
        Assert.Equal(expected, HmiProjectDatatypes.ProjectRefTarget(reference));

    [Fact]
    public void ParseDefinitions_EmptyAndMissingAreEmpty_BrokenIsAnError()
    {
        Assert.Empty(HmiProjectDatatypes.ParseDefinitions("""{ "definitions": { } }""").Definitions!);
        Assert.Empty(HmiProjectDatatypes.ParseDefinitions("""{ "$schema": "x" }""").Definitions!);
        Assert.NotNull(HmiProjectDatatypes.ParseDefinitions("{ broken").Error);
        Assert.NotNull(HmiProjectDatatypes.ParseDefinitions("""{ "definitions": [] }""").Error);
        Assert.NotNull(HmiProjectDatatypes.ParseDefinitions("[]").Error);
    }

    [Fact]
    public void Diff_ReportsOnlyInEachAndDifferentContent()
    {
        var schema = Defs("""{ "A": { "type": "object" }, "B": { "$ref": "x" }, "C": { "type": "array" } }""");
        var template = Defs("""{ "A": { "type": "object" }, "B": { "$ref": "y" }, "D": {} }""");

        HmiSchemaDiff diff = HmiProjectDatatypes.Diff(schema, template);

        Assert.Equal(["C"], diff.OnlyInSchema);
        Assert.Equal(["D"], diff.OnlyInTemplate);
        Assert.Equal(["B"], diff.Different);
        Assert.False(diff.Same);
        Assert.True(HmiProjectDatatypes.Diff(schema, schema).Same);
    }

    // ── the tool ────────────────────────────────────────────────────────────

    [Fact]
    public void List_ReportsKindsSourcesDanglingAndAMissingTemplate()
    {
        WriteSchema(SchemaText(Definitions));

        var (summary, json) = new HmiCoordinator(Session()).ListProjectDatatypes(null);

        Assert.DoesNotContain("[Error]", summary);
        Assert.Contains("5 project datatype(s)", summary);
        Assert.Contains("dangling $ref", summary);
        Assert.Contains("State → PLC_TMC.E_Deleted", summary);
        Assert.Contains("tchmi.project.Schema.tpl.json does not exist", summary);
        using var doc = JsonDocument.Parse(json);
        var defs = doc.RootElement.GetProperty("definitions").EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!);
        Assert.Equal("attachedTmc", defs["PLC_TMC.ST_Rec"].GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, defs["ucLamp"].GetProperty("source").ValueKind);
        Assert.Equal(1, doc.RootElement.GetProperty("danglingCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("templateDiff").ValueKind);
    }

    [Fact]
    public void List_NamespaceOfAnAdsRuntime_IsAdsRuntime()
    {
        WriteSchema(SchemaText("""{ "PLC1.ST_X": { "type": "object", "properties": {} } }"""));

        var (_, json) = new HmiCoordinator(Session()).ListProjectDatatypes(null);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("adsRuntime", doc.RootElement.GetProperty("definitions")[0].GetProperty("source").GetString());
    }

    [Fact]
    public void List_TemplateThatDiffers_IsAWarningNotHidden()
    {
        WriteSchema(SchemaText(Definitions));
        WriteTemplate(SchemaText("""{ "PLC_TMC.ST_Rec": { "type": "object" } }"""));

        var (summary, json) = new HmiCoordinator(Session()).ListProjectDatatypes(null);

        Assert.Contains("WARNING: tchmi.project.Schema.tpl.json differs", summary);
        Assert.Contains("different content: PLC_TMC.ST_Rec", summary);
        using var doc = JsonDocument.Parse(json);
        var diff = doc.RootElement.GetProperty("templateDiff");
        Assert.Equal(4, diff.GetProperty("onlyInSchema").GetArrayLength());
        Assert.Equal("PLC_TMC.ST_Rec", diff.GetProperty("different")[0].GetString());
    }

    [Fact]
    public void List_SameTemplate_SaysSo()
    {
        WriteSchema(SchemaText(Definitions));
        WriteTemplate(SchemaText(Definitions));

        var (summary, _) = new HmiCoordinator(Session()).ListProjectDatatypes(null);

        Assert.Contains("tchmi.project.Schema.tpl.json has the same definitions", summary);
    }

    [Fact]
    public void List_UnreadableSchema_FailsClosed()
    {
        WriteSchema("{ not json");

        var (summary, json) = new HmiCoordinator(Session()).ListProjectDatatypes(null);

        Assert.StartsWith("[Error]", summary);
        Assert.Equal("{}", json);
    }

    [Fact]
    public void List_MissingSchema_IsAnError()
    {
        var (summary, _) = new HmiCoordinator(Session()).ListProjectDatatypes(null);
        Assert.StartsWith("[Error]", summary);
        Assert.Contains("does not exist", summary);
    }

    [Fact]
    public void List_EmptyDefinitions_IsNoDatatype()
    {
        WriteSchema("""{ "$schema": "http://json-schema.org/draft-04/schema#", "definitions": { } }""");

        var (summary, _) = new HmiCoordinator(Session()).ListProjectDatatypes(null);

        Assert.Contains("has no project datatype", summary);
    }

    [Fact]
    public void List_NoSolution_IsAnError()
    {
        var fake = Session();
        fake.SolutionOpen = false;
        var (summary, _) = new HmiCoordinator(fake).ListProjectDatatypes(null);
        Assert.StartsWith("[Error]", summary);
    }

    [Fact]
    public void List_NoHmiProject_IsAnError()
    {
        var fake = Session();
        fake.HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1, [], null);
        var (summary, _) = new HmiCoordinator(fake).ListProjectDatatypes(null);
        Assert.Contains("contains no TwinCAT HMI project", summary);
    }
}
