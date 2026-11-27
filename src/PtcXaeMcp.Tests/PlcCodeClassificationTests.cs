using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// The crux of the interface-members feature: an interface member must use the
// distinct CreateChild nSubType (610/612/654/655, Infosys 242732427), NOT the
// POU subtype (609/611/613/614). These pure decisions are the only part of the
// member-add path that can be unit-tested without a live XAE; the live shape is
// gated by the integration suite.
public class PlcCodeClassificationTests
{
    // ---- MemberSubType: interface members use 610/612/654/655 ----------------

    [Theory]
    [InlineData(PlcMemberKind.Method,   PlcItemType.InterfaceMethod)]
    [InlineData(PlcMemberKind.Property, PlcItemType.InterfaceProperty)]
    [InlineData(PlcMemberKind.Get,      PlcItemType.InterfaceGet)]
    [InlineData(PlcMemberKind.Set,      PlcItemType.InterfaceSet)]
    public void MemberSubType_InterfaceParent_UsesInterfaceSubtype(string kind, int expected)
        => Assert.Equal(expected, PlcCodeClassification.MemberSubType(parentIsInterface: true, kind));

    // ---- MemberSubType: POU members unchanged (no regression) ----------------

    [Theory]
    [InlineData(PlcMemberKind.Method,   PlcItemType.Method)]
    [InlineData(PlcMemberKind.Property, PlcItemType.Property)]
    [InlineData(PlcMemberKind.Get,      PlcItemType.PropertyGet)]
    [InlineData(PlcMemberKind.Set,      PlcItemType.PropertySet)]
    [InlineData(PlcMemberKind.Action,   PlcItemType.Action)]
    public void MemberSubType_PouParent_UsesPouSubtype(string kind, int expected)
        => Assert.Equal(expected, PlcCodeClassification.MemberSubType(parentIsInterface: false, kind));

    [Fact]
    public void MemberSubType_Action_IsPouSubtypeRegardlessOfParent()
        => Assert.Equal(PlcItemType.Action,
            PlcCodeClassification.MemberSubType(parentIsInterface: true, PlcMemberKind.Action));

    [Fact]
    public void MemberSubType_UnknownKind_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => PlcCodeClassification.MemberSubType(parentIsInterface: false, "WIDGET"));

    // ---- MemberKindValidOnInterface ------------------------------------------

    [Theory]
    [InlineData(PlcMemberKind.Method)]
    [InlineData(PlcMemberKind.Property)]
    [InlineData(PlcMemberKind.Get)]
    [InlineData(PlcMemberKind.Set)]
    public void MemberKindValidOnInterface_MethodAndProperty_Allowed(string kind)
        => Assert.True(PlcCodeClassification.MemberKindValidOnInterface(kind));

    [Fact]
    public void MemberKindValidOnInterface_Action_Rejected()
        => Assert.False(PlcCodeClassification.MemberKindValidOnInterface(PlcMemberKind.Action));

    // ---- PouKindOf: INTERFACE (618) is classified, not dropped ---------------

    [Fact]
    public void PouKindOf_Interface_ClassifiedAsInterface()
        => Assert.Equal(PouKind.Interface, PlcCodeClassification.PouKindOf(PlcItemType.Interface));

    [Theory]
    [InlineData(PlcItemType.PouProgram,       PouKind.Pou)]
    [InlineData(PlcItemType.PouFunctionBlock, PouKind.Pou)]
    [InlineData(PlcItemType.Gvl,              PouKind.Gvl)]
    [InlineData(PlcItemType.Dut,              PouKind.Dut)]
    [InlineData(PlcItemType.DutStruct,        PouKind.Dut)]
    [InlineData(PlcItemType.Folder,           PouKind.Folder)]
    public void PouKindOf_KnownTypes_Classified(int itemType, string expected)
        => Assert.Equal(expected, PlcCodeClassification.PouKindOf(itemType));

    [Fact]
    public void PouKindOf_References_NotClassified()
        => Assert.Null(PlcCodeClassification.PouKindOf(617)); // PLCLIBMAN — skipped by the walk

    // ---- IsDeclarationOnly ---------------------------------------------------

    [Fact]
    public void IsDeclarationOnly_Interface_True()
        => Assert.True(PlcCodeClassification.IsDeclarationOnly(PlcItemType.Interface));

    [Theory]
    [InlineData(PlcItemType.PouProgram)]
    [InlineData(PlcItemType.PouFunctionBlock)]
    public void IsDeclarationOnly_Pou_False(int itemType)
        => Assert.False(PlcCodeClassification.IsDeclarationOnly(itemType));

    // ---- PlcMemberKind.FromItemType maps both POU and interface subtypes -----

    [Theory]
    [InlineData(PlcItemType.InterfaceMethod,   PlcMemberKind.Method)]
    [InlineData(PlcItemType.InterfaceProperty, PlcMemberKind.Property)]
    [InlineData(PlcItemType.InterfaceGet,      PlcMemberKind.Get)]
    [InlineData(PlcItemType.InterfaceSet,      PlcMemberKind.Set)]
    [InlineData(PlcItemType.Method,            PlcMemberKind.Method)]
    [InlineData(PlcItemType.Property,          PlcMemberKind.Property)]
    public void FromItemType_InterfaceAndPouSubtypes_MapToSameKind(int itemType, string expected)
        => Assert.Equal(expected, PlcMemberKind.FromItemType(itemType));

    // ---- F2 (fix/pou-members-in-folders): which child nodes the member walk descends into ----
    // A FB can hold folders (601, Infosys "PLC-Objekte anlegen": "Create folder for methods (SubType
    // 601 — available from TC 4024.14)"). The walk used to descend only into PROPERTY, so every
    // METHOD/PROPERTY/ACTION inside such a folder was missing from list_pou_members.

    [Theory]
    [InlineData(PlcItemType.Folder)]
    [InlineData(PlcItemType.Property)]
    [InlineData(PlcItemType.InterfaceProperty)]
    public void ShouldDescendInto_FolderAndProperty_True(int itemType)
        => Assert.True(PlcMemberKind.ShouldDescendInto(itemType));

    [Theory]
    [InlineData(PlcItemType.Method)]
    [InlineData(PlcItemType.InterfaceMethod)]
    [InlineData(PlcItemType.Action)]
    [InlineData(PlcItemType.PropertyGet)]
    [InlineData(PlcItemType.PropertySet)]
    [InlineData(PlcItemType.InterfaceGet)]
    [InlineData(PlcItemType.InterfaceSet)]
    [InlineData(PlcDeletePolicy.Transition)]
    [InlineData(-1)]
    public void ShouldDescendInto_Leaves_False(int itemType)
        => Assert.False(PlcMemberKind.ShouldDescendInto(itemType));

    // ---- Which sections a member read may request ----------------------------
    // Reading the DeclarationText of an INTERFACE Get/Set accessor crashed TcXaeShell (unhandled C++ exception in the
    // XAE process), so twincat_plc_search over a project with an interface property took the XAE down (issue #66).

    [Theory]
    [InlineData(PlcItemType.InterfaceGet)]
    [InlineData(PlcItemType.InterfaceSet)]
    public void InterfaceAccessors_HaveNoText_AndNoSectionIsEverRequested(int itemType)
    {
        var entry = PlcElementCatalog.ForItemType(itemType)!;
        Assert.True(entry.HasNoText);
        Assert.False(entry.HasDeclaration);
        Assert.False(entry.HasImplementation);
        Assert.Equal((false, false), PlcElementCatalog.ReadableSections(itemType));
    }

    [Fact]
    public void NoTextReason_NamesThePathAndThePropertyAsTheThingToRead()
    {
        string reason = PlcElementCatalog.NoTextReason("A^I_X^P^Get");
        Assert.Contains("'A^I_X^P^Get'", reason);
        Assert.Contains("INTERFACE property", reason);
    }

    [Theory]
    [InlineData(PlcItemType.InterfaceProperty)]
    [InlineData(PlcItemType.InterfaceMethod)]
    [InlineData(PlcItemType.Property)]
    [InlineData(PlcItemType.Interface)]
    public void ReadableSections_DeclarationOnly_NeverRequestsImplementation(int itemType)
        => Assert.Equal((true, false), PlcElementCatalog.ReadableSections(itemType));

    [Theory]
    [InlineData(PlcItemType.Action)]
    [InlineData(PlcDeletePolicy.Transition)]
    public void ReadableSections_ImplementationOnly_NeverRequestsDeclaration(int itemType)
        => Assert.Equal((false, true), PlcElementCatalog.ReadableSections(itemType));

    [Theory]
    [InlineData(PlcItemType.Method)]
    [InlineData(PlcItemType.PropertyGet)]
    [InlineData(PlcItemType.PropertySet)]
    public void ReadableSections_DeclarationAndImplementation_Both(int itemType)
        => Assert.Equal((true, true), PlcElementCatalog.ReadableSections(itemType));

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(PlcItemType.Folder)]
    public void ReadableSections_UnknownOrNotCode_KeepsBestEffortReadOfBoth(int? itemType)
        => Assert.Equal((true, true), PlcElementCatalog.ReadableSections(itemType));
}
