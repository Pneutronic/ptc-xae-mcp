using System.Reflection;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class PlcKindResolverTests
{
    private const string TypeLibPath =
        @"C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TE2000-HMI-Engineering\VisualStudio\2022\TCatSysManagerLib.dll";

    // ── catalog ↔ ItemType ──────────────────────────────────────────────

    [Fact]
    public void Catalog_HasNoDuplicateItemTypes_AndStaysInTheSourceRange()
    {
        var types = PlcElementCatalog.Entries.Select(e => e.ItemType).ToList();
        types.Should().OnlyHaveUniqueItems();
        types.Should().OnlyContain(t => t >= 600 && t <= 699);
    }

    [Fact]
    public void Catalog_EveryKindOfTheSchemaHasAtLeastOneRow()
    {
        foreach (string kind in PlcKind.All)
            PlcElementCatalog.Entries.Should().Contain(e => e.Kind == kind, $"kind {kind} needs a catalog row");
    }

    [Fact]
    public void Catalog_RowsOnlyUseKindsOfTheSchema()
    {
        PlcElementCatalog.Entries.Where(e => !e.IsSystem)
            .Select(e => e.Kind!).Distinct().Should().BeSubsetOf(PlcKind.All);
    }

    [Fact]
    public void Catalog_PinsTheContentOfTheInfosysTable()
    {
        PlcElementCatalog.ForItemType(PlcItemType.Action)!.Content.Should().Be(PlcContent.Implementation);
        PlcElementCatalog.ForItemType(PlcItemType.Transition)!.Content.Should().Be(PlcContent.Implementation);
        PlcElementCatalog.ForItemType(PlcItemType.Method)!.Content.Should().Be(PlcContent.DeclarationAndImplementation);
        PlcElementCatalog.ForItemType(PlcItemType.PropertyGet)!.HasImplementation.Should().BeTrue();
        PlcElementCatalog.ForItemType(PlcItemType.DutStruct)!.HasImplementation.Should().BeFalse();
        PlcElementCatalog.ForItemType(PlcItemType.Gvl)!.HasImplementation.Should().BeFalse();
        PlcElementCatalog.ForItemType(PlcItemType.Interface)!.HasImplementation.Should().BeFalse();
    }

    [Fact]
    public void Catalog_629IsTheParameterList_NotAPersistentList()
    {
        var e = PlcElementCatalog.ForItemType(629)!;
        e.Kind.Should().Be(PlcKind.Gvl);
        e.Subtype.Should().Be("PARAMLIST");
        e.Constant.Should().Be("PLCGVLPARAMLIST");
    }

    // The catalog must stay consistent with the older, narrower classifications it generalises.
    [Fact]
    public void Catalog_AgreesWithTheOldClassifiers()
    {
        for (int t = 600; t < 700; t++)
        {
            string? old = PlcCodeClassification.PouKindOf(t);
            if (old is not null) PlcKindResolver.KindOf(t).Should().Be(old, $"ItemType {t}");

            if (PlcMemberKind.FromItemType(t) is not null)
                PlcKindResolver.KindOf(t).Should().Be(PlcKind.Member, $"ItemType {t}");

            if (PlcDeletePolicy.IsDeletable(t))
                PlcElementCatalog.ForItemType(t).Should().NotBeNull($"deletable ItemType {t} must be in the catalog")
                    .And.Match<PlcCatalogEntry>(e => !e.IsSystem);
        }
    }

    [Fact]
    public void Catalog_InterfaceVariantsAreExactlyTheInterfaceItemTypes()
    {
        PlcElementCatalog.Entries.Where(e => e.OnInterface).Select(e => e.ItemType)
            .Should().BeEquivalentTo(new[]
            {
                PlcItemType.InterfaceMethod, PlcItemType.InterfaceProperty,
                PlcItemType.InterfaceGet, PlcItemType.InterfaceSet,
            });
    }

    // Authoritative numbers come from the installed type library (spec R1). Without TwinCAT installed
    // (hosted CI) there is nothing to compare against, so the test ends without asserting.
    [Fact]
    public void Catalog_MatchesTreeItemTypesOfTheInstalledTypeLibrary()
    {
        if (!File.Exists(TypeLibPath)) return;

        var asm = Assembly.LoadFrom(TypeLibPath);
        var treeItemTypes = asm.GetType("TCatSysManagerLib.TREEITEMTYPES");
        treeItemTypes.Should().NotBeNull();

        foreach (var row in PlcElementCatalog.Entries)
        {
            string name = "TREEITEMTYPE_" + row.Constant;
            Enum.IsDefined(treeItemTypes!, name).Should().BeTrue($"{name} must exist in TREEITEMTYPES");
            ((int)Enum.Parse(treeItemTypes!, name)).Should().Be(row.ItemType, name);
        }

        // Every type of 600–699 the library defines is in the catalog.
        var libraryValues = Enum.GetValues(treeItemTypes!).Cast<object>()
            .Select(v => (int)v).Where(v => v >= 600 && v < 700).Distinct();
        libraryValues.Should().BeSubsetOf(PlcElementCatalog.Entries.Select(e => e.ItemType));
    }

    // ── kind / subtype parsing ──────────────────────────────────────────

    [Theory]
    [InlineData("POU", "POU")]
    [InlineData("pou", "POU")]
    [InlineData(" gvl ", "GVL")]
    [InlineData("Member", "MEMBER")]
    [InlineData("TextList", "TEXTLIST")]
    public void TryParseKind_AcceptsAnyCase(string raw, string expected)
    {
        PlcKindResolver.TryParseKind(raw, out string kind, out string? error).Should().BeTrue();
        kind.Should().Be(expected);
        error.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TryParseKind_Missing_IsAnError(string? raw)
    {
        PlcKindResolver.TryParseKind(raw, out _, out string? error).Should().BeFalse();
        error.Should().StartWith("[Error] kind is required").And.Contain("POU");
    }

    [Fact]
    public void TryParseKind_Unknown_ListsTheKinds()
    {
        PlcKindResolver.TryParseKind("STRUCT", out _, out string? error).Should().BeFalse();
        error.Should().StartWith("[Error] Unknown kind 'STRUCT'").And.Contain("DUT").And.Contain("MEMBER");
    }

    [Fact]
    public void TryParseSubtype_ValidatesAgainstTheKind()
    {
        PlcKindResolver.TryParseSubtype(PlcKind.Dut, "struct", out string? sub, out string? err).Should().BeTrue();
        sub.Should().Be("STRUCT");
        err.Should().BeNull();

        PlcKindResolver.TryParseSubtype(PlcKind.Dut, null, out sub, out err).Should().BeTrue();
        sub.Should().BeNull();

        PlcKindResolver.TryParseSubtype(PlcKind.Dut, "NORMAL", out _, out err).Should().BeFalse();
        err.Should().StartWith("[Error] Unknown subtype 'NORMAL' for kind DUT").And.Contain("STRUCT");

        PlcKindResolver.TryParseSubtype(PlcKind.Interface, "X", out _, out err).Should().BeFalse();
        err.Should().Contain("has no subtypes");
    }

    // ── Check ───────────────────────────────────────────────────────────

    [Fact]
    public void Check_MatchingKind_IsAccepted()
    {
        PlcKindResolver.Check("p", PlcKind.Dut, new PlcNodeInfo(true, PlcItemType.DutStruct)).Should().BeNull();
        PlcKindResolver.Check("p", PlcKind.Gvl, new PlcNodeInfo(true, PlcItemType.GvlParamList)).Should().BeNull();
        PlcKindResolver.Check("p", PlcKind.Member, new PlcNodeInfo(true, PlcItemType.InterfaceGet)).Should().BeNull();
        PlcKindResolver.Check("p", PlcKind.Dut, new PlcNodeInfo(true, PlcItemType.DutStruct), "STRUCT").Should().BeNull();
    }

    [Fact]
    public void Check_GvlWhereDutWasAsked_NamesBoth()
    {
        // The 2026-10-06 confusion: a DUT was handled as a GVL.
        PlcKindResolver.Check("TIPC^P^P Project^GVLs^GVL_Main", PlcKind.Dut, new PlcNodeInfo(true, PlcItemType.Gvl))
            .Should().Be("[Error] 'TIPC^P^P Project^GVLs^GVL_Main' is a GVL, not a DUT.");
    }

    [Fact]
    public void Check_Article_IsAnForInterfaceAndImagePool()
    {
        PlcKindResolver.Check("p", PlcKind.Pou, new PlcNodeInfo(true, PlcItemType.Interface))
            .Should().Be("[Error] 'p' is an INTERFACE, not a POU.");
        PlcKindResolver.Check("p", PlcKind.Interface, new PlcNodeInfo(true, PlcItemType.PouFunctionBlock))
            .Should().Be("[Error] 'p' is a POU, not an INTERFACE.");
    }

    [Fact]
    public void Check_SubtypeMismatch_NamesBothSubtypes()
    {
        PlcKindResolver.Check("p", PlcKind.Dut, new PlcNodeInfo(true, PlcItemType.DutStruct), "ENUM")
            .Should().Be("[Error] 'p' is a DUT (STRUCT), not a DUT (ENUM).");
    }

    [Fact]
    public void Check_MissingNode_IsNotFound()
    {
        PlcKindResolver.Check("p", PlcKind.Pou, PlcNodeInfo.Missing).Should().Be("[Error] PLC tree path not found: p\nNext: " + PlcKindResolver.NotFoundHint);
    }

    [Fact]
    public void Check_UnreadableItemType_IsRefused_NotGuessed()
    {
        PlcKindResolver.Check("p", PlcKind.Pou, new PlcNodeInfo(true, null))
            .Should().StartWith("[Error] Cannot verify kind of 'p'");
    }

    [Theory]
    [InlineData(PlcItemType.PlcApp)]
    [InlineData(PlcItemType.ProjectInfo)]
    [InlineData(PlcItemType.TmcDescription)]
    public void Check_SystemNodes_AreNeverAnyKind(int itemType)
    {
        PlcKindResolver.Check("p", PlcKind.Folder, new PlcNodeInfo(true, itemType))
            .Should().Contain("PLC system node").And.Contain("not a FOLDER");
    }

    [Fact]
    public void Check_UnknownItemType_IsRefused()
    {
        PlcKindResolver.Check("p", PlcKind.Pou, new PlcNodeInfo(true, 9999))
            .Should().Contain("not a PLC element known to this tool (ItemType 9999)");
    }

    [Fact]
    public void KindOf_ReturnsNullForSystemAndUnknown()
    {
        PlcKindResolver.KindOf(PlcItemType.PlcApp).Should().BeNull();
        PlcKindResolver.KindOf(9999).Should().BeNull();
        PlcKindResolver.KindOf(PlcItemType.Transition).Should().Be(PlcKind.Member);
    }
}
