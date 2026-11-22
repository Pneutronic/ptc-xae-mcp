using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class AxisSimulationXmlTests
{
    [Fact]
    public void Read_Attribute_ReturnsValue() =>
        Assert.True(AxisSimulationXml.TryRead("<TreeItem><Axis Id=\"1\" SimulationMode=\"true\"/></TreeItem>"));

    [Fact]
    public void Read_Element_ReturnsValue() =>
        Assert.False(AxisSimulationXml.TryRead("<TreeItem><NcAxisDef><SimulationMode>false</SimulationMode></NcAxisDef></TreeItem>"));

    [Fact]
    public void Read_Absent_IsNull() =>
        Assert.Null(AxisSimulationXml.TryRead("<TreeItem><NcAxisDef/></TreeItem>"));

    [Fact]
    public void Set_Attribute_ChangesOnlyThatValue()
    {
        string xml = AxisSimulationXml.TrySet("<TreeItem><Axis Id=\"1\" SimulationMode=\"false\"><Name>A</Name></Axis></TreeItem>", true)!;
        Assert.Contains("SimulationMode=\"true\"", xml);
        Assert.Contains("<Name>A</Name>", xml);
    }

    [Fact]
    public void Set_Element_ChangesThatElement() =>
        Assert.Contains("<SimulationMode>true</SimulationMode>",
            AxisSimulationXml.TrySet("<TreeItem><NcAxisDef><SimulationMode>false</SimulationMode></NcAxisDef></TreeItem>", true));

    [Fact]
    public void Set_Absent_IsNullNotAGuess() =>
        Assert.Null(AxisSimulationXml.TrySet("<TreeItem><NcAxisDef/></TreeItem>", true));
}

public class VariantItemCoordinatorTests
{
    private const string Path = "TIID^Device 1";
    private const string Axis = "TINC^NC-Task 1 SAF^Axes^Axis 1";

    private static FakeTwinCatSession Session(string active = "Machine")
    {
        var s = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        s.FakeVariantStatus = new VariantStatusResult(active, ["Machine", "Simulation"], []);
        s.FakeVariantItems[Path] = new FakeVariantItem();
        s.FakeVariantItems[Axis] = new FakeVariantItem { IsNcAxis = true };
        return s;
    }

    [Fact]
    public void SetItem_Disabled_EnablesFlagThenSwitchesThenWritesThenRestores()
    {
        var fake = Session(active: "Machine");
        string result = new VariantEditingCoordinator(fake).SetItem(Path, "Simulation", true, null);

        Assert.DoesNotContain("[Error]", result);
        var log = fake.CallLog.Where(c => c.StartsWith("SetVariantFlags", StringComparison.Ordinal)
                                          || c.StartsWith("SetActiveVariant", StringComparison.Ordinal)
                                          || c.StartsWith("WriteVariantItemValue", StringComparison.Ordinal)
                                          || c.StartsWith("SaveVariantChanges", StringComparison.Ordinal)).ToList();
        Assert.Equal(
        [
            $"SetVariantFlags({Path}, pvDisable=True, pvParameter=False)",
            "SetActiveVariant(Simulation)",
            $"WriteVariantItemValue({Path}, disabled=True, axisSimulation=)",
            "SetActiveVariant(Machine)",          // matrix read: Machine
            "SetActiveVariant(Simulation)",       // matrix read: Simulation
            "SetActiveVariant(Machine)",          // final restore
            "SaveVariantChanges()",
        ], log);
        Assert.Equal("Machine", fake.FakeVariantStatus.ActiveVariant);
    }

    [Fact]
    public void SetItem_ReturnsTheValueForAllVariants()
    {
        var fake = Session(active: "Machine");
        string result = new VariantEditingCoordinator(fake).SetItem(Path, "Simulation", true, null);

        var items = JsonDocument.Parse(result).RootElement.GetProperty("variants");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("Machine", items[0].GetProperty("variant").GetString());
        Assert.False(items[0].GetProperty("disabled").GetBoolean());
        Assert.Equal("Simulation", items[1].GetProperty("variant").GetString());
        Assert.True(items[1].GetProperty("disabled").GetBoolean());
    }

    [Fact]
    public void SetItem_FlagAlreadySet_IsNotSetAgain()
    {
        var fake = Session();
        fake.FakeVariantItems[Path].PvDisable = true;
        new VariantEditingCoordinator(fake).SetItem(Path, "Simulation", true, null);

        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("SetVariantFlags", StringComparison.Ordinal));
    }

    [Fact]
    public void SetItem_WriteThrows_RestoresTheActiveVariantAndReportsOneError()
    {
        var fake = Session(active: "Machine");
        fake.WriteVariantItemThrows = new InvalidOperationException("no luck");
        string result = new VariantEditingCoordinator(fake).SetItem(Path, "Simulation", true, null);

        Assert.StartsWith("[Error]", result);
        Assert.DoesNotContain("[Error] [Error]", result);
        Assert.Contains("no luck", result);
        Assert.Equal("Machine", fake.FakeVariantStatus.ActiveVariant);
        Assert.DoesNotContain("SaveVariantChanges()", fake.CallLog);
    }

    [Fact]
    public void SetItem_SwitchThrows_StillRestores()
    {
        var fake = Session(active: "Machine");
        fake.SetActiveVariantThrows = new InvalidOperationException("switch failed");
        string result = new VariantEditingCoordinator(fake).SetItem(Path, "Simulation", true, null);

        Assert.StartsWith("[Error]", result);
        Assert.Equal("Machine", fake.FakeVariantStatus.ActiveVariant);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("WriteVariantItemValue", StringComparison.Ordinal));
    }

    [Fact]
    public void SetItem_TargetIsActiveVariant_DoesNotSwitchForTheWrite()
    {
        var fake = Session(active: "Simulation");
        new VariantEditingCoordinator(fake).SetItem(Path, "Simulation", true, null);

        int write = fake.CallLog.IndexOf($"WriteVariantItemValue({Path}, disabled=True, axisSimulation=)");
        Assert.True(write >= 0);
        Assert.DoesNotContain("SetActiveVariant(Simulation)", fake.CallLog.Take(write));
        Assert.Equal("Simulation", fake.FakeVariantStatus.ActiveVariant);
    }

    [Fact]
    public void SetItem_AxisSimulation_OnNonAxis_IsRefusedBeforeAnyChange()
    {
        var fake = Session();
        string result = new VariantEditingCoordinator(fake).SetItem(Path, "Simulation", null, true);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("NC axes", result);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("SetVariantFlags", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("SetActiveVariant", StringComparison.Ordinal));
    }

    [Fact]
    public void SetItem_AxisSimulation_EnablesParameterFlag()
    {
        var fake = Session();
        string result = new VariantEditingCoordinator(fake).SetItem(Axis, "Simulation", null, true);

        Assert.DoesNotContain("[Error]", result);
        Assert.Contains($"SetVariantFlags({Axis}, pvDisable=False, pvParameter=True)", fake.CallLog);
        Assert.Contains($"WriteVariantItemValue({Axis}, disabled=, axisSimulation=True)", fake.CallLog);
    }

    [Fact]
    public void SetItem_AxisSimulationNotExposedByTheXae_IsRefusedBeforeAnyChange()
    {
        var fake = Session();
        fake.FakeVariantItems[Axis].SimulationExposed = false;
        string result = new VariantEditingCoordinator(fake).SetItem(Axis, "Simulation", null, true);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("does not carry the simulation flag", result);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("SetVariantFlags", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("SetActiveVariant", StringComparison.Ordinal));
    }

    [Fact]
    public void SetItem_UnknownVariant_ErrorBeforeAnyChange()
    {
        var fake = Session();
        string result = new VariantEditingCoordinator(fake).SetItem(Path, "Nope", true, null);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("Unknown variant", result);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("SetVariantFlags", StringComparison.Ordinal));
    }

    [Fact]
    public void SetItem_GroupAsVariant_IsRefused()
    {
        var fake = Session();
        fake.FakeVariantStatus = fake.FakeVariantStatus with { Groups = [new VariantGroupEntry("AllSim", ["Simulation"])] };
        string result = new VariantEditingCoordinator(fake).SetItem(Path, "[AllSim]", true, null);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("single variant", result);
    }

    [Fact]
    public void SetItem_UnknownItem_ErrorBeforeAnyChange()
    {
        var fake = Session();
        string result = new VariantEditingCoordinator(fake).SetItem("TIID^Nope", "Simulation", true, null);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("TIID^Nope", result);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("SetActiveVariant", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "Simulation")]
    [InlineData(Path, "")]
    public void SetItem_MissingArguments_Error(string path, string variant)
    {
        var fake = Session();
        Assert.StartsWith("[Error]", new VariantEditingCoordinator(fake).SetItem(path, variant, true, null));
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void SetItem_NothingToSet_Error()
    {
        var fake = Session();
        string result = new VariantEditingCoordinator(fake).SetItem(Path, "Simulation", null, null);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("disabled", result);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void SetItem_NoSolution_Error()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        Assert.StartsWith("[Error]", new VariantEditingCoordinator(fake).SetItem(Path, "Simulation", true, null));
    }
}
