using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class VariantEditingCoordinatorTests
{
    private static FakeTwinCatSession Session(string active = "Machine", bool withVariants = true)
    {
        var session = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        session.FakeVariantStatus = withVariants
            ? new VariantStatusResult(active, ["Machine", "Simulation"], [new VariantGroupEntry("AllSim", ["Simulation"])])
            : new VariantStatusResult(string.Empty, [], []);
        return session;
    }

    // ── Create ──────────────────────────────────────────────────────────────

    [Fact]
    public void Create_AddsVariant_WritesOnceAndReturnsJson()
    {
        var fake = Session();
        string result = new VariantEditingCoordinator(fake).Create("Test", null);

        Assert.DoesNotContain("[Error]", result);
        Assert.Single(fake.WrittenVariantConfigs);
        var doc = JsonDocument.Parse(result).RootElement;
        Assert.Equal("Test", doc.GetProperty("variant").GetString());
        Assert.Equal("Machine", doc.GetProperty("activeVariant").GetString());
        Assert.Equal(3, doc.GetProperty("availableVariants").GetArrayLength());
    }

    [Fact]
    public void Create_WithGroup_AddsGroupMember()
    {
        var fake = Session();
        string result = new VariantEditingCoordinator(fake).Create("Sim2", "AllSim");

        Assert.DoesNotContain("[Error]", result);
        Assert.Equal(["Simulation", "Sim2"], fake.FakeVariantStatus.Groups.Single().Members);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[x]")]
    public void Create_InvalidName_ErrorBeforeAnySessionCall(string name)
    {
        var fake = Session();
        string result = new VariantEditingCoordinator(fake).Create(name, null);

        Assert.StartsWith("[Error]", result);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void Create_NoSolution_ReturnsErrorAndDoesNotWrite()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        string result = new VariantEditingCoordinator(fake).Create("Test", null);

        Assert.StartsWith("[Error]", result);
        Assert.Empty(fake.WrittenVariantConfigs);
    }

    [Fact]
    public void Create_Duplicate_ErrorAndNothingWritten()
    {
        var fake = Session();
        string result = new VariantEditingCoordinator(fake).Create("machine", null);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("already exists", result);
        Assert.Empty(fake.WrittenVariantConfigs);
    }

    [Fact]
    public void Create_FirstVariant_BecomesActive()
    {
        var fake = Session(withVariants: false);
        string result = new VariantEditingCoordinator(fake).Create("Machine", null);

        Assert.DoesNotContain("[Error]", result);
        Assert.Contains("SetActiveVariant(Machine)", fake.CallLog);
        Assert.Equal("Machine", fake.FakeVariantStatus.ActiveVariant);
    }

    [Fact]
    public void Create_SecondVariant_DoesNotSwitchTheActiveVariant()
    {
        var fake = Session();
        new VariantEditingCoordinator(fake).Create("Test", null);

        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("SetActiveVariant", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_WriteThrows_IsReportedWithOnePrefix()
    {
        var fake = Session();
        fake.WriteVariantConfigThrows = new InvalidOperationException("XAE refused");
        string result = new VariantEditingCoordinator(fake).Create("Test", null);

        Assert.StartsWith("[Error]", result);
        Assert.DoesNotContain("[Error] [Error]", result);
        Assert.Contains("XAE refused", result);
    }

    [Fact]
    public void Create_WriteDoesNotLand_ErrorAndOriginalConfigRestored()
    {
        var fake = Session();
        string original = fake.ReadProjectVariantConfig();
        fake.WriteVariantConfigIsIgnored = true;
        string result = new VariantEditingCoordinator(fake).Create("Test", null);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("did not take effect", result);
        // The restore attempt writes the XML that was read before the edit.
        Assert.Equal(original, fake.WrittenVariantConfigs.Last());
    }

    // ── Delete ──────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_RemovesVariantAndGroupMembership()
    {
        var fake = Session(active: "Machine");
        string result = new VariantEditingCoordinator(fake).Delete("Simulation");

        Assert.DoesNotContain("[Error]", result);
        Assert.Equal(["Machine"], fake.FakeVariantStatus.AvailableVariants);
        Assert.Empty(fake.FakeVariantStatus.Groups.Single().Members);
        var doc = JsonDocument.Parse(result).RootElement;
        Assert.Equal("AllSim", doc.GetProperty("removedFromGroups")[0].GetString());
    }

    [Fact]
    public void Delete_ActiveVariant_IsRefusedWithHint()
    {
        var fake = Session(active: "Simulation");
        string result = new VariantEditingCoordinator(fake).Delete("simulation");

        Assert.StartsWith("[Error]", result);
        Assert.Contains("active", result);
        Assert.Contains("twincat_set_active_variant", result);
        Assert.Empty(fake.WrittenVariantConfigs);
    }

    [Fact]
    public void Delete_VariantOfTheActiveGroup_IsRefused()
    {
        var fake = Session(active: "[AllSim]");
        string result = new VariantEditingCoordinator(fake).Delete("Simulation");

        Assert.StartsWith("[Error]", result);
        Assert.Empty(fake.WrittenVariantConfigs);
    }

    [Fact]
    public void Delete_Unknown_ErrorAndNothingWritten()
    {
        var fake = Session();
        string result = new VariantEditingCoordinator(fake).Delete("Nope");

        Assert.StartsWith("[Error]", result);
        Assert.Contains("Unknown variant 'Nope'", result);
        Assert.Empty(fake.WrittenVariantConfigs);
    }

    [Fact]
    public void Delete_ItemsStillMentionTheVariant_AreNamedInTheResult()
    {
        var fake = Session();
        fake.FakeVariantItemsXml = "<ProjectVariantItems><Item><Path>TIID^Device 1</Path><Variant>Simulation</Variant></Item></ProjectVariantItems>";
        string result = new VariantEditingCoordinator(fake).Delete("Simulation");

        Assert.DoesNotContain("[Error]", result);
        var notes = JsonDocument.Parse(result).RootElement.GetProperty("notes");
        Assert.Contains(notes.EnumerateArray(), n => n.GetString()!.Contains("ProjectVariantItems"));
    }

    [Fact]
    public void Delete_NoSolution_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        Assert.StartsWith("[Error]", new VariantEditingCoordinator(fake).Delete("X"));
    }
}
