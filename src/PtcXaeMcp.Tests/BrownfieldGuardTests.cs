using PtcXaeMcp.WinForms;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Unit coverage for the brownfield-guard fail-closed decision functions.
/// COM-free: the live-XAE behaviour is exercised by the integration layer.
/// </summary>
public class BrownfieldGuardTests
{
    // ---- H1: fail-closed empty-check -------------------------------------------------

    [Fact]
    public void ReadChildCount_Lenient_SwallowsComErrorAndReturnsZero()
    {
        int value = BrownfieldGuard.ReadChildCount(
            () => throw new InvalidOperationException("COM RPC_E_DISCONNECTED"),
            failClosed: false);

        value.Should().Be(0, "lenient enumeration loops keep the historical return-0 tolerance");
    }

    [Fact]
    public void ReadChildCount_FailClosed_RethrowsComError()
    {
        Action act = () => BrownfieldGuard.ReadChildCount(
            () => throw new InvalidOperationException("COM RPC_E_DISCONNECTED"),
            failClosed: true);

        act.Should().Throw<InvalidOperationException>(
            "on the guard path a COM failure is uncertainty and must surface, never coerce to 0");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void ReadChildCount_SuccessfulRead_ReturnsValueInBothModes(int count)
    {
        BrownfieldGuard.ReadChildCount(() => count, failClosed: false).Should().Be(count);
        BrownfieldGuard.ReadChildCount(() => count, failClosed: true).Should().Be(count);
    }

    [Fact]
    public void AssertMasterEmpty_EmptyMaster_DoesNotThrow()
    {
        Action act = () => BrownfieldGuard.AssertMasterEmpty(0, "Device 1 (EtherCAT)");
        act.Should().NotThrow();
    }

    [Fact]
    public void AssertMasterEmpty_PopulatedMaster_Throws()
    {
        Action act = () => BrownfieldGuard.AssertMasterEmpty(3, "Device 1 (EtherCAT)");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Device 1 (EtherCAT)*")
            .WithMessage("*3*");
    }

    // ---- H2: exact master addressing -------------------------------------------------

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, "", false)]
    [InlineData(false, "   ", false)]
    [InlineData(false, "TIID^Device 1 (EtherCAT)", true)]
    [InlineData(true, "TIID^Device 1 (EtherCAT)", true)]
    public void IsStrict_ActiveWhenFailClosedOrPathPinned(bool failClosed, string? path, bool expected)
    {
        BrownfieldGuard.IsStrict(failClosed, path).Should().Be(expected);
    }

    [Fact]
    public void SelectMaster_ExactMatch_WinsInBothModes()
    {
        var candidates = new[] { "Device 1 (EtherCAT)", "Device 2 (EtherCAT)" };

        BrownfieldGuard.SelectMaster("Device 2 (EtherCAT)", candidates, strict: false)
            .Should().Be("Device 2 (EtherCAT)");
        BrownfieldGuard.SelectMaster("Device 2 (EtherCAT)", candidates, strict: true)
            .Should().Be("Device 2 (EtherCAT)");
    }

    [Fact]
    public void SelectMaster_ExactMatch_IsCaseInsensitive()
    {
        var candidates = new[] { "Device 1 (EtherCAT)" };

        BrownfieldGuard.SelectMaster("device 1 (ethercat)", candidates, strict: true)
            .Should().Be("Device 1 (EtherCAT)");
    }

    [Fact]
    public void SelectMaster_Lenient_FallsBackToFirstCandidate()
    {
        var candidates = new[] { "Device 1 (EtherCAT)", "Device 2 (EtherCAT)" };

        BrownfieldGuard.SelectMaster("Nonexistent", candidates, strict: false)
            .Should().Be("Device 1 (EtherCAT)", "greenfield keeps the candidates[0] fallback");
    }

    [Fact]
    public void SelectMaster_Strict_NoExactMatch_Throws()
    {
        var candidates = new[] { "Device 1 (EtherCAT)", "Device 2 (EtherCAT)" };

        Action act = () => BrownfieldGuard.SelectMaster("Packaging Master", candidates, strict: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Packaging Master*")
            .WithMessage("*Fail-closed*");
    }

    [Fact]
    public void AllowFuzzyDeviceMatch_OnlyLenient()
    {
        BrownfieldGuard.AllowFuzzyDeviceMatch(strict: false).Should().BeTrue();
        BrownfieldGuard.AllowFuzzyDeviceMatch(strict: true).Should().BeFalse();
    }

    // ---- H4: build controllable ------------------------------------------------------

    [Fact]
    public void RunBuildIfEnabled_AutoBuildOff_DoesNotInvokeBuildAndReturnsFalse()
    {
        bool built = false;

        bool ran = BrownfieldGuard.RunBuildIfEnabled(autoBuild: false, () => built = true);

        ran.Should().BeFalse();
        built.Should().BeFalse("autoBuild=false must never call SolutionBuild.Build(true)");
    }

    [Fact]
    public void RunBuildIfEnabled_AutoBuildOn_InvokesBuildAndReturnsTrue()
    {
        bool built = false;

        bool ran = BrownfieldGuard.RunBuildIfEnabled(autoBuild: true, () => built = true);

        ran.Should().BeTrue();
        built.Should().BeTrue();
    }

    [Fact]
    public void RunBuildIfEnabled_AutoBuildOn_PropagatesBuildFailure()
    {
        Action act = () => BrownfieldGuard.RunBuildIfEnabled(
            autoBuild: true, () => throw new InvalidOperationException("Kompilierfehler"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Kompilierfehler*");
    }

    // ---- MCP surface: brownfield request validation ----------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TIID^Device 1 (EtherCAT)")]
    [InlineData("TIID^Device 2 (EtherCAT)^Box 1")]
    public void ValidateBrownfieldRequest_WellFormedOrAbsent_NoIssues(string? path)
    {
        BrownfieldGuard.ValidateBrownfieldRequest(path).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Device 1 (EtherCAT)")]   // not anchored under TIID
    [InlineData("TIID")]                  // bare root, addresses no device
    [InlineData("TIID^")]                 // empty segment after the root
    [InlineData("TIPC^PLC")]              // wrong subtree
    public void ValidateBrownfieldRequest_MalformedPath_ReturnsIssue(string path)
    {
        var issues = BrownfieldGuard.ValidateBrownfieldRequest(path);

        issues.Should().ContainSingle()
            .Which.Should().Contain("targetMasterPath").And.Contain("TIID");
    }
}
