using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>"Check all objects": waiting for the end, the verdict and the result text.</summary>
public class CheckAllObjectsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    // Plays the samples back on a fake clock (one interval per read); the last sample repeats.
    private static (bool Finished, int Reads) Wait(params CheckAllObjectsSample?[] samples)
    {
        var t = TimeSpan.Zero;
        int reads = 0;
        bool finished = CheckAllObjectsWait.Until(
            () => samples[Math.Min(reads++, samples.Length - 1)],
            timeout: TimeSpan.FromSeconds(30),
            interval: TimeSpan.FromMilliseconds(500),
            elapsed: () => t,
            sleep: d => t += d);
        return (finished, reads);
    }

    [Fact]
    public void Wait_EndsWhenTheSameSampleIsReadThreeTimes()
    {
        var done = new CheckAllObjectsSample(true, 3, 900);
        var (finished, reads) = Wait(new CheckAllObjectsSample(false, 0, 100), new CheckAllObjectsSample(false, 1, 400), done, done, done);
        Assert.True(finished);
        Assert.Equal(5, reads);
    }

    [Fact]
    public void Wait_ARejectedRead_StartsOver()
    {
        var done = new CheckAllObjectsSample(false, 0, 10);
        var (finished, reads) = Wait(done, done, null, done, done, done);
        Assert.True(finished);
        Assert.Equal(6, reads);
    }

    [Fact]
    public void Wait_AGrowingOutput_IsNotFinished_UntilTheTimeout()
    {
        int length = 0;
        var t = TimeSpan.Zero;
        bool finished = CheckAllObjectsWait.Until(
            () => new CheckAllObjectsSample(true, 0, length += 10),
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500), () => t, d => t += d);
        Assert.False(finished);
    }

    [Fact]
    public void Verdict_ErrorsInTheList_Fail()
    {
        var errors = new[]
        {
            new BuildError("error", "Identifier 'nNotDeclared' not defined", "POUs/F_Unreached.TcPOU", 1),
            new BuildError("warning", "unused", null, null),
        };
        Assert.Equal("1 error(s) in the PLC project's objects.", CheckAllObjectsVerdict.FailureReason(errors, null, true, Timeout));
    }

    [Fact]
    public void Verdict_ASummaryWithErrors_FailsEvenWithoutListedErrors() =>
        Assert.Equal("the compiler reports 2 errors, 0 warnings.",
            CheckAllObjectsVerdict.FailureReason([], "2 errors, 0 warnings", true, Timeout));

    [Fact]
    public void Verdict_NotFinished_Fails() =>
        Assert.StartsWith("the check had not finished after 10 min", CheckAllObjectsVerdict.FailureReason([], null, false, Timeout));

    [Fact]
    public void Verdict_WarningsOnly_Pass() =>
        Assert.Null(CheckAllObjectsVerdict.FailureReason([new BuildError("warning", "unused", null, null)], "0 errors, 1 warnings", true, Timeout));

    [Fact]
    public void Coordinator_NoSolution_IsRefused_WithoutRunningTheCheck()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };

        string result = new BuildCoordinator(fake).CheckAllObjects(null);

        Assert.StartsWith("[Error]", result);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("CheckAllPlcObjects", StringComparison.Ordinal));
    }

    [Fact]
    public void Coordinator_Errors_AreListedUnderAFailedVerdict()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            FakeCheckAllObjectsResult = new BuildResult(false, 1,
                [new BuildError("error", "Identifier 'nNotDeclared' not defined", "POUs/F_Unreached.TcPOU", 1)],
                FailureReason: "1 error(s) in the PLC project's objects."),
        };

        string result = new BuildCoordinator(fake).CheckAllObjects("FixturePlc");

        Assert.Contains("CheckAllPlcObjects(FixturePlc)", fake.CallLog);
        Assert.Contains("[Error] Check all objects failed.", result);
        Assert.Contains("Reason: 1 error(s) in the PLC project's objects.", result);
        Assert.Contains("[ERROR] (POUs/F_Unreached.TcPOU:1) Identifier 'nNotDeclared' not defined", result);
    }

    [Fact]
    public void Coordinator_NoErrors_SaysSo()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };

        string result = new BuildCoordinator(fake).CheckAllObjects(null);

        Assert.Contains("Check all objects: no errors", result);
        Assert.DoesNotContain("[Error]", result);
    }

    [Fact]
    public void Coordinator_ASessionFailure_IsAnError()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            BuildThrows = new InvalidOperationException("could not select the PLC project"),
        };

        string result = new BuildCoordinator(fake).CheckAllObjects(null);

        Assert.Contains("[Error] could not select the PLC project", result);
    }
}
