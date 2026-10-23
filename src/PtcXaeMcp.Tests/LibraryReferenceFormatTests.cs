using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class LibraryReferenceFormatTests
{
    private static LibraryReference Ref(string name, string version, string? pinned = null, bool? matches = null) =>
        new(name, version, "Beckhoff Automation GmbH", name, $"{name}, {version}", LibrarySource.Xae, pinned, matches);

    private static InstalledLibrary Lib(string name, string version) => new(name, version, "Beckhoff Automation GmbH", name);

    private static FakeTwinCatSession Fake(params LibraryReference[] refs)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.AddRange(refs);
        return fake;
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData("", false, true)]
    [InlineData("text", false, true)]
    [InlineData("JSON", true, true)]
    [InlineData("xml", false, false)]
    public void Validate_AcceptsTextAndJson(string? format, bool json, bool valid)
    {
        string? error = LibraryReferenceFormat.Validate(format, out bool isJson);
        Assert.Equal(valid, error is null);
        if (valid) Assert.Equal(json, isJson);
        else Assert.StartsWith("[Error]", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Suffix_ShowsNameVersionDistributorAndResolution()
    {
        Assert.Equal("[resolved: Tc2_Standard 3.4.5.0 (Beckhoff Automation GmbH), effective]",
            LibraryReferenceFormat.ResolvedSuffix(new ResolvedReference("Tc2_Standard", "3.4.5.0", "Beckhoff Automation GmbH", "effective")));
        Assert.Equal("[resolved: -, unresolved]",
            LibraryReferenceFormat.ResolvedSuffix(new ResolvedReference(null, null, null, "unresolved")));
        Assert.Equal("[resolved: L 1.0.0.0, fixed]",
            LibraryReferenceFormat.ResolvedSuffix(new ResolvedReference("L", "1.0.0.0", null, "fixed")));
    }

    [Fact]
    public void Text_KeepsTheExistingLineAndAppendsTheSuffix()
    {
        var r = Ref("Tc2_Standard", "*", pinned: "3.4.5.0", matches: false);
        string text = LibraryReferenceFormat.Text("Found 1 library reference.",
            [new ResolvedLibraryReference(r, new ResolvedReference("Tc2_Standard", "3.4.10.0", "Beckhoff Automation GmbH", "newest-installed"))]);
        string line = text.Split('\n')[1].TrimEnd('\r');
        Assert.StartsWith("  Tc2_Standard: Tc2_Standard * (Beckhoff Automation GmbH) [src=xae]  [file: 3.4.5.0 — mismatch] [resolved:", line, StringComparison.Ordinal);
        Assert.EndsWith("[resolved: Tc2_Standard 3.4.10.0 (Beckhoff Automation GmbH), newest-installed]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_CarriesTheFullRecords()
    {
        var r = Ref("Tc2_Standard", "3.4.5.0");
        string json = LibraryReferenceFormat.Json("Found 1 library reference.",
            [new ResolvedLibraryReference(r, new ResolvedReference("Tc2_Standard", "3.4.5.0", "Beckhoff Automation GmbH", "fixed"))], ["w1"]);
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement.GetProperty("references")[0];
        Assert.Equal("Tc2_Standard", item.GetProperty("resolvedName").GetString());
        Assert.Equal("3.4.5.0", item.GetProperty("resolvedVersion").GetString());
        Assert.Equal("fixed", item.GetProperty("resolution").GetString());
        Assert.Equal("xae", item.GetProperty("source").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("w1", doc.RootElement.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public void Coordinator_UsesTheManagersEffectiveResolution()
    {
        var fake = Fake(Ref("Tc2_Standard", "*"));
        fake.FakePlcLibraryContexts.Add(new PlcLibraryContext("PLC", fake.LibraryReferences,
            [new PlaceholderResolution("Tc2_Standard", new ResolvedLibrary("Tc2_Standard", "3.4.5.0", "Beckhoff Automation GmbH"), null, null)],
            [Lib("Tc2_Standard", "3.4.10.0")]));

        var (_, items, warnings) = new LibraryCoordinator(fake).ListReferencesResolved();

        var resolved = Assert.Single(items).Resolved;
        Assert.Equal("effective", resolved.Resolution);
        Assert.Equal("3.4.5.0", resolved.Version);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Coordinator_WithoutManagerData_ComputesNewestInstalledAndWarns()
    {
        var fake = Fake(Ref("Tc2_Standard", "*"), Ref("Tc2_System", "3.4.26.0"));
        fake.FakePlcLibraryContexts.Add(new PlcLibraryContext("PLC", fake.LibraryReferences, [],
            [Lib("Tc2_Standard", "3.4.9.0"), Lib("Tc2_Standard", "3.4.10.0")]));

        var (_, items, warnings) = new LibraryCoordinator(fake).ListReferencesResolved();

        Assert.Equal(["newest-installed", "fixed"], items.Select(i => i.Resolved.Resolution).ToArray());
        Assert.Equal("3.4.10.0", items[0].Resolved.Version);
        Assert.Contains(warnings, w => w.Contains("approximation", StringComparison.Ordinal));
    }

    [Fact]
    public void Coordinator_ManagerUnreadable_FallsBackToInstalledList()
    {
        var fake = Fake(Ref("Tc2_Standard", "*"));
        fake.ReadPlcLibraryContextsThrows = new InvalidOperationException("no node");
        fake.InstalledLibraries.Add(Lib("Tc2_Standard", "3.4.9.0"));

        var (summary, items, warnings) = new LibraryCoordinator(fake).ListReferencesResolved();

        Assert.False(summary.StartsWith("[Error]", StringComparison.Ordinal));
        Assert.Equal("3.4.9.0", Assert.Single(items).Resolved.Version);
        Assert.Contains(warnings, w => w.Contains("no node", StringComparison.Ordinal));
    }

    [Fact]
    public void Coordinator_NoReferences_ReturnsTheSummaryOnly()
    {
        var (summary, items, _) = new LibraryCoordinator(Fake()).ListReferencesResolved();
        Assert.Equal("No library references found.", summary);
        Assert.Empty(items);
    }

    [Fact]
    public void Coordinator_NoSolution_IsAnError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        var (summary, items, _) = new LibraryCoordinator(fake).ListReferencesResolved();
        Assert.StartsWith("[Error]", summary, StringComparison.Ordinal);
        Assert.Empty(items);
    }
}
