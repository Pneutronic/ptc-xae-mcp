using PtcXaeMcp.WinForms.TwinCat.Schema;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Coverage for the pure candidate-path generation and scoring extracted from
/// NativeTwinCatRunner (ResolvePlcTaskBasePaths, AddIoCandidates, FindPlcVarItem's and
/// FindIoLeaf's scoring, ScoreEthernetBranchPortCandidate) — Hardening 9, step 1.
/// </summary>
public class LinkCandidatesTests
{
    [Theory]
    [InlineData("K2a (EL1008)", "K2A")]
    [InlineData("  -A1 Box 3 (EK1100) ", "-A1BOX3")]
    [InlineData("NoSuffix", "NOSUFFIX")]
    [InlineData("", "")]
    public void TerminalNameKey_DropsTheTypeSuffixAndBlanks(string name, string expected) =>
        LinkCandidates.TerminalNameKey(name).Should().Be(expected);

    [Fact]
    public void ImageFolderNames_FollowTheDirection()
    {
        LinkCandidates.ImageFolderNames("PlcTask", "I").Should().Contain("PlcTask Inputs").And.NotContain(n => n.Contains("Output"));
        LinkCandidates.ImageFolderNames("Fast", "Q").Should().Contain("Fast Outputs").And.NotContain(n => n.Contains("Input"));
    }

    [Fact]
    public void IoGroupFolderNames_FollowTheDirection()
    {
        LinkCandidates.IoGroupFolderNames("I").Should().Contain("Inputs");
        LinkCandidates.IoGroupFolderNames("Q").Should().Contain("Outputs");
    }

    [Fact]
    public void AddIoCandidates_InputDirection_ProducesInputPathsAndNoDuplicates()
    {
        var result = new List<string>();
        LinkCandidates.AddIoCandidates(result, "TIID^Dev^Term", 3, "I");

        result.Should().Contain("TIID^Dev^Term^Channel 3^Input");
        result.Should().OnlyHaveUniqueItems();
        // Preserved quirk from the original AddIoCandidates: the last two candidates are
        // hardcoded "IO Inputs {channel}" / "IO Outputs {channel}" regardless of direction.
        result.Should().Contain("TIID^Dev^Term^IO Outputs Channel 3");
    }

    [Fact]
    public void AddIoCandidates_AppendsWithoutClearingExistingEntries()
    {
        var result = new List<string> { "existing" };
        LinkCandidates.AddIoCandidates(result, "TIID^Dev^Term", 0, "Q");

        result[0].Should().Be("existing");
        result.Should().Contain(p => p.Contains("Output", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Port A (Ethernet)", "path", "A", 70)]
    [InlineData("RJ45 Ethernet", "path", "A", 40)]
    [InlineData("Unrelated", "Unrelated", "A", 0)]
    public void ScoreEthernetBranchPortCandidate_HigherForBetterSignals(string name, string path, string desiredPort, int expectedAtLeast)
    {
        int score = LinkCandidates.ScoreEthernetBranchPortCandidate(name, path, desiredPort);
        score.Should().BeGreaterThanOrEqualTo(expectedAtLeast);
    }

    [Fact]
    public void ScoreEthernetBranchPortCandidate_EmptyDesiredPort_ReturnsZero()
    {
        LinkCandidates.ScoreEthernetBranchPortCandidate("Port A", "path", "").Should().Be(0);
    }

    [Theory]
    [InlineData("Channel 3", 3, true)]
    [InlineData("Kanal 3", 3, true)]
    [InlineData("IO Inputs Channel 3", 3, true)]
    [InlineData("IO Outputs Channel 3", 3, true)]
    [InlineData("Channel 4", 3, false)]
    [InlineData("Unrelated", 3, false)]
    public void IsIoLeafChannelMatch_MatchesChannelNumberInNameOrPath(string name, int channel, bool expected)
    {
        LinkCandidates.IsIoLeafChannelMatch(name, "", channel).Should().Be(expected);
    }

    [Fact]
    public void ScoreIoLeafCandidate_ExactOverrideName_ScoresHighest()
    {
        LinkCandidates.ScoreIoLeafCandidate("MyLeaf", 0, "I", "MyLeaf").Should().Be(200);
    }

    [Fact]
    public void ScoreIoLeafCandidate_InputDirection_InputExactNameHighest()
    {
        LinkCandidates.ScoreIoLeafCandidate("Input", 0, "I", "").Should().Be(120);
    }

    [Fact]
    public void ScoreIoLeafCandidate_OutputDirection_OutputExactNameHighest()
    {
        LinkCandidates.ScoreIoLeafCandidate("Output", 0, "Q", "").Should().Be(120);
    }

    [Fact]
    public void ScoreIoLeafCandidate_NoPositiveSignal_ReturnsMinusOne()
    {
        LinkCandidates.ScoreIoLeafCandidate("Unrelated", 0, "I", "").Should().Be(-1);
    }
}
