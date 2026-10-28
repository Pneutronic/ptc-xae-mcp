using PtcXaeMcp.WinForms.Theme;
using PtcXaeMcp.WinForms.Updates;

namespace PtcXaeMcp.Tests;

/// <summary>
/// The opt-in update check: pure parsing/evaluation and the policy precedence. No network here;
/// the one request lives in <c>UpdateCheck.FetchLatestLocationAsync</c> and is exercised by hand.
/// </summary>
public sealed class UpdateCheckTests
{
    // ---- tag / version parsing -------------------------------------------------------------

    [Theory]
    [InlineData("https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.3.0", "2.3.0")]
    [InlineData("https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/2.10.1", "2.10.1")]
    [InlineData("/Pneutronic/ptc-xae-mcp/releases/tag/v3.0", "3.0.0")]
    [InlineData("v2.3.0", "2.3.0")]
    [InlineData(" 2.3.0 ", "2.3.0")]
    public void ParseTagVersion_ReadsTheVersionFromTagsAndTagUrls(string input, string expected)
    {
        UpdateCheck.ParseTagVersion(input).Should().Be(Version.Parse(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://github.com/Pneutronic/ptc-xae-mcp/releases")] // no release yet: redirect to the list
    [InlineData("https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/nightly")]
    [InlineData("https://evil.example/releases/tag/v99.0.0/../../x")]
    [InlineData("vabc")]
    public void ParseTagVersion_RejectsEverythingThatIsNotAReleaseTag(string? input)
    {
        UpdateCheck.ParseTagVersion(input).Should().BeNull();
    }

    [Theory]
    [InlineData("2.3.0", "2.3.0")]
    [InlineData("2.3.0+9af618a", "2.3.0")]
    [InlineData("2.4.0-rc.1+abc", "2.4.0")]
    [InlineData("2.3", "2.3.0")]
    public void ParseCurrent_StripsBuildMetadataAndPreRelease(string input, string expected)
    {
        UpdateCheck.ParseCurrent(input).Should().Be(Version.Parse(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    [InlineData("")]
    public void ParseCurrent_UnknownVersionIsNull(string? input)
    {
        UpdateCheck.ParseCurrent(input).Should().BeNull();
    }

    // ---- evaluation -------------------------------------------------------------------------

    [Fact]
    public void Evaluate_NewerRelease_IsUpdateAvailableWithTheReleaseUrl()
    {
        var o = UpdateCheck.Evaluate("2.3.0+abc", "https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.4.0");
        o.Kind.Should().Be(UpdateCheckKind.UpdateAvailable);
        o.Latest.Should().Be(new Version(2, 4, 0));
        o.Url.Should().Be("https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.4.0");
        o.Message.Should().Contain("2.4.0").And.Contain("2.3.0");
    }

    [Theory]
    [InlineData("2.3.0", "v2.3.0")]
    [InlineData("2.4.0+local", "v2.3.0")] // a local build ahead of the release is "up to date", never a downgrade prompt
    public void Evaluate_SameOrNewerInstalled_IsUpToDate(string current, string tag)
    {
        var o = UpdateCheck.Evaluate(current, $"https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/{tag}");
        o.Kind.Should().Be(UpdateCheckKind.UpToDate);
        o.Url.Should().Be(UpdateCheck.ReleasesUrl);
    }

    [Fact]
    public void Evaluate_NoLocationOrUnknownCurrent_IsUnavailableNotAnError()
    {
        UpdateCheck.Evaluate("2.3.0", null).Kind.Should().Be(UpdateCheckKind.Unavailable);
        UpdateCheck.Evaluate("unknown", "https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.4.0")
            .Kind.Should().Be(UpdateCheckKind.Unavailable);
    }

    // ---- policy precedence ------------------------------------------------------------------

    [Theory]
    [InlineData(1, true)]
    [InlineData(1L, true)]
    [InlineData("1", true)]
    [InlineData(0, false)]
    [InlineData("yes", false)]
    [InlineData(null, false)]
    public void IsDisabledByPolicy_OnlyAOneDisables(object? value, bool expected)
    {
        UpdateCheck.IsDisabledByPolicy(value).Should().Be(expected);
    }

    [Fact]
    public void IsEnabled_RequiresOptInAndNoPolicyAndNoEnvOverride()
    {
        static string? None(string _) => null;
        static string? Zero(string n) => n == UpdateCheck.EnvVar ? " 0 " : null;

        UpdateCheck.IsEnabled(userOptIn: false, disabledByPolicy: false, None).Should().BeFalse("default is off");
        UpdateCheck.IsEnabled(userOptIn: true, disabledByPolicy: false, None).Should().BeTrue();
        UpdateCheck.IsEnabled(userOptIn: true, disabledByPolicy: true, None).Should().BeFalse("policy wins");
        UpdateCheck.IsEnabled(userOptIn: true, disabledByPolicy: false, Zero).Should().BeFalse("env=0 wins");
        UpdateCheck.IsEnabled(userOptIn: true, disabledByPolicy: false, n => n == UpdateCheck.EnvVar ? "1" : null)
            .Should().BeTrue("env=1 is not an opt-in by itself, but does not disable");
    }

    [Fact]
    public void DescribeState_NamesWhoSwitchedItOff()
    {
        static string? None(string _) => null;
        UpdateCheck.DescribeState(true, true, None).Should().Be("off (machine policy)");
        UpdateCheck.DescribeState(true, false, n => n == UpdateCheck.EnvVar ? "0" : null).Should().Be("off (PTC_XAE_MCP_UPDATE_CHECK=0)");
        UpdateCheck.DescribeState(true, false, None).Should().Be("on");
        UpdateCheck.DescribeState(false, false, None).Should().Be("off");
    }

    // ---- ui.json ----------------------------------------------------------------------------

    [Fact]
    public void UiSettings_CheckForUpdates_DefaultsOffAndRoundTrips()
    {
        UiSettings.Parse("{\"theme\":\"dark\"}").CheckForUpdates.Should().BeFalse("an old ui.json means no opt-in");
        UiSettings.Parse("{\"checkForUpdates\":\"yes\"}").CheckForUpdates.Should().BeFalse("only a JSON bool counts");

        var s = new UiSettings { CheckForUpdates = true };
        UiSettings.Parse(s.ToJson()).CheckForUpdates.Should().BeTrue();
        UiSettings.Parse(new UiSettings().ToJson()).CheckForUpdates.Should().BeFalse();
    }
}

/// <summary>
/// The one real request, opt-in like the feature itself: set <c>PTC_XAE_MCP_UPDATE_CHECK_LIVE=1</c>
/// to run it (needs github.com). Guards the HEAD-without-redirect assumption against GitHub changing
/// the latest-release redirect.
/// </summary>
public sealed class UpdateCheckLiveTests
{
    [SkippableFact]
    public async Task LatestReleaseRedirect_CarriesAParsableTag()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("PTC_XAE_MCP_UPDATE_CHECK_LIVE") == "1",
            "Set PTC_XAE_MCP_UPDATE_CHECK_LIVE=1 to send the one HEAD request to github.com.");

        string? location = await UpdateCheck.FetchLatestLocationAsync("0.0.1", CancellationToken.None);
        location.Should().StartWith(UpdateCheck.ReleasesUrl + "/tag/");
        UpdateCheck.ParseTagVersion(location).Should().NotBeNull();

        var outcome = await UpdateCheck.RunAsync("0.0.1");
        outcome.Kind.Should().Be(UpdateCheckKind.UpdateAvailable, "0.0.1 is older than any release");
        outcome.Url.Should().Be(location);
    }
}
