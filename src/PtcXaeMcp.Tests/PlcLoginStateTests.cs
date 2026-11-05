using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>Issue #148 (LIVE-4) — the online state is never inferred from a disabled Login command.</summary>
public class PlcLoginStateTests
{
    private const string PouPath = "TIPC^Plc^Plc Project^POUs^FB_A";

    private static FakeTwinCatSession OpenFake() => new() { IsConnected = true, SolutionOpen = true };

    // =====================================================================
    // Honest login state (LIVE-4)
    // =====================================================================

    [Theory]
    [InlineData(true,  false, "Proceed")]
    [InlineData(false, true,  "AlreadyOnline")]
    [InlineData(null,  true,  "AlreadyOnline")]
    [InlineData(false, false, "NoTarget")]   // LIVE-4: was reported as "already online"
    [InlineData(null,  false, "Unknown")]
    [InlineData(true,  null,  "Unknown")]
    [InlineData(false, null,  "Unknown")]
    public void Login_Precheck_NeverInfersOnlineFromDisabledLogin(bool? login, bool? logout, string expected) =>
        Assert.Equal(expected, PlcOnlineInference.BeforeLogin(login, logout).ToString());

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, null)]
    public void OnlineState_ComesFromLogoutOnly(bool? logoutAvailable, bool? expectedOnline) =>
        Assert.Equal(expectedOnline, PlcOnlineInference.IsOnline(logoutAvailable));

    // =====================================================================
    // Waiting for the state after a Login/Logout command
    // =====================================================================

    private static readonly PlcOnlineFlags Online = new(Login: false, Logout: true);
    private static readonly PlcOnlineFlags Offline = new(Login: true, Logout: false);
    private static readonly PlcOnlineFlags BothDisabled = new(Login: false, Logout: false);

    // Plays the readings back on a fake clock (one interval per read); the last reading repeats.
    private static (PlcOnlineSettled Result, int Reads) Settle(bool expectOnline, params PlcOnlineFlags?[] readings)
    {
        var t = TimeSpan.Zero;
        int reads = 0;
        var result = PlcOnlineSettle.Wait(
            () => readings[Math.Min(reads++, readings.Length - 1)],
            expectOnline,
            timeout: TimeSpan.FromSeconds(10),
            minWaitForOther: TimeSpan.FromSeconds(3),
            interval: TimeSpan.FromMilliseconds(250),
            elapsed: () => t,
            sleep: d => t += d);
        return (result, reads);
    }

    [Fact]
    public void Logout_WaitsThroughTheTransition_AndSettlesOffline()
    {
        // live 2026-11-05: still online, rejected calls, both disabled, then offline
        var (r, reads) = Settle(expectOnline: false, Online, null, BothDisabled, Offline, Offline);
        Assert.Equal(new PlcOnlineSettled(false, true), r);
        Assert.Equal(5, reads);
    }

    [Fact]
    public void Login_IgnoresTheOfflineStateBeforeTheModal()
    {
        // Before the XAE raises the login modal the old offline state reads stable.
        var (r, _) = Settle(expectOnline: true, Offline, Offline, null, BothDisabled, Online, null, Online, Online);
        Assert.Equal(new PlcOnlineSettled(true, true), r);
    }

    [Fact]
    public void Login_ThatStaysOffline_SettlesOffline_OnlyAfterTheMinimumWait()
    {
        var (r, reads) = Settle(expectOnline: true, Offline);
        Assert.Equal(new PlcOnlineSettled(false, true), r);
        Assert.Equal(13, reads);   // 3 s at 250 ms: the read at t = 3.0 s
    }

    [Fact]
    public void ATransitionalRead_RestartsTheCount()
    {
        var (r, reads) = Settle(expectOnline: true, Online, null, Online, Online);
        Assert.Equal(new PlcOnlineSettled(true, true), r);
        Assert.Equal(4, reads);
    }

    [Fact]
    public void ANeverReadableState_TimesOut_Unsettled()
    {
        var (r, _) = Settle(expectOnline: false, (PlcOnlineFlags?)null);
        Assert.Equal(new PlcOnlineSettled(null, false), r);
    }

    [Fact]
    public void BothDisabledToTheEnd_TimesOut_WithTheLogoutFlag()
    {
        var (r, _) = Settle(expectOnline: false, Online, BothDisabled);
        Assert.Equal(new PlcOnlineSettled(false, false), r);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(null, false, true)]
    [InlineData(true, null, true)]
    public void InTransition_IsBothDisabledOrUnreadable(bool? login, bool? logout, bool expected) =>
        Assert.Equal(expected, new PlcOnlineFlags(login, logout).InTransition);
}
