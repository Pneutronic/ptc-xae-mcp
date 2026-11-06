using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;
using static PtcXaeMcp.Tests.GlobalDataTypesTests;

namespace PtcXaeMcp.Tests;

/// <summary>
/// twincat_plc_add_global_data_type for STRUCT, ENUM and ALIAS types with the fake session: dependencies taken along,
/// collisions refused before anything changes, the read-back of every pinned type, and a pinned struct kept when an event
/// class of the same ExternalTypes.tmc is updated.
/// </summary>
public sealed class GlobalDataTypeCoordinatorTests
{
    private const string Plcproj = @"C:\sln\Plc\Plc.plcproj";
    private const string ExternalTypes = @"C:\sln\Plc\ExternalTypes.tmc";
    private const string PlcprojText =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
        "  <ProjectExtensions />\r\n</Project>";

    private static FakeTwinCatSession OpenFake()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.TypeSystem.Add("<DataType><Name GUID=\"" + IntGuid + "\" TcBaseType=\"true\">INT</Name><BitSize>16</BitSize></DataType>");
        fake.TypeSystem.Add(BuiltInStruct);
        fake.TypeSystem.Add(LibraryFb);
        fake.TypeSystem.Add(PlcDut);
        fake.TypeSystem.Add(Enum());
        fake.TypeSystem.Add(Inner());
        fake.TypeSystem.Add(Struct());
        fake.TypeSystem.Add(Alias());
        fake.EventPlcs.Add(("Plc", Plcproj));
        fake.Files[Plcproj] = PlcprojText;
        return fake;
    }

    private static EventClassCoordinator Coordinator(FakeTwinCatSession fake) => new(fake);

    private static bool Changed(FakeTwinCatSession fake) =>
        fake.CallLog.Any(c => c == "LogoutPlc()" || c.StartsWith("EditFiles", StringComparison.Ordinal) || c == "SaveAll()");

    private static List<string> Pinned(FakeTwinCatSession fake) =>
        GlobalDataTypeXml.DataTypesInTmc(fake.Files.GetValueOrDefault(ExternalTypes)).Select(t => t.Name).ToList();

    [Fact]
    public void Struct_IsPinnedWithItsEnumAndInnerStruct_EachReadBack()
    {
        var fake = OpenFake();

        string result = Coordinator(fake).AddGlobalDataType("st_rec", null);

        result.Should().StartWith($"Pinned STRUCT 'ST_Rec' {StructGuid} into PLC 'Plc' (\"Add Global Data Type\")")
            .And.Contain("the tree shows ExternalTypes.tmc^Global.ST_Rec.");
        result.Should().Contain($"Pinned its dependency ENUM 'E_Mode' {EnumGuid} into PLC 'Plc' too; the tree shows ExternalTypes.tmc^Global.E_Mode.");
        result.Should().Contain("Pinned its dependency STRUCT 'ST_Inner'");
        result.Should().Contain($"Dependencies taken along: ENUM 'E_Mode' {EnumGuid}, STRUCT 'ST_Inner' {InnerGuid}.");
        result.Should().Contain("carries the pinned types").And.NotContain("[Error]").And.NotContain("[Warn]");
        fake.CallLog.Should().ContainInOrder("ReadTypeSystemXml()", "ReadPlcExternalTypes(objects)", "LogoutPlc()",
            "EditFilesWithSolutionClosed(ExternalTypes.tmc,Plc.plcproj)", "ReadPlcExternalTypes()");
        Pinned(fake).Should().Equal("E_Mode", "ST_Inner", "ST_Rec");
        fake.Files[Plcproj].Should().Contain("<None Include=\"ExternalTypes.tmc\">");
    }

    [Fact]
    public void Alias_TakesTheWholeChainAlong()
    {
        var fake = OpenFake();

        string result = Coordinator(fake).AddGlobalDataType("T_Rec", null);

        result.Should().StartWith("Pinned ALIAS 'T_Rec'");
        Pinned(fake).Should().Equal("E_Mode", "ST_Inner", "ST_Rec", "T_Rec");
    }

    [Fact]
    public void Enum_AloneHasNoDependencies()
    {
        var fake = OpenFake();

        string result = Coordinator(fake).AddGlobalDataType("E_Mode", null);

        result.Should().StartWith("Pinned ENUM 'E_Mode'").And.NotContain("Dependencies taken along");
        Pinned(fake).Should().Equal("E_Mode");
    }

    [Fact]
    public void PinningTheSameTypeAgain_IsANoOp_ButAChangedDependencyIsRefreshed()
    {
        var fake = OpenFake();
        Coordinator(fake).AddGlobalDataType("ST_Rec", null);
        fake.CallLog.Clear();

        Coordinator(fake).AddGlobalDataType("ST_Rec", null).Should().Contain("is already pinned in PLC 'Plc' and up to date")
            .And.Contain("with its dependencies ENUM 'E_Mode'");
        Changed(fake).Should().BeFalse();

        // The enum gets a value in the type system: the pin is out of date and is written again.
        string changed = Enum(values: "<EnumInfo><Text>Off</Text><Enum>0</Enum></EnumInfo><EnumInfo><Text>Auto</Text><Enum>2</Enum></EnumInfo>");
        fake.TypeSystem[fake.TypeSystem.FindIndex(x => x.Contains(">E_Mode<", StringComparison.Ordinal))] = changed;

        string result = Coordinator(fake).AddGlobalDataType("ST_Rec", null);

        result.Should().StartWith("Pinned STRUCT 'ST_Rec'").And.NotContain("[Warn]");
        Pinned(fake).Should().Equal("E_Mode", "ST_Inner", "ST_Rec");
        fake.Files[ExternalTypes].Should().Contain("Auto");
    }

    [Fact]
    public void ADutOfTheSameName_IsRefused()
    {
        var fake = OpenFake();
        fake.PlcTypeObjects.Add(("Plc", new PlcTypeObject("E_Mode", "DUT", "TIPC^Plc^Plc Project^DUTs^E_Mode")));

        string result = Coordinator(fake).AddGlobalDataType("ST_Rec", null);

        result.Should().StartWith("[Error] STRUCT 'ST_Rec'").And.Contain("cannot be pinned into PLC 'Plc'")
            .And.Contain("the PLC project has a DUT 'E_Mode' (TIPC^Plc^Plc Project^DUTs^E_Mode) of the same name as ENUM 'E_Mode'")
            .And.EndWith("Nothing was changed.");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void AnEventClassStructDut_IsRefused()
    {
        var fake = OpenFake();
        fake.TypeSystem.Add("<DataType><Name GUID=\"{00000000-0000-0000-0000-0000000000D1}\" PersistentType=\"true\">MyEvents</Name>" +
                            "<EventId><Name Id=\"1\">A</Name><Severity>Info</Severity></EventId></DataType>");
        fake.PlcTypeObjects.Add(("Plc", new PlcTypeObject("ST_MyEvents", "DUT", "TIPC^Plc^Plc Project^ST_MyEvents")));

        Coordinator(fake).AddGlobalDataType("MyEvents", null).Should().StartWith("[Error] Event class 'MyEvents'")
            .And.Contain("DUT 'ST_MyEvents'");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void APinnedTypeOfTheSameNameWithAnotherGuid_IsRefused()
    {
        var fake = OpenFake();
        fake.Files[ExternalTypes] = EventClassXml.UpsertIntoTmc(null, Enum(guid: "{11111111-0000-0000-0000-000000000001}"));

        string result = Coordinator(fake).AddGlobalDataType("ST_Rec", null);

        result.Should().StartWith("[Error] STRUCT 'ST_Rec'")
            .And.Contain("its ExternalTypes.tmc already holds a different ENUM 'E_Mode' {11111111-0000-0000-0000-000000000001}")
            .And.Contain("same GUID");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void APinnedTypeWithTheSameGuidUnderAnotherName_IsRefused()
    {
        var fake = OpenFake();
        fake.Files[ExternalTypes] = EventClassXml.UpsertIntoTmc(null, Enum(name: "E_OldMode"));

        Coordinator(fake).AddGlobalDataType("E_Mode", null).Should().StartWith("[Error] ENUM 'E_Mode'")
            .And.Contain($"already holds ENUM 'E_OldMode' {EnumGuid} with the GUID of ENUM 'E_Mode'");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void TypesThatAreNotTheProjects_AreRefused()
    {
        var fake = OpenFake();

        Coordinator(fake).AddGlobalDataType("PlcAppSystemInfo", null).Should().StartWith("[Error] 'PlcAppSystemInfo' is a built-in TwinCAT STRUCT");
        Coordinator(fake).AddGlobalDataType("TON", null).Should().StartWith("[Error] 'TON' (FUNCTION_BLOCK) comes from the library 'Tc2_Standard'");
        Coordinator(fake).AddGlobalDataType("E_FixtureMode", null).Should().StartWith("[Error] 'E_FixtureMode' (ENUM) comes from a PLC project")
            .And.Contain("Data types of the project: E_Mode (ENUM), ST_Inner (STRUCT), ST_Rec (STRUCT), T_Rec (ALIAS)");
        Coordinator(fake).AddGlobalDataType("Nope", null).Should().StartWith("[Error] No data type 'Nope' in the type system")
            .And.Contain("ST_Rec (STRUCT)");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void ALibraryTypeInsideTheStruct_IsNotTakenAlong_AndSaysSo()
    {
        var fake = OpenFake();
        fake.TypeSystem.Add("<DataType><Name GUID=\"{00000000-0000-0000-0000-0000000000F1}\" PersistentType=\"true\">ST_Timed</Name><BitSize>256</BitSize>" +
                            "<SubItem><Name>tWait</Name><Type Namespace=\"Tc2_Standard\">TON</Type><BitSize>256</BitSize><BitOffs>0</BitOffs></SubItem></DataType>");

        string result = Coordinator(fake).AddGlobalDataType("ST_Timed", null);

        result.Should().StartWith("Pinned STRUCT 'ST_Timed'")
            .And.Contain("[Warn] 'TON' (used by 'ST_Timed') is not taken along: it comes from the library 'Tc2_Standard'");
        Pinned(fake).Should().Equal("ST_Timed");
    }

    [Fact]
    public void ADependencyTheXaeDoesNotShowAfterTheReopen_IsAnError()
    {
        var fake = OpenFake();
        fake.ReopenHidesNodes.Add("E_Mode");

        string result = Coordinator(fake).AddGlobalDataType("ST_Rec", null);

        result.Should().Contain("[Error] PLC 'Plc': ExternalTypes.tmc was written, but after the reopen the XAE shows no ExternalTypes.tmc node for ENUM 'E_Mode'");
        result.Should().Contain("Pinned STRUCT 'ST_Rec'");
    }

    [Fact]
    public void UpdatingAPinnedEventClass_WritesTheOtherPinsBack()
    {
        var fake = OpenFake();
        Coordinator(fake).Create("MyEvents", "My events", [new(1, "Started")]).Should().StartWith("Created");
        Coordinator(fake).AddGlobalDataType("ST_Rec", null).Should().StartWith("Pinned STRUCT");
        Coordinator(fake).AddGlobalDataType("MyEvents", null).Should().StartWith("Pinned event class 'MyEvents'");
        Pinned(fake).Should().Equal("E_Mode", "ST_Inner", "ST_Rec", "MyEvents");

        // The fake XAE empties ExternalTypes.tmc on the save after DeleteType (the worst case).
        string result = Coordinator(fake).Update("MyEvents", "renamed", null);

        result.Should().Contain("Pinned event class 'MyEvents'").And.Contain("the update dropped the pin").And.NotContain("[Error]");
        Pinned(fake).Should().BeEquivalentTo(["E_Mode", "ST_Inner", "ST_Rec", "MyEvents"]);
        EventClassXml.EventClassesInTmc(fake.Files[ExternalTypes]).Single().DisplayText.Should().Be("renamed");
    }

    [Fact]
    public void Collisions_ListsEveryOne()
    {
        var types = new[] { Enum(), Inner(), Struct() }.Select(x => GlobalDataTypeXml.Parse(x)!).ToList();
        var closure = GlobalDataTypeClosure.Resolve(types[2], types);
        var plc = new PlcExternalTypesInfo("Plc", Plcproj, [], EventClassXml.UpsertIntoTmc(null, Inner("ST_Other")),
            [new PlcTypeObject("ST_Rec", "DUT", "p1"), new PlcTypeObject("E_Mode", "POU", "p2"), new PlcTypeObject("FB_Free", "POU", "p3")]);

        var lines = EventClassCoordinator.Collisions(closure, plc);

        lines.Should().HaveCount(3);
        lines.Should().Contain(l => l.Contains("POU 'E_Mode'")).And.Contain(l => l.Contains("DUT 'ST_Rec'"))
            .And.Contain(l => l.Contains("STRUCT 'ST_Other'") && l.Contains("with the GUID of STRUCT 'ST_Inner'"));
    }
}
