using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class RuntimeControlCoordinatorTests
{
    // ── BroadcastSearch ────────────────────────────────────────────────────────

    [Fact]
    public void BroadcastSearch_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };
        var result = new RuntimeControlCoordinator(fake).BroadcastSearch(null);
        Assert.StartsWith("[Error]", result);
        Assert.DoesNotContain("BroadcastSearch", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void BroadcastSearch_NoSolution_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        var result = new RuntimeControlCoordinator(fake).BroadcastSearch(null);
        Assert.StartsWith("[Error]", result);
    }

    [Fact]
    public void BroadcastSearch_HappyPath_ReturnsJsonArray()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.FakeBroadcastTargets.Add(new BroadcastTarget("CX-001", "1.2.3.4.1.1", "1.2.3.4", "3.1.4026", "Windows 10"));

        var result = new RuntimeControlCoordinator(fake).BroadcastSearch(null);

        Assert.Contains("CX-001", result);
        Assert.Contains("1.2.3.4.1.1", result);
        Assert.Contains("BroadcastSearch(target=<null>)", fake.CallLog);
    }

    [Fact]
    public void BroadcastSearch_WithTarget_PassesTargetToSession()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        _ = new RuntimeControlCoordinator(fake).BroadcastSearch("192.168.1.5");
        Assert.Contains("BroadcastSearch(target=192.168.1.5)", fake.CallLog);
    }

    [Fact]
    public void BroadcastSearch_SessionThrows_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.BroadcastSearchThrows = new InvalidOperationException("COM error");
        var result = new RuntimeControlCoordinator(fake).BroadcastSearch(null);
        Assert.StartsWith("[Error]", result);
        Assert.Contains("COM error", result);
    }

    // ── AddRoute ───────────────────────────────────────────────────────────────

    [Fact]
    public void AddRoute_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };
        var result = new RuntimeControlCoordinator(fake).AddRoute("1.2.3.4.1.1", "1.2.3.4", null, null, null, true);
        Assert.StartsWith("[Error]", result);
    }

    [Fact]
    public void AddRoute_EmptyNetId_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        var result = new RuntimeControlCoordinator(fake).AddRoute("", "1.2.3.4", null, null, null, true);
        Assert.StartsWith("[Error]", result);
        Assert.Contains("amsNetId", result);
    }

    [Fact]
    public void AddRoute_NoAddressOrHost_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        var result = new RuntimeControlCoordinator(fake).AddRoute("1.2.3.4.1.1", null, null, null, null, true);
        Assert.StartsWith("[Error]", result);
    }

    [Fact]
    public void AddRoute_HappyPath_ProjectRoute_CallsSession()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        var result = new RuntimeControlCoordinator(fake)
            .AddRoute("1.2.3.4.1.1", "1.2.3.4", null, null, null, true);
        Assert.DoesNotContain("[Error]", result);
        Assert.Contains("AddRoute(netId=1.2.3.4.1.1", fake.CallLog[0]);
        Assert.Contains("projectRoute=True", fake.CallLog[0]);
    }

    [Fact]
    public void AddRoute_HappyPath_SystemRoute_PassesCredentials()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        var result = new RuntimeControlCoordinator(fake)
            .AddRoute("1.2.3.4.1.1", null, "CX-001", "admin", "secret", false);
        Assert.DoesNotContain("[Error]", result);
        Assert.Contains("projectRoute=False", fake.CallLog[0]);
    }

    // ── SetTarget ──────────────────────────────────────────────────────────────

    [Fact]
    public void SetTarget_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };
        var result = new RuntimeControlCoordinator(fake).SetTarget("1.2.3.4.1.1", null);
        Assert.StartsWith("[Error]", result);
    }

    [Fact]
    public void SetTarget_EmptyNetId_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        var result = new RuntimeControlCoordinator(fake).SetTarget("", null);
        Assert.StartsWith("[Error]", result);
    }

    [Fact]
    public void SetTarget_HappyPath_NoPlat_CallsSession()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        _ = new RuntimeControlCoordinator(fake).SetTarget("1.2.3.4.1.1", null);
        Assert.Contains("SetTarget(netId=1.2.3.4.1.1, platform=<null>)", fake.CallLog);
    }

    [Fact]
    public void SetTarget_WithPlatform_PassesPlatformToSession()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        _ = new RuntimeControlCoordinator(fake).SetTarget("1.2.3.4.1.1", "TwinCAT OS (x64)");
        Assert.Contains("SetTarget(netId=1.2.3.4.1.1, platform=TwinCAT OS (x64))", fake.CallLog);
    }

    // ── RestartRuntime ─────────────────────────────────────────────────────────

    [Fact]
    public void RestartRuntime_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };
        var result = new RuntimeControlCoordinator(fake).RestartRuntime();
        Assert.StartsWith("[Error]", result);
    }

    [Fact]
    public void RestartRuntime_HappyPath_CallsSession()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        var result = new RuntimeControlCoordinator(fake).RestartRuntime();
        Assert.DoesNotContain("[Error]", result);
        Assert.Contains("RestartRuntime()", fake.CallLog);
    }

    [Fact]
    public void RestartRuntime_SessionThrows_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.RestartRuntimeThrows = new InvalidOperationException("COM error");
        var result = new RuntimeControlCoordinator(fake).RestartRuntime();
        Assert.StartsWith("[Error]", result);
    }
}
