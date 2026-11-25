using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;
using System.Text.Json;

namespace PtcXaeMcp.Tests;

// Issue #58: the active platform is reported, and one that cannot fit the target is caught before activation.
public class PlatformFitTests
{
    private const string Umr = "192.168.250.20.1.1";

    private static ProjectTargetInfo Info(string? platform, string? netId) =>
        new(platform, netId, [Umr], ["127.0.0.1.1.1", "10.0.0.5.1.1"]);

    [Theory]
    [InlineData("ARMV7-A")]
    [InlineData("TwinCAT CE7 (ARMV7)")]
    public void UmrWithArmPlatform_IsRefused(string platform)
    {
        var fit = PlatformFit.Check(Info(platform, Umr));

        Assert.Equal(PlatformFitKind.Refuse, fit.Kind);
        Assert.Contains("User Mode Runtime", fit.Message);
        Assert.Contains("twincat_set_target", fit.Message);
        Assert.Contains("TwinCAT OS (x64)", fit.Message);
    }

    [Fact]
    public void UmrWithOsX64_IsOk() =>
        Assert.Equal(PlatformFitKind.Ok, PlatformFit.Check(Info("TwinCAT OS (x64)", Umr)).Kind);

    [Fact]
    public void UmrWithKernelRuntimePlatform_WarnsOnly() =>
        Assert.Equal(PlatformFitKind.Warn, PlatformFit.Check(Info("TwinCAT RT (x64)", Umr)).Kind);

    [Theory]
    [InlineData("127.0.0.1.1.1")]
    [InlineData("10.0.0.5.1.1")]
    [InlineData("0.0.0.0.0.0")]
    [InlineData("")]
    public void LocalPcWithArmPlatform_Warns(string netId) =>
        Assert.Equal(PlatformFitKind.Warn, PlatformFit.Check(Info("ARMV7-A", netId)).Kind);

    [Fact]
    public void RemoteDeviceWithArmPlatform_IsNotJudged() =>
        Assert.Equal(PlatformFitKind.Ok, PlatformFit.Check(Info("ARMV7-A", "5.12.34.56.1.1")).Kind);

    [Fact]
    public void UnknownPlatform_IsNotJudged() =>
        Assert.Equal(PlatformFitKind.Ok, PlatformFit.Check(Info(null, Umr)).Kind);

    [Fact]
    public void NetIdComparison_IgnoresCase() =>
        Assert.Equal(PlatformFitKind.Refuse,
            PlatformFit.Check(new ProjectTargetInfo("ARMV7-A", "AB.1.1.1.1.1", ["ab.1.1.1.1.1"], [])).Kind);

    // ── twincat_activate_config ─────────────────────────────────────────────

    [Fact]
    public void ActivateConfig_UmrWithArmPlatform_RefusedBeforeActivation()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true, SolutionOpen = true,
            FakeProjectTarget = Info("ARMV7-A", Umr),
        };

        var result = new BuildCoordinator(fake).ActivateConfig();

        Assert.StartsWith("[Error]", result);
        Assert.Contains("ARMV7-A", result);
        Assert.Contains("Nothing was activated", result);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ActivateConfig(", StringComparison.Ordinal));
    }

    [Fact]
    public void ActivateConfig_FittingPlatform_ActivatesAndNamesThePlatform()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true, SolutionOpen = true,
            FakeProjectTarget = Info("TwinCAT OS (x64)", Umr),
            FakeActivateResult = new ActivateResult(true, "Activated successfully."),
        };

        var result = new BuildCoordinator(fake).ActivateConfig();

        Assert.StartsWith("Activated", result);
        Assert.Contains("Platform: TwinCAT OS (x64)", result);
        Assert.Contains(Umr, result);
        Assert.DoesNotContain("[Warn]", result);
    }

    [Fact]
    public void ActivateConfig_WarnVerdict_ActivatesWithWarning()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true, SolutionOpen = true,
            FakeProjectTarget = Info("TwinCAT RT (x64)", Umr),
        };

        var result = new BuildCoordinator(fake).ActivateConfig();

        Assert.StartsWith("Activated", result);
        Assert.Contains("[Warn]", result);
    }

    [Fact]
    public void ActivateConfig_PlatformUnreadable_StillActivates()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true, SolutionOpen = true,
            ReadProjectTargetThrows = new InvalidOperationException("no system manager"),
        };

        var result = new BuildCoordinator(fake).ActivateConfig();

        Assert.False(result.StartsWith("[Error]", StringComparison.Ordinal), result);
    }

    // ── twincat_get_active_project ──────────────────────────────────────────

    [Fact]
    public void GetActiveProject_ReportsPlatformTargetAndWarning()
    {
        var fake = new FakeTwinCatSession
        {
            SolutionOpen = true,
            ActiveSlnPath = @"C:\repos\P\P.sln",
            FakeProjectTarget = Info("ARMV7-A", Umr),
        };

        var json = JsonDocument.Parse(new ActiveProjectCoordinator(fake).GetActiveProject()).RootElement;

        Assert.Equal("ARMV7-A", json.GetProperty("targetPlatform").GetString());
        Assert.Equal(Umr, json.GetProperty("targetNetId").GetString());
        Assert.Contains("User Mode Runtime", json.GetProperty("platformWarning").GetString());
    }

    [Fact]
    public void GetActiveProject_FittingPlatform_HasNoWarning()
    {
        var fake = new FakeTwinCatSession
        {
            SolutionOpen = true,
            ActiveSlnPath = @"C:\repos\P\P.sln",
            FakeProjectTarget = Info("TwinCAT OS (x64)", Umr),
        };

        var json = JsonDocument.Parse(new ActiveProjectCoordinator(fake).GetActiveProject()).RootElement;

        Assert.Equal("TwinCAT OS (x64)", json.GetProperty("targetPlatform").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("platformWarning").ValueKind);
    }

    [Fact]
    public void GetActiveProject_SolutionClosed_ReadsNoTarget()
    {
        var fake = new FakeTwinCatSession { SolutionOpen = false, FakeProjectTarget = Info("ARMV7-A", Umr) };

        var json = JsonDocument.Parse(new ActiveProjectCoordinator(fake).GetActiveProject()).RootElement;

        Assert.Equal(JsonValueKind.Null, json.GetProperty("targetPlatform").ValueKind);
        Assert.DoesNotContain("ReadProjectTarget()", fake.CallLog);
    }
}
