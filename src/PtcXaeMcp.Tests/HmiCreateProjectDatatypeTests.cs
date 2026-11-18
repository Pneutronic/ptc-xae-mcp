using System.Text.Json;
using System.Text.Json.Nodes;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Project datatypes from a remote type of an attached TMC file. Invariants: the generated shapes are the ones
/// TE2000 writes (struct, enum with options, array with startOffset, sub-types as their own definitions with
/// project refs); nothing is written unless the engineer confirmed; a duplicate (by name or by content) or a
/// conflicting sub-type refuses before anything is written; success only after both files read back.
/// </summary>
[Collection(PathPolicyEnvironment.Name)]
public class HmiCreateProjectDatatypeTests : IDisposable
{
    private const string Project = "HMI_Fixture";
    private const string Ns = "PLC_TMC";

    // Data types as the neutral fixture's .tmc carries them, plus an array member with LBound 1, an EXTENDS
    // struct, and the kinds that must be refused.
    internal const string Tmc = """
        <?xml version="1.0" encoding="utf-8"?>
        <TcModuleClass GeneratedBy="TwinCAT XAE Plc">
          <DataTypes>
            <DataType><Name>E_FixtureMode</Name><BitSize>16</BitSize><BaseType>INT</BaseType>
              <EnumInfo><Text>Idle</Text><Enum>0</Enum></EnumInfo><EnumInfo><Text>Running</Text><Enum>1</Enum></EnumInfo>
              <EnumInfo><Text>Done</Text><Enum>2</Enum></EnumInfo>
              <Properties><Property><Name>qualified_only</Name></Property><Property><Name>strict</Name></Property></Properties>
            </DataType>
            <DataType><Name>ST_FixtureRecord</Name><BitSize>512</BitSize>
              <SubItem><Name>nId</Name><Type>UDINT</Type><Comment> PLC index </Comment></SubItem>
              <SubItem><Name>fValue</Name><Type>LREAL</Type></SubItem>
              <SubItem><Name>sLabel</Name><Type>STRING(40)</Type></SubItem>
              <SubItem><Name>sText</Name><Type>STRING(80)</Type></SubItem>
              <SubItem><Name>eMode</Name><Type>E_FixtureMode</Type></SubItem>
              <SubItem><Name>sName</Name><Type>T_FixtureName</Type></SubItem>
            </DataType>
            <DataType><Name>T_FixtureName</Name><BitSize>328</BitSize><BaseType>STRING(40)</BaseType></DataType>
            <DataType><Name>ST_Batch</Name>
              <SubItem><Name>aData</Name><Type>ST_FixtureRecord</Type><ArrayInfo><LBound>1</LBound><Elements>11</Elements></ArrayInfo></SubItem>
              <SubItem><Name>aFlags</Name><Type>BOOL</Type><ArrayInfo><LBound>0</LBound><Elements>4</Elements></ArrayInfo></SubItem>
              <SubItem><Name>tElapsed</Name><Type>TIME</Type></SubItem>
            </DataType>
            <DataType><Name>ST_Derived</Name><ExtendsType>ST_Base</ExtendsType><SubItem><Name>nExtra</Name><Type>INT</Type></SubItem></DataType>
            <DataType><Name>ST_Base</Name><SubItem><Name>nBase</Name><Type>DINT</Type></SubItem></DataType>
            <DataType><Name>ST_Matrix</Name><SubItem><Name>a</Name><Type>INT</Type><ArrayInfo><LBound>0</LBound><Elements>2</Elements></ArrayInfo><ArrayInfo><LBound>0</LBound><Elements>2</Elements></ArrayInfo></SubItem></DataType>
            <DataType><Name>ST_WithPointer</Name><SubItem><Name>p</Name><Type>POINTER TO INT</Type></SubItem></DataType>
            <DataType><Name>ST_WithFb</Name><SubItem><Name>fb</Name><Type>FB_Worker</Type></SubItem></DataType>
            <DataType><Name>FB_Worker</Name><SubItem><Name>bBusy</Name><Type>BOOL</Type></SubItem><Method><Name>Run</Name></Method></DataType>
          </DataTypes>
        </TcModuleClass>
        """;

    private readonly string _root;
    private readonly string _projectDir;
    private readonly string _tmcPath;

    public HmiCreateProjectDatatypeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ptc-cpdt-{Guid.NewGuid():N}");
        string sln = Path.Combine(_root, "sln");
        _projectDir = Path.Combine(sln, Project);
        Directory.CreateDirectory(Path.Combine(_projectDir, "Properties"));
        _tmcPath = Path.Combine(sln, "FixturePlc", "FixturePlc.tmc");
        Directory.CreateDirectory(Path.GetDirectoryName(_tmcPath)!);
        File.WriteAllText(_tmcPath, Tmc);
        WriteProjectFile(attached: true);
        WriteSchema("""{ "$schema": "http://json-schema.org/draft-04/schema#", "definitions": { } }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void WriteProjectFile(bool attached)
    {
        string files = attached
            ? $"<HmiTmcFiles>[{{\"FileName\":{JsonSerializer.Serialize(_tmcPath)},\"RootNamespace\":\"{Ns}\",\"IsFromSolution\":false}}]</HmiTmcFiles>"
            : "";
        File.WriteAllText(Path.Combine(_projectDir, Project + ".hmiproj"),
            $"""<?xml version="1.0" encoding="utf-8"?><Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup>{files}</PropertyGroup></Project>""");
    }

    private string SchemaPath => HmiProjectDatatypes.SchemaPath(_projectDir);
    private string TemplatePath => HmiProjectDatatypes.TemplatePath(_projectDir);
    private void WriteSchema(string text) => File.WriteAllText(SchemaPath, text);

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

    private static Dictionary<string, JsonNode> Build(string type)
    {
        var (result, error) = HmiRemoteTypes.Build(Tmc, Ns, type);
        Assert.Null(error);
        return result!.Definitions.ToDictionary(d => d.Key, d => d.Value);
    }

    private JsonObject SchemaDefinitions(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!["definitions"]!.AsObject();

    // ── the generated shapes ────────────────────────────────────────────────

    [Fact]
    public void Struct_WithEnumField_CreatesBothWithProjectRefs()
    {
        var (result, _) = HmiRemoteTypes.Build(Tmc, Ns, "ST_FixtureRecord");

        Assert.Equal("PLC_TMC.ST_FixtureRecord", result!.RootName);
        Assert.Equal(["PLC_TMC.ST_FixtureRecord", "PLC_TMC.E_FixtureMode"], result.Definitions.Select(d => d.Key));
        JsonNode props = result.Definitions[0].Value["properties"]!;
        Assert.Equal("object", (string?)result.Definitions[0].Value["type"]);
        Assert.Equal("tchmi:general#/definitions/UDINT", (string?)props["nId"]!["allOf"]![0]!["$ref"]);
        Assert.Equal("PLC index", (string?)props["nId"]!["allOf"]![1]!["comment"]);
        Assert.Equal("tchmi:general#/definitions/LREAL", (string?)props["fValue"]!["$ref"]);
        Assert.Equal("tchmi:general#/definitions/STRING%2880%29", (string?)props["sText"]!["$ref"]);
        Assert.Equal(40, (int?)props["sLabel"]!["maxLength"]);
        Assert.Equal("tchmi:project#/definitions/PLC_TMC.E_FixtureMode", (string?)props["eMode"]!["$ref"]);
        Assert.Equal(40, (int?)props["sName"]!["maxLength"]); // alias resolved to its base, no own definition
    }

    [Fact]
    public void Enum_IsInt16WithOptionsAndAttributes()
    {
        JsonNode e = Build("E_FixtureMode")["PLC_TMC.E_FixtureMode"];

        JsonNode head = e["allOf"]![0]!;
        Assert.Equal("", (string?)head["qualified_only"]);
        Assert.Equal("", (string?)head["strict"]);
        Assert.Equal(["0", "1", "2"], head["enum"]!.AsArray().Select(v => (string?)v));
        Assert.Equal("Running", (string?)head["options"]![1]!["label"]);
        Assert.Equal(1L, (long?)head["options"]![1]!["value"]);
        Assert.Equal("tchmi:general#/definitions/INT16", (string?)e["allOf"]![1]!["$ref"]);
    }

    [Fact]
    public void ArrayMember_WithLBound1_GetsItsOwnTypeWithStartOffset()
    {
        var defs = Build("ST_Batch");

        JsonNode arr = defs["PLC_TMC.ARRAY_1..11_OF-ST_FixtureRecord"];
        Assert.Equal("array", (string?)arr["type"]);
        Assert.Equal(11, (int?)arr["minItems"]);
        Assert.Equal(11, (int?)arr["maxItems"]);
        Assert.Equal(1, (int?)arr["startOffset"]);
        Assert.Equal("tchmi:project#/definitions/PLC_TMC.ST_FixtureRecord", (string?)arr["items"]!["$ref"]);
        Assert.Equal("tchmi:general#/definitions/BOOL", (string?)defs["PLC_TMC.ARRAY_0..3_OF-BOOL"]["items"]!["$ref"]);
        Assert.Equal(0, (int?)defs["PLC_TMC.ARRAY_0..3_OF-BOOL"]["startOffset"]);
        Assert.Contains("PLC_TMC.E_FixtureMode", defs.Keys);
        Assert.Equal("tchmi:general#/definitions/TIME", (string?)defs["PLC_TMC.ST_Batch"]["properties"]!["tElapsed"]!["$ref"]);
        Assert.Equal("tchmi:project#/definitions/PLC_TMC.ARRAY_1..11_OF-ST_FixtureRecord",
            (string?)defs["PLC_TMC.ST_Batch"]["properties"]!["aData"]!["$ref"]);
    }

    [Fact]
    public void GeneratedDefinitions_HaveNoDanglingProjectRefs()
    {
        var obj = new JsonObject();
        foreach (var (k, v) in Build("ST_Batch")) obj[k] = v.DeepClone();
        Assert.All(HmiProjectDatatypes.Describe(obj), d => Assert.Empty(d.DanglingRefs));
    }

    [Fact]
    public void Extends_PutsTheBaseMembersFirst() =>
        Assert.Equal(["nBase", "nExtra"],
            Build("ST_Derived")["PLC_TMC.ST_Derived"]["properties"]!.AsObject().Select(p => p.Key));

    [Theory]
    [InlineData("ST_Matrix", "2-dimensional array")]
    [InlineData("ST_WithPointer", "pointer/reference")]
    [InlineData("ST_WithFb", "function block")]
    [InlineData("FB_Worker", "function block")]
    [InlineData("T_FixtureName", "alias")]
    [InlineData("ST_Missing", "has no data type")]
    public void Unsupported_IsRefusedWithTheReason(string type, string reason)
    {
        var (result, error) = HmiRemoteTypes.Build(Tmc, Ns, type);
        Assert.Null(result);
        Assert.Contains(reason, error);
    }

    // ── duplicate rules ─────────────────────────────────────────────────────

    [Fact]
    public void Plan_SameNameExists_IsRefused()
    {
        var (gen, _) = HmiRemoteTypes.Build(Tmc, Ns, "ST_FixtureRecord");
        var existing = new JsonObject { ["PLC_TMC.ST_FixtureRecord"] = new JsonObject() };
        Assert.Contains("already exists", HmiProjectDatatypes.PlanCreate(existing, gen!).Error);
    }

    [Fact]
    public void Plan_SameContentUnderAnotherName_IsRefused()
    {
        var (gen, _) = HmiRemoteTypes.Build(Tmc, Ns, "ST_FixtureRecord");
        var existing = new JsonObject { ["MyRecord"] = gen!.Definitions[0].Value.DeepClone() };
        Assert.Contains("already exists as 'MyRecord'", HmiProjectDatatypes.PlanCreate(existing, gen).Error);
    }

    [Fact]
    public void Plan_IdenticalSubTypeIsReused_DifferentIsRefused()
    {
        var (gen, _) = HmiRemoteTypes.Build(Tmc, Ns, "ST_FixtureRecord");
        var same = new JsonObject { ["PLC_TMC.E_FixtureMode"] = gen!.Definitions[1].Value.DeepClone() };
        var plan = HmiProjectDatatypes.PlanCreate(same, gen);
        Assert.Null(plan.Error);
        Assert.Equal(["PLC_TMC.E_FixtureMode"], plan.Reused);
        Assert.Equal(["PLC_TMC.ST_FixtureRecord"], plan.ToAdd.Select(d => d.Key));

        var other = new JsonObject { ["PLC_TMC.E_FixtureMode"] = new JsonObject { ["type"] = "integer" } };
        Assert.Contains("different content", HmiProjectDatatypes.PlanCreate(other, gen).Error);
    }

    [Fact]
    public void AddDefinitions_KeepsOtherContentAndUsesTabs()
    {
        string text = """{ "$schema": "x", "definitions": { "ucLamp": { "type": "object" } } }""" + "\n";
        string? result = HmiProjectDatatypes.AddDefinitions(text, [new("PLC_TMC.E_A", new JsonObject { ["type"] = "integer" })]);

        JsonNode parsed = JsonNode.Parse(result!)!;
        Assert.Equal("x", (string?)parsed["$schema"]);
        Assert.NotNull(parsed["definitions"]!["ucLamp"]);
        Assert.NotNull(parsed["definitions"]!["PLC_TMC.E_A"]);
        Assert.Contains("\n\t\"definitions\"", result.Replace("\r", ""));
        Assert.Null(HmiProjectDatatypes.AddDefinitions("[]", []));
    }

    // ── the tool ────────────────────────────────────────────────────────────

    [Fact]
    public void Create_Confirmed_WritesReadsBackAndRefreshes()
    {
        var fake = Session();
        var popup = new FakeHmiConfirmer();

        var (summary, json) = new HmiCoordinator(fake, popup).CreateProjectDatatype(null, "PLC_TMC.ST_FixtureRecord", 120);

        Assert.DoesNotContain("[Error]", summary);
        Assert.Contains("verified by reading tchmi.project.Schema.json back", summary);
        Assert.Contains("tchmi:project#/definitions/PLC_TMC.ST_FixtureRecord", summary);
        Assert.Contains($"RefreshHmiDefinitions({Project}, project)", fake.CallLog);
        var defs = SchemaDefinitions(SchemaPath);
        Assert.Equal(["PLC_TMC.ST_FixtureRecord", "PLC_TMC.E_FixtureMode"], defs.Select(p => p.Key));
        var (request, _) = Assert.Single(popup.Requests);
        Assert.Equal(HmiWriteKind.CreateProjectDatatype, request.Kind);
        Assert.False(request.WritesToPlc);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("refreshed").GetBoolean());
        Assert.Equal(SchemaPath, doc.RootElement.GetProperty("changedFiles")[0].GetString());
    }

    [Fact]
    public void Create_TemplatePresent_WritesBoth()
    {
        File.WriteAllText(TemplatePath, """{ "definitions": { } }""");

        var (summary, json) = new HmiCoordinator(Session(), new FakeHmiConfirmer()).CreateProjectDatatype(null, "PLC_TMC.E_FixtureMode", 120);

        Assert.DoesNotContain("[Error]", summary);
        Assert.True(SchemaDefinitions(TemplatePath).ContainsKey("PLC_TMC.E_FixtureMode"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("changedFiles").GetArrayLength());
    }

    [Fact]
    public void Create_ConflictOnlyInTemplate_WritesNothing()
    {
        File.WriteAllText(TemplatePath, """{ "definitions": { "PLC_TMC.E_FixtureMode": { "type": "integer" } } }""");
        string before = File.ReadAllText(SchemaPath);

        var (summary, _) = new HmiCoordinator(Session(), new FakeHmiConfirmer()).CreateProjectDatatype(null, "PLC_TMC.ST_FixtureRecord", 120);

        Assert.StartsWith("[Error] tchmi.project.Schema.tpl.json:", summary);
        Assert.Equal(before, File.ReadAllText(SchemaPath));
    }

    [Fact]
    public void Create_Rejected_WritesNothing()
    {
        string before = File.ReadAllText(SchemaPath);
        var fake = Session();

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer(HmiConfirmOutcome.Rejected)).CreateProjectDatatype(null, "PLC_TMC.ST_FixtureRecord", 120);

        Assert.StartsWith("[Error] Not confirmed", summary);
        Assert.Equal(before, File.ReadAllText(SchemaPath));
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("RefreshHmiDefinitions", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_TwiceIsRefusedTheSecondTime()
    {
        var c = new HmiCoordinator(Session(), new FakeHmiConfirmer());
        Assert.DoesNotContain("[Error]", c.CreateProjectDatatype(null, "PLC_TMC.ST_FixtureRecord", 120).Summary);
        var (summary, _) = c.CreateProjectDatatype(null, "PLC_TMC.ST_FixtureRecord", 120);
        Assert.Contains("already exists", summary);
    }

    [Fact]
    public void Create_SecondStructSharingTheEnum_ReusesIt()
    {
        var c = new HmiCoordinator(Session(), new FakeHmiConfirmer());
        c.CreateProjectDatatype(null, "PLC_TMC.E_FixtureMode", 120);

        var (summary, json) = c.CreateProjectDatatype(null, "PLC_TMC.ST_FixtureRecord", 120);

        Assert.DoesNotContain("[Error]", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("PLC_TMC.E_FixtureMode", doc.RootElement.GetProperty("reused")[0].GetString());
    }

    [Fact]
    public void Create_NamespaceNotAttached_NamesTheAttachedOnes()
    {
        var (summary, _) = new HmiCoordinator(Session(), new FakeHmiConfirmer()).CreateProjectDatatype(null, "PLC9.ST_FixtureRecord", 120);
        Assert.StartsWith("[Error] 'PLC9' is not a TMC file attached", summary);
        Assert.Contains("attached: PLC_TMC", summary);
    }

    [Fact]
    public void Create_TypeNotInTheTmc_WritesNothing()
    {
        var fake = Session();
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).CreateProjectDatatype(null, "PLC_TMC.ST_Nope", 120);

        Assert.Contains("has no data type 'ST_Nope'", summary);
        Assert.Empty(popup.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ST_Only")]
    [InlineData("PLC_TMC.")]
    [InlineData(".ST_X")]
    public void Create_MalformedRemoteType_IsRefused(string? remoteType) =>
        Assert.Contains("remoteType is required",
            new HmiCoordinator(Session(), new FakeHmiConfirmer()).CreateProjectDatatype(null, remoteType, 120).Summary);

    [Fact]
    public void Create_RefreshFails_IsAWarningNotAFailure()
    {
        var fake = Session();
        fake.RefreshHmiDefinitionsResult = new HmiOperationResult(false, "INVALID_SYMBOL");

        var (summary, json) = new HmiCoordinator(fake, new FakeHmiConfirmer()).CreateProjectDatatype(null, "PLC_TMC.E_FixtureMode", 120);

        Assert.DoesNotContain("[Error]", summary);
        Assert.Contains("WARNING: RefreshDefinitions", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("refreshed").GetBoolean());
    }

    [Fact]
    public void Create_UnreadableSchema_WritesNothing()
    {
        WriteSchema("{ broken");
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(Session(), popup).CreateProjectDatatype(null, "PLC_TMC.E_FixtureMode", 120);

        Assert.StartsWith("[Error]", summary);
        Assert.Empty(popup.Requests);
        Assert.Equal("{ broken", File.ReadAllText(SchemaPath));
    }
}
