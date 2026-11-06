using System.Runtime.InteropServices;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>Issue #149 scope 4: RequireOpenSolution tells the four failure states apart.</summary>
public class SessionGuardsTests
{
    [Fact]
    public void NoProcess_ReportsNoInstance()
    {
        var s = new FakeTwinCatSession { IsConnected = false };
        Assert.StartsWith("[Error] No TwinCAT XAE instance is running.\nNext: twincat_open_project", SessionGuards.RequireOpenSolution(s));
    }

    [Fact]
    public void ProcessButDteUnreachable_ReportsUnreachable_NotNoInstance()
    {
        var s = new FakeTwinCatSession { IsConnected = false, UnreachableText = XaeReachability.Describe(1, null) };
        string? r = SessionGuards.RequireOpenSolution(s);
        Assert.StartsWith("[Error] TcXaeShell is running", r);
    }

    [Fact]
    public void BlockedByModal_NamesTheDialog_InsteadOfNoSolution()
    {
        // LIVE-7: with a modal open the guard used to answer "No solution is currently open" in 0.1 s.
        var s = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            HasOpenSolutionThrows = new XaeBlockedException(
                ComCall.BlockedByDialogMessage("Gehe zu Zeile", TimeSpan.FromSeconds(1.5), null), "Gehe zu Zeile", null),
        };
        string? r = SessionGuards.RequireOpenSolution(s);
        Assert.StartsWith("[Error] XAE is blocked by dialog 'Gehe zu Zeile'", r);
    }

    [Fact]
    public void RawBusy_IsReportedAsBusy_NotAsNoSolution()
    {
        var s = new FakeTwinCatSession
        {
            IsConnected = true,
            HasOpenSolutionThrows = new COMException("busy", unchecked((int)0x8001010A)),
        };
        string? r = SessionGuards.RequireOpenSolution(s);
        Assert.NotNull(r);
        Assert.StartsWith("[Error] XAE is", r);
        Assert.DoesNotContain("No solution", r);
    }

    [Fact]
    public void NoSolution_ReportsNoSolution()
    {
        var s = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        Assert.Equal("[Error] No solution is currently open.\nNext: twincat_open_project(slnPath) opens one; " +
                     "twincat_get_active_project shows what the XAE has.", SessionGuards.RequireOpenSolution(s));
    }

    [Fact]
    public void Ready_ReturnsNull()
    {
        var s = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        Assert.Null(SessionGuards.RequireOpenSolution(s));
    }

    [Fact]
    public void NonBusyFailure_IsNotMaskedAsBusy()
    {
        var s = new FakeTwinCatSession { IsConnected = true, HasOpenSolutionThrows = new InvalidOperationException("boom") };
        Assert.Throws<InvalidOperationException>(() => SessionGuards.RequireOpenSolution(s));
    }
}
