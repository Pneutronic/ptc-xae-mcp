using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// The type-system DataType XML for "Add Global Data Type" of any type: kinds, references, the dependency walk, the
/// comparison of two definitions and ExternalTypes.tmc with several types.
/// </summary>
public sealed class GlobalDataTypesTests
{
    internal const string IntGuid = "{18071995-0000-0000-0000-000000000006}";
    internal const string UdintGuid = "{18071995-0000-0000-0000-000000000008}";
    internal const string EnumGuid = "{00000000-0000-0000-0000-0000000000E1}";
    internal const string InnerGuid = "{00000000-0000-0000-0000-0000000000A1}";
    internal const string StructGuid = "{00000000-0000-0000-0000-0000000000B1}";
    internal const string AliasGuid = "{00000000-0000-0000-0000-0000000000C1}";

    internal static string Enum(string name = "E_Mode", string guid = EnumGuid, string values = "<EnumInfo><Text>Off</Text><Enum>0</Enum></EnumInfo><EnumInfo><Text>On</Text><Enum>1</Enum></EnumInfo>") =>
        $"<DataType><Name GUID=\"{guid}\" PersistentType=\"true\">{name}</Name><BitSize>16</BitSize>" +
        $"<BaseType GUID=\"{IntGuid}\">INT</BaseType>{values}</DataType>";

    internal static string Inner(string name = "ST_Inner") =>
        $"<DataType><Name GUID=\"{InnerGuid}\" PersistentType=\"true\">{name}</Name><BitSize>32</BitSize>" +
        $"<SubItem><Name>nValue</Name><Type GUID=\"{UdintGuid}\">UDINT</Type><BitSize>32</BitSize><BitOffs>0</BitOffs></SubItem></DataType>";

    internal static string Struct(string name = "ST_Rec", string guid = StructGuid, string extra = "") =>
        $"<DataType><Name GUID=\"{guid}\" PersistentType=\"true\">{name}</Name><BitSize>96</BitSize>" +
        $"<SubItem><Name>eMode</Name><Type GUID=\"{EnumGuid}\">E_Mode</Type><BitSize>16</BitSize><BitOffs>0</BitOffs></SubItem>" +
        $"<SubItem><Name>stInner</Name><Type GUID=\"{InnerGuid}\">ST_Inner</Type><BitSize>32</BitSize><BitOffs>32</BitOffs></SubItem>" +
        $"<SubItem><Name>aModes</Name><Type GUID=\"{EnumGuid}\">E_Mode</Type><ArrayInfo><LBound>0</LBound><Elements>2</Elements></ArrayInfo><BitSize>32</BitSize><BitOffs>64</BitOffs></SubItem>" +
        $"{extra}</DataType>";

    internal static string Alias(string name = "T_Rec") =>
        $"<DataType><Name GUID=\"{AliasGuid}\" PersistentType=\"true\">{name}</Name><BitSize>96</BitSize>" +
        $"<BaseType GUID=\"{StructGuid}\">ST_Rec</BaseType></DataType>";

    internal const string BuiltInStruct =
        "<DataType><Name GUID=\"{D8713831-B59F-4201-84CC-04357A7EE4B7}\" TcBaseType=\"true\">PlcAppSystemInfo</Name><BitSize>32</BitSize>" +
        "<SubItem><Name>ObjId</Name><Type GUID=\"{18071995-0000-0000-0000-00000000000F}\">OTCID</Type><BitSize>32</BitSize><BitOffs>0</BitOffs></SubItem></DataType>";

    internal const string LibraryFb =
        "<DataType><Name Namespace=\"Tc2_Standard\">TON</Name><BitSize>256</BitSize><SubItem><Name>IN</Name><Type>BOOL</Type><BitSize>8</BitSize>" +
        "<BitOffs>64</BitOffs></SubItem><Properties><Property><Name>PouType</Name><Value>FunctionBlock</Value></Property></Properties></DataType>";

    internal const string PlcDut =
        "<DataType><Name>E_FixtureMode</Name><BitSize>16</BitSize><BaseType>INT</BaseType><EnumInfo><Text>Idle</Text><Enum>0</Enum></EnumInfo></DataType>";

    private static TypeSystemType P(string xml) => GlobalDataTypeXml.Parse(xml)!;

    private static List<TypeSystemType> All(params string[] xml) => xml.Select(P).ToList();

    [Fact]
    public void Parse_TellsKindOriginAndReferences()
    {
        var e = P(Enum());
        e.Kind.Should().Be("ENUM");
        e.Persistent.Should().BeTrue();
        e.Guid.Should().Be(EnumGuid);
        e.References.Should().ContainSingle().Which.Name.Should().Be("INT");

        var s = P(Struct());
        s.Kind.Should().Be("STRUCT");
        s.References.Select(r => r.Name).Should().Equal("E_Mode", "ST_Inner"); // E_Mode once although used twice
        s.Label.Should().Be($"STRUCT 'ST_Rec' {StructGuid}");

        P(Alias()).Kind.Should().Be("ALIAS");
        P(BuiltInStruct).BuiltIn.Should().BeTrue();
        var fb = P(LibraryFb);
        fb.Kind.Should().Be("FUNCTION_BLOCK");
        fb.Namespace.Should().Be("Tc2_Standard");
        fb.Origin.Should().Contain("library 'Tc2_Standard'");
        P(PlcDut).Origin.Should().Contain("PLC project");
        P("<DataType><Name>U_Bits</Name><BitSize>32</BitSize><SubItem><Name>a</Name><Type>DWORD</Type><BitSize>32</BitSize><BitOffs>0</BitOffs></SubItem>" +
          "<SubItem><Name>b</Name><Type>REAL</Type><BitSize>32</BitSize><BitOffs>0</BitOffs></SubItem></DataType>").Kind.Should().Be("UNION");
        P("<DataType><Name PersistentType=\"true\">Ev</Name><EventId><Name Id=\"1\">A</Name><Severity>Info</Severity></EventId></DataType>")
            .IsEventClass.Should().BeTrue();
    }

    [Fact]
    public void Parse_PicksTheNamedTypeOutOfAWrapper()
    {
        string wrapper = "<?xml version=\"1.0\"?><DataTypes>" + Enum() + Struct() + "</DataTypes>";
        GlobalDataTypeXml.Parse(wrapper, "st_rec")!.Name.Should().Be("ST_Rec");
        GlobalDataTypeXml.Parse(wrapper)!.Name.Should().Be("E_Mode");
        GlobalDataTypeXml.Parse("<not xml").Should().BeNull();
        GlobalDataTypeXml.Parse(null).Should().BeNull();
    }

    [Fact]
    public void Resolve_TakesEveryProjectTypeAlong_DependenciesFirst()
    {
        var all = All(BuiltInStruct, Alias(), Struct(), Inner(), Enum());

        var closure = GlobalDataTypeClosure.Resolve(all[1], all);

        closure.Types.Select(t => t.Name).Should().Equal("E_Mode", "ST_Inner", "ST_Rec", "T_Rec");
        closure.Target.Name.Should().Be("T_Rec");
        closure.Dependencies.Select(t => t.Name).Should().Equal("E_Mode", "ST_Inner", "ST_Rec");
        closure.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ResolvesByNameWithoutGuid_AndSkipsBuiltInsSilently()
    {
        string s = "<DataType><Name PersistentType=\"true\">ST_Plain</Name><BitSize>64</BitSize>" +
                   "<SubItem><Name>e</Name><Type>E_Mode</Type><BitSize>16</BitSize><BitOffs>0</BitOffs></SubItem>" +
                   "<SubItem><Name>i</Name><Type GUID=\"{D8713831-B59F-4201-84CC-04357A7EE4B7}\">PlcAppSystemInfo</Type><BitSize>32</BitSize><BitOffs>32</BitOffs></SubItem>" +
                   "<SubItem><Name>s</Name><Type>STRING(40)</Type><BitSize>328</BitSize><BitOffs>64</BitOffs></SubItem>" +
                   "<SubItem><Name>p</Name><Type PointerTo=\"1\">BYTE</Type><BitSize>64</BitSize><BitOffs>392</BitOffs></SubItem></DataType>";
        var all = All(s, Enum(), BuiltInStruct);

        var closure = GlobalDataTypeClosure.Resolve(all[0], all);

        closure.Types.Select(t => t.Name).Should().Equal("E_Mode", "ST_Plain");
        closure.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ListsLibraryPlcAndUnknownTypes_AsNotTakenAlong()
    {
        string s = "<DataType><Name PersistentType=\"true\">ST_Mixed</Name><BitSize>64</BitSize>" +
                   "<SubItem><Name>t</Name><Type Namespace=\"Tc2_Standard\">TON</Type><BitSize>256</BitSize><BitOffs>0</BitOffs></SubItem>" +
                   "<SubItem><Name>m</Name><Type>E_FixtureMode</Type><BitSize>16</BitSize><BitOffs>256</BitOffs></SubItem>" +
                   "<SubItem><Name>x</Name><Type>ST_Nowhere</Type><BitSize>16</BitSize><BitOffs>272</BitOffs></SubItem></DataType>";
        var all = All(s, LibraryFb, PlcDut);

        var closure = GlobalDataTypeClosure.Resolve(all[0], all);

        closure.Types.Select(t => t.Name).Should().Equal("ST_Mixed");
        closure.Skipped.Select(k => k.Reference.Name).Should().Equal("TON", "E_FixtureMode", "ST_Nowhere");
        closure.Skipped[0].Why.Should().Contain("library 'Tc2_Standard'");
        closure.Skipped[1].Why.Should().Contain("PLC project");
        closure.Skipped[2].Why.Should().Contain("not in the type system");
        closure.Skipped.Should().OnlyContain(k => k.UsedBy == "ST_Mixed");
    }

    [Fact]
    public void Resolve_SurvivesACycle()
    {
        string a = "<DataType><Name PersistentType=\"true\">ST_A</Name><BitSize>64</BitSize>" +
                   "<SubItem><Name>pB</Name><Type PointerTo=\"1\">ST_B</Type><BitSize>64</BitSize><BitOffs>0</BitOffs></SubItem></DataType>";
        string b = "<DataType><Name PersistentType=\"true\">ST_B</Name><BitSize>64</BitSize>" +
                   "<SubItem><Name>pA</Name><Type PointerTo=\"1\">ST_A</Type><BitSize>64</BitSize><BitOffs>0</BitOffs></SubItem>" +
                   "<SubItem><Name>pSelf</Name><Type PointerTo=\"1\">ST_B</Type><BitSize>64</BitSize><BitOffs>64</BitOffs></SubItem></DataType>";
        var all = All(a, b);

        GlobalDataTypeClosure.Resolve(all[0], all).Types.Select(t => t.Name).Should().Equal("ST_B", "ST_A");
    }

    [Fact]
    public void SameDefinition_IgnoresPersistentTypeOrderCdataAndWhitespace_ButNotContent()
    {
        string written = Enum();
        string rewritten = "<DataType>\r\n  <Name PersistentType=\"false\" GUID=\"{00000000-0000-0000-0000-0000000000e1}\">E_Mode</Name>\r\n" +
                           "  <BitSize> 16 </BitSize><BaseType GUID=\"" + IntGuid + "\">INT</BaseType>" +
                           "<EnumInfo><Text><![CDATA[Off]]></Text><Enum>0</Enum></EnumInfo><EnumInfo><Text>On</Text><Enum>1</Enum></EnumInfo></DataType>";
        GlobalDataTypeXml.SameDefinition(written, rewritten).Should().BeTrue();
        GlobalDataTypeXml.SameDefinition(written, Enum(values: "<EnumInfo><Text>Off</Text><Enum>0</Enum></EnumInfo>")).Should().BeFalse();
        GlobalDataTypeXml.SameDefinition(written, "<broken").Should().BeFalse();
    }

    [Fact]
    public void UpsertIntoTmc_SeveralTypes_AppendsInOrder_AndReplacesOnlyTheSameOne()
    {
        string tmc = null!;
        foreach (string t in new[] { Enum(), Inner(), Struct() })
            tmc = EventClassXml.UpsertIntoTmc(tmc, t);
        GlobalDataTypeXml.DataTypesInTmc(tmc).Select(t => t.Name).Should().Equal("E_Mode", "ST_Inner", "ST_Rec");

        string changedEnum = Enum(values: "<EnumInfo><Text>Off</Text><Enum>0</Enum></EnumInfo><EnumInfo><Text>Auto</Text><Enum>2</Enum></EnumInfo>");
        tmc = EventClassXml.UpsertIntoTmc(tmc, changedEnum);

        var types = GlobalDataTypeXml.DataTypesInTmc(tmc);
        types.Select(t => t.Name).Should().Equal("E_Mode", "ST_Inner", "ST_Rec");
        GlobalDataTypeXml.SameDefinition(types[0].DataTypeXml, changedEnum).Should().BeTrue();
        GlobalDataTypeXml.SameDefinition(types[1].DataTypeXml, Inner()).Should().BeTrue();
        GlobalDataTypeXml.SameDefinition(types[2].DataTypeXml, Struct()).Should().BeTrue();
    }

    [Theory]
    [InlineData("Global.ST_Rec", "ST_Rec", false, true)]
    [InlineData("ST_Rec", "st_rec", false, true)]
    [InlineData("Global.ST_Rec", "Rec", false, false)]
    [InlineData("Global.ST_MyEvents", "MyEvents", true, true)]
    [InlineData("Global.MyEvents", "MyEvents", true, true)]
    [InlineData("Global.E_Mode", "ST_Rec", false, false)]
    public void PinNodeIs_MatchesTheTypeName_AndTheEventClassStruct(string node, string name, bool eventClass, bool expected)
    {
        var type = new TypeSystemType(name, "", eventClass ? GlobalDataTypeXml.KindEventClass : "STRUCT", true, false, null, "", []);
        GlobalDataTypeXml.PinNodeIs(node, type).Should().Be(expected);
    }

    [Theory]
    [InlineData("INT", "", true)]
    [InlineData("STRING(40)", "", true)]
    [InlineData("WSTRING(10)", "", true)]
    [InlineData("ARRAY [0..1] OF INT", "", true)]
    [InlineData("OTCID", "{18071995-0000-0000-0000-00000000000F}", true)]
    [InlineData("Whatever", "{18071995-0000-0000-0000-000000000099}", true)]
    [InlineData("ST_Rec", "", false)]
    [InlineData("E_Mode", EnumGuid, false)]
    public void IsElementary(string name, string guid, bool expected) =>
        GlobalDataTypeXml.IsElementary(new TypeReference(name, guid, null)).Should().Be(expected);
}
