using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// The build checks the PLC library references before it compiles: a missing or wrongly versioned library
/// makes the error list say nothing about the code, so the result is marked not conclusive — the build still runs.
/// </summary>
public class BuildLibraryPreflightTests
{
    private static LibraryReference Ref(string name, string version) =>
        new(name, version, "Beckhoff Automation GmbH", name, $"{name}, {version}", LibrarySource.Xae, null, null);

    private static InstalledLibrary Lib(string name, string version) =>
        new(name, version, "Beckhoff Automation GmbH", name);

    private static PlcLibraryContext Context(string plc, LibraryReference[] refs, InstalledLibrary[] installed) =>
        new(plc, refs, [], installed);

    private static FakeTwinCatSession Session(params PlcLibraryContext[] contexts)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.FakePlcLibraryContexts.AddRange(contexts);
        return fake;
    }

    [Fact]
    public void BuildPlc_AllInstalled_NoWarning()
    {
        var fake = Session(Context("PLC", [Ref("Tc2_Standard", "3.4.5.0")], [Lib("Tc2_Standard", "3.4.5.0")]));

        string result = new BuildCoordinator(fake).BuildPlc(null);

        Assert.StartsWith("Build succeeded", result);
        Assert.DoesNotContain("[Warn]", result);
        Assert.Contains("BuildPlc(<null>)", fake.CallLog);
    }

    [Fact]
    public void BuildPlc_VersionMismatch_WarnsButStillBuilds()
    {
        var fake = Session(Context("PLC", [Ref("Tc2_Standard", "3.4.6.0")], [Lib("Tc2_Standard", "3.4.5.0")]));

        string result = new BuildCoordinator(fake).BuildPlc(null);

        Assert.StartsWith("[Warn] Build result not conclusive", result);
        Assert.Contains("Tc2_Standard 3.4.6.0", result);
        Assert.Contains("version mismatch", result);
        Assert.Contains("installed 3.4.5.0", result);
        Assert.Contains("twincat_install_library", result);
        Assert.Contains("Build succeeded", result);
        Assert.Contains("BuildPlc(<null>)", fake.CallLog);
    }

    [Fact]
    public void BuildPlc_Missing_WarnsAndNamesTheLibrary()
    {
        var fake = Session(Context("PLC", [Ref("Tc3_Missing", "1.0.0.0"), Ref("Tc2_Standard", "*")], [Lib("Tc2_Standard", "3.4.5.0")]));

        string result = new BuildCoordinator(fake).BuildPlc(null);

        Assert.StartsWith("[Warn]", result);
        Assert.Contains("Tc3_Missing 1.0.0.0 missing", result);
        Assert.DoesNotContain("Tc2_Standard", result.Split('\n')[0]);
    }

    [Fact]
    public void BuildPlc_NamedProject_ChecksOnlyThatProject()
    {
        var fake = Session(Context("PLC2", [Ref("Tc2_Standard", "3.4.5.0")], [Lib("Tc2_Standard", "3.4.5.0")]));

        new BuildCoordinator(fake).BuildPlc("PLC2");

        Assert.Contains("ReadPlcLibraryContexts(PLC2)", fake.CallLog);
    }

    [Fact]
    public void BuildPlc_NoName_ChecksOnlyTheFirstProject()
    {
        var fake = Session(
            Context("PLC", [Ref("Tc2_Standard", "3.4.5.0")], [Lib("Tc2_Standard", "3.4.5.0")]),
            Context("Other", [Ref("Tc3_Missing", "1.0.0.0")], []));

        string result = new BuildCoordinator(fake).BuildPlc(null);

        Assert.DoesNotContain("[Warn]", result);
    }

    [Fact]
    public void BuildSolution_ChecksEveryPlcProject_AndNamesIt()
    {
        var fake = Session(
            Context("PLC", [Ref("Tc2_Standard", "3.4.5.0")], [Lib("Tc2_Standard", "3.4.5.0")]),
            Context("Other", [Ref("Tc3_Missing", "1.0.0.0")], []));

        string result = new BuildCoordinator(fake).BuildSolution();

        Assert.StartsWith("[Warn]", result);
        Assert.Contains("Other: Tc3_Missing 1.0.0.0 missing", result);
        Assert.Contains("BuildSolution()", fake.CallLog);
    }

    [Fact]
    public void RebuildPlc_And_RebuildSolution_AlsoWarn()
    {
        var ctx = Context("PLC", [Ref("Tc3_Missing", "1.0.0.0")], []);

        Assert.StartsWith("[Warn]", new BuildCoordinator(Session(ctx)).RebuildPlc(null));
        Assert.StartsWith("[Warn]", new BuildCoordinator(Session(ctx)).RebuildSolution());
    }

    [Fact]
    public void CheckFails_BuildRunsWithNote()
    {
        var fake = Session();
        fake.ReadPlcLibraryContextsThrows = new InvalidOperationException("library manager not readable");

        string result = new BuildCoordinator(fake).BuildPlc(null);

        Assert.StartsWith("[Note] Library check skipped: library manager not readable", result);
        Assert.Contains("Build succeeded", result);
        Assert.Contains("BuildPlc(<null>)", fake.CallLog);
    }

    [Fact]
    public void NoSolution_NoLibraryCheck()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };

        string result = new BuildCoordinator(fake).BuildPlc(null);

        Assert.StartsWith("[Error]", result);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ReadPlcLibraryContexts", StringComparison.Ordinal));
    }

    [Fact]
    public void Classify_IsSharedWithCheckLibrariesInstalled()
    {
        var items = LibraryCoordinator.Classify(
            [Ref("A", "1.0"), Ref("B", "2.0"), Ref("C", "*")],
            [Lib("A", "1.0"), Lib("B", "1.9"), Lib("C", "5.0")]);

        Assert.Equal(
            [AvailabilityStatus.Installed, AvailabilityStatus.VersionMismatch, AvailabilityStatus.Installed],
            items.Select(i => i.Status));
    }
}
