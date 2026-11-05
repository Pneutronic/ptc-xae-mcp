using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class LibraryCoordinatorTests
{
    private static LibraryReference Ref(string placeholder, string version,
        LibrarySource source = LibrarySource.Xae, string? filePinned = null, bool? matches = null) =>
        new(
            Name: placeholder,
            Version: version,
            Publisher: "Beckhoff Automation GmbH",
            Placeholder: placeholder,
            EffectiveResolution: $"{placeholder}, {version} (Beckhoff Automation GmbH)",
            Source: source,
            FilePinnedVersion: filePinned,
            VersionMatchesFile: matches);

    [Fact]
    public void ListReferences_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };
        var coord = new LibraryCoordinator(fake);

        var (summary, refs) = coord.ListReferences();

        Assert.StartsWith("[Error]", summary);
        Assert.Empty(refs);
        Assert.DoesNotContain("ListLibraryReferences", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void ListReferences_NoSolution_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        var coord = new LibraryCoordinator(fake);

        var (summary, refs) = coord.ListReferences();

        Assert.StartsWith("[Error]", summary);
        Assert.Empty(refs);
    }

    [Fact]
    public void ListReferences_Empty_ReportsNone()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        var coord = new LibraryCoordinator(fake);

        var (summary, refs) = coord.ListReferences();

        Assert.Equal("No library references found.", summary);
        Assert.Empty(refs);
    }

    [Fact]
    public void ListReferences_AllMatch_NoMismatchNote()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_Standard", "3.4.5.0", filePinned: "3.4.5.0", matches: true));
        fake.LibraryReferences.Add(Ref("Tc3_Module", "3.5.0.0", filePinned: "3.5.0.0", matches: true));

        var (summary, refs) = new LibraryCoordinator(fake).ListReferences();

        Assert.Equal("Found 2 library references.", summary);
        Assert.Equal(2, refs.Count);
    }

    [Fact]
    public void ListReferences_WithMismatches_AnnotatesCount()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_Standard", "3.4.5.0", filePinned: "3.4.5.0", matches: true));
        fake.LibraryReferences.Add(Ref("Tc3_Module", "3.5.0.0", filePinned: "3.4.0.0", matches: false));

        var (summary, _) = new LibraryCoordinator(fake).ListReferences();

        Assert.Contains("1 XAE/.plcproj version mismatch", summary);
    }

    [Fact]
    public void ListInstalled_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };

        var (summary, libs) = new LibraryCoordinator(fake).ListInstalled();

        Assert.StartsWith("[Error]", summary);
        Assert.Empty(libs);
        Assert.DoesNotContain("ListInstalledLibraries", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void ListInstalled_NoSolution_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };

        var (summary, libs) = new LibraryCoordinator(fake).ListInstalled();

        Assert.StartsWith("[Error]", summary);
        Assert.Empty(libs);
    }

    [Fact]
    public void ListInstalled_ScanThrows_WrappedAsError()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ListInstalledThrows = new InvalidOperationException("ScanLibraries failed: boom")
        };

        var (summary, libs) = new LibraryCoordinator(fake).ListInstalled();

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("ScanLibraries failed", summary);
        Assert.Empty(libs);
    }

    [Fact]
    public void ListInstalled_HappyPath_ReportsCount()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.InstalledLibraries.Add(new InstalledLibrary("Tc2_Standard", "3.4.5.0", "Beckhoff", "Standard"));
        fake.InstalledLibraries.Add(new InstalledLibrary("Tc3_Module", "3.5.0.0", "Beckhoff", "Module"));

        var (summary, libs) = new LibraryCoordinator(fake).ListInstalled();

        Assert.StartsWith("Found 2 installed libraries", summary);
        Assert.Equal(2, libs.Count);
    }

    private static InstalledLibrary Installed(string name, string version, string publisher = "Beckhoff Automation GmbH") =>
        new(name, version, publisher, name);

    [Fact]
    public void CheckAvailability_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };

        var (summary, items) = new LibraryCoordinator(fake).CheckAvailability();

        Assert.StartsWith("[Error]", summary);
        Assert.Empty(items);
        Assert.DoesNotContain("ListLibraryReferences", string.Join(",", fake.CallLog));
        Assert.DoesNotContain("ListInstalledLibraries", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void CheckAvailability_NoSolution_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };

        var (summary, items) = new LibraryCoordinator(fake).CheckAvailability();

        Assert.StartsWith("[Error]", summary);
        Assert.Empty(items);
    }

    [Fact]
    public void CheckAvailability_NoReferences_ShortCircuits()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.InstalledLibraries.Add(Installed("Tc2_Standard", "3.4.5.0"));

        var (summary, items) = new LibraryCoordinator(fake).CheckAvailability();

        Assert.Equal("No library references found.", summary);
        Assert.Empty(items);
        Assert.DoesNotContain("ListInstalledLibraries", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void CheckAvailability_AllInstalled_ExactVersionMatch()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_Standard", "3.4.5.0"));
        fake.LibraryReferences.Add(Ref("Tc3_Module", "3.5.0.0"));
        fake.InstalledLibraries.Add(Installed("Tc2_Standard", "3.4.5.0"));
        fake.InstalledLibraries.Add(Installed("Tc3_Module", "3.5.0.0"));
        fake.InstalledLibraries.Add(Installed("Tc2_System", "3.4.5.0"));

        var (summary, items) = new LibraryCoordinator(fake).CheckAvailability();

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(AvailabilityStatus.Installed, i.Status));
        Assert.All(items, i => Assert.NotNull(i.Match));
        Assert.Equal("All 2 library references installed.", summary);
    }

    [Fact]
    public void CheckAvailability_NameMissing_ReportsMissing()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_Standard", "3.4.5.0"));
        fake.LibraryReferences.Add(Ref("Tc3_Missing", "1.0.0.0"));
        fake.InstalledLibraries.Add(Installed("Tc2_Standard", "3.4.5.0"));

        var (summary, items) = new LibraryCoordinator(fake).CheckAvailability();

        var missing = Assert.Single(items, i => i.Status == AvailabilityStatus.Missing);
        Assert.Equal("Tc3_Missing", missing.Reference.Name);
        Assert.Null(missing.Match);
        Assert.Contains("1 missing", summary);
    }

    [Fact]
    public void CheckAvailability_NameInstalledWrongVersion_ReportsMismatch()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_Standard", "3.4.5.0"));
        fake.InstalledLibraries.Add(Installed("Tc2_Standard", "3.4.4.0"));

        var (summary, items) = new LibraryCoordinator(fake).CheckAvailability();

        var item = Assert.Single(items);
        Assert.Equal(AvailabilityStatus.VersionMismatch, item.Status);
        Assert.NotNull(item.Match);
        Assert.Equal("3.4.4.0", item.Match!.Version);
        Assert.Contains("version mismatch", summary);
    }

    [Fact]
    public void CheckAvailability_StarVersion_SatisfiedByAnyInstalled()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_Standard", "*"));
        fake.InstalledLibraries.Add(Installed("Tc2_Standard", "3.4.5.0"));

        var (summary, items) = new LibraryCoordinator(fake).CheckAvailability();

        var item = Assert.Single(items);
        Assert.Equal(AvailabilityStatus.Installed, item.Status);
        Assert.NotNull(item.Match);
        Assert.Contains("installed", summary);
    }

    [Fact]
    public void CheckAvailability_NewestVersionAnnotation_SatisfiedByAnyInstalled()
    {
        // Issue #152 comment / PR #167 live finding: a reference the XAE reports as
        // "* (newest)" (added with version="*") must be treated as any-version, not compared
        // literally against an installed version and misclassified as VersionMismatch.
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_MC2", "* (newest)"));
        fake.InstalledLibraries.Add(Installed("Tc2_MC2", "3.3.42.0"));

        var (summary, items) = new LibraryCoordinator(fake).CheckAvailability();

        var item = Assert.Single(items);
        Assert.Equal(AvailabilityStatus.Installed, item.Status);
        Assert.NotNull(item.Match);
        Assert.Contains("installed", summary);
    }

    [Fact]
    public void CheckAvailability_StarVersion_NoMatchingName_ReportsMissing()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_Missing", "*"));
        fake.InstalledLibraries.Add(Installed("Tc2_Standard", "3.4.5.0"));

        var (summary, items) = new LibraryCoordinator(fake).CheckAvailability();

        var item = Assert.Single(items);
        Assert.Equal(AvailabilityStatus.Missing, item.Status);
        Assert.Null(item.Match);
        Assert.Contains("1 missing", summary);
    }

    [Fact]
    public void CheckAvailability_NameMatch_IgnoresPublisherMismatchWhenSidesDiffer()
    {
        // Per spec: missing publisher on either side is not a mismatch; identical name+version wins.
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_Standard", "3.4.5.0"));
        fake.InstalledLibraries.Add(Installed("Tc2_Standard", "3.4.5.0", publisher: ""));

        var (_, items) = new LibraryCoordinator(fake).CheckAvailability();

        Assert.Equal(AvailabilityStatus.Installed, Assert.Single(items).Status);
    }

    [Fact]
    public void CheckAvailability_MixedResults_SummaryHasAllCounts()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_Standard", "3.4.5.0"));   // installed
        fake.LibraryReferences.Add(Ref("Tc3_Module", "3.5.0.0"));     // mismatch
        fake.LibraryReferences.Add(Ref("Tc2_Missing", "1.0.0.0"));    // missing
        fake.InstalledLibraries.Add(Installed("Tc2_Standard", "3.4.5.0"));
        fake.InstalledLibraries.Add(Installed("Tc3_Module", "3.4.0.0"));

        var (summary, items) = new LibraryCoordinator(fake).CheckAvailability();

        Assert.Equal(3, items.Count);
        Assert.Equal(1, items.Count(i => i.Status == AvailabilityStatus.Installed));
        Assert.Equal(1, items.Count(i => i.Status == AvailabilityStatus.Missing));
        Assert.Equal(1, items.Count(i => i.Status == AvailabilityStatus.VersionMismatch));
        Assert.Contains("1 missing", summary);
        Assert.Contains("1 version mismatch", summary);
    }

    [Fact]
    public void CheckAvailability_NameMatchCaseInsensitive()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_Standard", "3.4.5.0"));
        fake.InstalledLibraries.Add(Installed("tc2_standard", "3.4.5.0"));

        var (_, items) = new LibraryCoordinator(fake).CheckAvailability();

        Assert.Equal(AvailabilityStatus.Installed, Assert.Single(items).Status);
    }

    // ── plcProjectName (a solution with several PLC projects) ──────────────────

    [Fact]
    public void PlcProjectName_ReachesEverySessionCall_OfAddRemoveAndSetVersion()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        var coordinator = new LibraryCoordinator(fake);

        coordinator.AddReference("Tc2_Standard", "*", "Beckhoff Automation GmbH", asPlaceholder: true, force: false, plcProjectName: "LibPlc");
        coordinator.SetVersion("Tc2_Standard", "3.4.5.0", null, plcProjectName: "LibPlc");
        coordinator.RemoveReference("Tc2_Standard", plcProjectName: "LibPlc");
        coordinator.CheckAvailability(plcProjectName: "LibPlc");

        Assert.Contains("AddLibraryReference(Tc2_Standard,*,Beckhoff Automation GmbH,True,plc=LibPlc)", fake.CallLog);
        Assert.Contains(fake.CallLog, c => c.StartsWith("SetLibraryVersion(Tc2_Standard,3.4.5.0,", StringComparison.Ordinal) && c.EndsWith(",plc=LibPlc)", StringComparison.Ordinal));
        Assert.Contains("RemoveLibraryReference(Tc2_Standard,plc=LibPlc)", fake.CallLog);
        Assert.DoesNotContain("ListLibraryReferences()", fake.CallLog);
        Assert.Contains("ListLibraryReferences(LibPlc)", fake.CallLog);
    }

    // ── AddReference ──────────────────────────────────────────────────────────

    [Fact]
    public void AddReference_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };

        var (summary, refs) = new LibraryCoordinator(fake).AddReference("Tc2_MC2", "*", null, false, false);

        Assert.StartsWith("[Error]", summary);
        Assert.Empty(refs);
        Assert.DoesNotContain("AddLibraryReference", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void AddReference_NoSolution_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };

        var (summary, refs) = new LibraryCoordinator(fake).AddReference("Tc2_MC2", "*", null, false, false);

        Assert.StartsWith("[Error]", summary);
        Assert.Empty(refs);
    }

    [Fact]
    public void AddReference_NotInstalled_ReturnsErrorWithoutMutation()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.InstalledLibraries.Add(Installed("Tc2_Standard", "3.4.5.0"));

        var (summary, refs) = new LibraryCoordinator(fake).AddReference("Tc2_MC2", "*", null, false, false);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("not installed", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AddLibraryReference", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void AddReference_NotInstalled_Force_Adds()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };

        var (summary, refs) = new LibraryCoordinator(fake).AddReference("Tc2_MC2", "*", null, false, true);

        Assert.Contains("Added", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Single(refs);
        Assert.Equal("Tc2_MC2", refs[0].Name);
    }

    [Fact]
    public void AddReference_HappyPath_AddsAndReturnsSummary()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.InstalledLibraries.Add(Installed("Tc2_MC2", "3.3.73.0"));

        var (summary, refs) = new LibraryCoordinator(fake).AddReference("Tc2_MC2", "*", "Beckhoff Automation GmbH", false, false);

        Assert.Contains("Added", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Single(refs);
        Assert.Equal("Tc2_MC2", refs[0].Name);
    }

    [Fact]
    public void AddReference_AlreadyReferenced_ReturnsNoOpMessage()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_MC2", "*"));
        fake.InstalledLibraries.Add(Installed("Tc2_MC2", "3.3.73.0"));

        var (summary, refs) = new LibraryCoordinator(fake).AddReference("Tc2_MC2", "*", null, false, false);

        Assert.Contains("already referenced", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AddLibraryReference", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void AddReference_StarVersion_AcceptedWhenNameInstalled()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.InstalledLibraries.Add(Installed("Tc2_MC2", "3.3.73.0"));

        var (summary, refs) = new LibraryCoordinator(fake).AddReference("Tc2_MC2", "*", null, false, false);

        Assert.Contains("Added", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddReference_AsPlaceholder_SkipsInstallGuard()
    {
        // Placeholder references don't require the library to be installed.
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };

        var (summary, refs) = new LibraryCoordinator(fake).AddReference("Tc2_NC", "*", null, true, false);

        Assert.Contains("Added", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AddLibraryReference", string.Join(",", fake.CallLog));
    }

    // ── RemoveReference ───────────────────────────────────────────────────────

    [Fact]
    public void RemoveReference_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };

        var (summary, refs) = new LibraryCoordinator(fake).RemoveReference("Tc2_MC2");

        Assert.StartsWith("[Error]", summary);
        Assert.Empty(refs);
    }

    [Fact]
    public void RemoveReference_NotReferenced_ReturnsNoOpMessage()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };

        var (summary, refs) = new LibraryCoordinator(fake).RemoveReference("Tc2_MC2");

        Assert.Contains("not referenced", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RemoveLibraryReference", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void RemoveReference_HappyPath_RemovesAndReturnsSummary()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_MC2", "*"));

        var (summary, refs) = new LibraryCoordinator(fake).RemoveReference("Tc2_MC2");

        Assert.Contains("Removed", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(refs);
    }

    // ── SetVersion ────────────────────────────────────────────────────────────

    [Fact]
    public void SetVersion_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };

        var (summary, refs) = new LibraryCoordinator(fake).SetVersion("Tc2_MC2", "3.3.73.0", null);

        Assert.StartsWith("[Error]", summary);
        Assert.Empty(refs);
    }

    [Fact]
    public void SetVersion_NotReferenced_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };

        var (summary, refs) = new LibraryCoordinator(fake).SetVersion("Tc2_MC2", "3.3.73.0", null);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("not referenced", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SetLibraryVersion", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void SetVersion_HappyPath_ChangesVersionAndReturnsSummary()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_MC2", "*"));

        var (summary, refs) = new LibraryCoordinator(fake).SetVersion("Tc2_MC2", "3.3.73.0", null);

        Assert.Contains("3.3.73.0", summary);
        Assert.Single(refs);
        Assert.Equal("3.3.73.0", refs[0].Version);
    }

    [Fact]
    public void SetVersion_WithoutCompany_KeepsCurrentPublisher()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_Standard", "3.3.3.0") with { Publisher = "Beckhoff Automation GmbH" });

        new LibraryCoordinator(fake).SetVersion("Tc2_Standard", "*", null);

        Assert.Contains("SetLibraryVersion(Tc2_Standard,*,Beckhoff Automation GmbH)", fake.CallLog);
    }

    [Fact]
    public void SetVersion_ExplicitCompany_IsPassedThrough()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibraryReferences.Add(Ref("Tc2_Standard", "3.3.3.0") with { Publisher = "Beckhoff Automation GmbH" });

        new LibraryCoordinator(fake).SetVersion("Tc2_Standard", "*", "Other Co");

        Assert.Contains("SetLibraryVersion(Tc2_Standard,*,Other Co)", fake.CallLog);
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("* (newest)", true)]
    [InlineData("newest", true)]
    [InlineData("NEWEST", true)]
    [InlineData("3.3.73.0", false)]
    public void IsAnyVersion_RecognisesXaeSpellings(string version, bool expected)
    {
        Assert.Equal(expected, Ref("Tc2_MC2", version).IsAnyVersion);
    }
}
