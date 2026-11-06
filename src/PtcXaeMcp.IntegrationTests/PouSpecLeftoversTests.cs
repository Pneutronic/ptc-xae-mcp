namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Pure offline coverage for <see cref="PouSpecLeftovers.ParseLeftoverPaths"/> — the
/// one piece of issue-#94 cleanup logic that is testable without a live XAE. No
/// collection / no fixture: these never touch TwinCAT, so they run on a plain
/// <c>dotnet test</c> and never skip.
/// </summary>
public class PouSpecLeftoversTests
{
    // Shape of twincat_plc_list: summary line, then "  {kind/subtype,-24} {path}{ [lang]}".
    private const string SampleListing =
        "Found 7 elements (1 FOLDER, 6 POU).\n" +
        "  FOLDER                   TIPC^PLC^PLC Projekt^POUs^01_Application\n" +
        "  POU/PROGRAM              TIPC^PLC^PLC Projekt^POUs^MAIN [ST]\n" +
        "  POU/PROGRAM              TIPC^PLC^PLC Projekt^POUs^PRG_IntegrationScratch [ST]\n" +
        "  POU/FUNCTION_BLOCK       TIPC^PLC^PLC Projekt^POUs^FB_PouSpec_20260522131534408 [ST]\n" +
        "  POU/FUNCTION_BLOCK       TIPC^PLC^PLC Projekt^POUs^FB_PouSpec_20260522131534408_1 [ST]\n" +
        "  POU/FUNCTION_BLOCK       TIPC^PLC^PLC Projekt^POUs^FB_PouSpec_20260522131600_R [ST]\n" +
        "  POU/PROGRAM              TIPC^PLC^PLC Projekt^POUs^PRG_PouSpec_20260522131600\n";   // no language suffix

    [Trait("Description", "Offline: plc_list-Parser findet nur FB_PouSpec_*/PRG_PouSpec_* (inkl. _R/_1), ignoriert MAIN/Scratch/Folder/Summary.")]
    [Fact]
    public void ParseLeftoverPaths_PicksOnlyPouSpecNodes_IncludingRenameAndDupVariants()
    {
        var paths = PouSpecLeftovers.ParseLeftoverPaths(SampleListing);

        Assert.Equal(new[]
        {
            "TIPC^PLC^PLC Projekt^POUs^FB_PouSpec_20260522131534408",
            "TIPC^PLC^PLC Projekt^POUs^FB_PouSpec_20260522131534408_1",
            "TIPC^PLC^PLC Projekt^POUs^FB_PouSpec_20260522131600_R",
            "TIPC^PLC^PLC Projekt^POUs^PRG_PouSpec_20260522131600",
        }, paths);
    }

    [Trait("Description", "Offline: der Parser bewahrt Leerzeichen im Pfad ('PLC Projekt') und entfernt nur das nachgestellte ' [lang]'.")]
    [Fact]
    public void ParseLeftoverPaths_PreservesSpacesInPath_AndStripsLanguageSuffix()
    {
        var paths = PouSpecLeftovers.ParseLeftoverPaths(
            "  POU/FUNCTION_BLOCK       TIPC^PLC^PLC Projekt^POUs^FB_PouSpec_1 [ST]\n");

        Assert.Equal("TIPC^PLC^PLC Projekt^POUs^FB_PouSpec_1", Assert.Single(paths));
    }

    [Trait("Description", "Offline: PRG_IntegrationScratch (per Design dauerhaft) wird nie als Leftover erkannt.")]
    [Fact]
    public void ParseLeftoverPaths_NeverMatchesPermanentScratchPou()
    {
        var paths = PouSpecLeftovers.ParseLeftoverPaths(
            "  POU/PROGRAM              TIPC^PLC^PLC Projekt^POUs^PRG_IntegrationScratch [ST]\n");

        Assert.Empty(paths);
    }

    [Trait("Description", "Offline: leere Liste oder eine '[Error] ...'-Antwort liefert keine Pfade.")]
    [Theory]
    [InlineData("")]
    [InlineData("[Error] No project open.")]
    [InlineData("No matching PLC elements found.")]
    public void ParseLeftoverPaths_ReturnsEmpty_ForNoItemsOrError(string listing)
        => Assert.Empty(PouSpecLeftovers.ParseLeftoverPaths(listing));
}
