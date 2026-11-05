using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>The XAE is brought to the foreground only when a call asks for it (bringToFront), never by default.</summary>
public class BringToFrontTests
{
    private static FakeTwinCatSession OpenFake() => new() { IsConnected = true, SolutionOpen = true };

    [Fact]
    public void OpenObject_ByDefault_DoesNotAskForTheForeground()
    {
        var fake = OpenFake();
        new PlcCodeCoordinator(fake).OpenObject("^POUs^MAIN");
        Assert.Contains("OpenObject(^POUs^MAIN)", fake.CallLog);
    }

    [Fact]
    public void OpenObject_WithBringToFront_PassesItOn()
    {
        var fake = OpenFake();
        new PlcCodeCoordinator(fake).OpenObject("^POUs^MAIN", bringToFront: true);
        Assert.Contains("OpenObject(^POUs^MAIN,front)", fake.CallLog);
    }

    [Fact]
    public void OpenPlcObject_NotRequested_SaysTheXaeWasLeftWhereItIs()
    {
        var fake = OpenFake();
        fake.FakeOpenPlcObjectResult = fake.FakeOpenPlcObjectResult with { ForegroundRequested = false, ForegroundConfirmed = false };

        var (summary, _) = new PlcCodeCoordinator(fake).OpenPlcObject("^POUs^MAIN");

        Assert.Contains("OpenPlcObject(^POUs^MAIN)", fake.CallLog);
        Assert.Contains("XAE left where it is (brought to the foreground only with bringToFront=true)", summary);
    }

    [Fact]
    public void GotoLine_PassesBringToFrontOn()
    {
        var fake = OpenFake();
        new EditorNavigationCoordinator(fake).GotoLine("^POUs^MAIN", 3, "implementation", bringToFront: true);
        Assert.Contains(fake.CallLog, c => c.StartsWith("GotoLine(^POUs^MAIN, 3, ", StringComparison.Ordinal) && c.EndsWith(",front)", StringComparison.Ordinal));
    }
}
