using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// twincat_tcunit_results reads TcUnit's runner over ADS by known paths and types. These pin the wait for the
/// runner, the verdicts and that only failed suites' cases are read unless includePassed.
/// </summary>
public class TcUnitResultsCoordinatorTests
{
    private const string Totals = "GVL_TcUnit.TcUnitRunner.TestResults.TestSuiteResults";

    /// <summary>A finished run: suite 1 "PRG.fbMath" 2 tests 1 failed, suite 2 "PRG.fbText" 1 test passed.</summary>
    private static Dictionary<string, object> FinishedRun(uint onlineChanges = 0) => new()
    {
        ["GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished"] = true,
        ["GVL_TcUnit.TcUnitRunner.TestResults.StoredGeneralTestResults"] = true,
        ["GVL_TcUnit.TcUnitRunner.TestResults.StoredTestSuiteResults"] = true,
        [Totals + ".NumberOfTestSuites"] = (ushort)2,
        [Totals + ".NumberOfTestCases"] = (ushort)3,
        [Totals + ".NumberOfSuccessfulTestCases"] = (ushort)2,
        [Totals + ".NumberOfFailedTestCases"] = (ushort)1,
        [Totals + ".Duration"] = 0.002,
        [OnlineChangeEvidence.CounterSymbol] = onlineChanges,
        [Totals + ".TestSuiteResults[1].Name"] = "PRG.fbMath",
        [Totals + ".TestSuiteResults[1].NumberOfTests"] = (ushort)2,
        [Totals + ".TestSuiteResults[1].NumberOfFailedTests"] = (ushort)1,
        [Totals + ".TestSuiteResults[1].Duration"] = 0.001,
        [Totals + ".TestSuiteResults[2].Name"] = "PRG.fbText",
        [Totals + ".TestSuiteResults[2].NumberOfTests"] = (ushort)1,
        [Totals + ".TestSuiteResults[2].NumberOfFailedTests"] = (ushort)0,
        [Totals + ".TestSuiteResults[2].Duration"] = 0.001,
        [Totals + ".TestSuiteResults[1].TestCaseResults[1].TestName"] = "AddsUp",
        [Totals + ".TestSuiteResults[1].TestCaseResults[1].TestIsFailed"] = false,
        [Totals + ".TestSuiteResults[1].TestCaseResults[1].TestIsSkipped"] = false,
        [Totals + ".TestSuiteResults[1].TestCaseResults[1].FailureMessage"] = "",
        [Totals + ".TestSuiteResults[1].TestCaseResults[2].TestName"] = "Divides",
        [Totals + ".TestSuiteResults[1].TestCaseResults[2].TestIsFailed"] = true,
        [Totals + ".TestSuiteResults[1].TestCaseResults[2].TestIsSkipped"] = false,
        [Totals + ".TestSuiteResults[1].TestCaseResults[2].FailureMessage"] = "EXP: 2, ACT: 3, MSG: ratio",
        [Totals + ".TestSuiteResults[2].TestCaseResults[1].TestName"] = "Joins",
        [Totals + ".TestSuiteResults[2].TestCaseResults[1].TestIsFailed"] = false,
        [Totals + ".TestSuiteResults[2].TestCaseResults[1].TestIsSkipped"] = false,
        [Totals + ".TestSuiteResults[2].TestCaseResults[1].FailureMessage"] = "",
    };

    private static (FakeTwinCatSession Fake, List<AdsTypedRead> Reads) Fake(Dictionary<string, object> values)
    {
        var reads = new List<AdsTypedRead>();
        var fake = new FakeTwinCatSession
        {
            TypedValueBySymbol = r => { reads.Add(r); return values.TryGetValue(r.Symbol, out var v) ? v : null; },
        };
        return (fake, reads);
    }

    private static TcUnitResultsCoordinator Coord(FakeTwinCatSession fake) => new(fake) { PollInterval = TimeSpan.Zero };

    [Fact]
    public void FailedRun_ReportsTheVerdictTotalsAndTheFailedCase()
    {
        var (fake, reads) = Fake(FinishedRun());

        using var doc = JsonDocument.Parse(Coord(fake).Collect(null, null, 5, includePassed: false));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("failed", root.GetProperty("verdict").GetString());
        Assert.Equal(3, root.GetProperty("totals").GetProperty("tests").GetInt32());
        Assert.Equal(1, root.GetProperty("totals").GetProperty("failed").GetInt32());
        Assert.Contains("PRG.fbMath.Divides: EXP: 2, ACT: 3, MSG: ratio", root.GetProperty("message").GetString());

        var suites = root.GetProperty("suites");
        Assert.Equal(2, suites.GetArrayLength());
        var cases = suites[0].GetProperty("cases");
        Assert.Equal(1, cases.GetArrayLength());
        Assert.Equal("Divides", cases[0].GetProperty("name").GetString());
        Assert.Equal(0, suites[1].GetProperty("cases").GetArrayLength());

        // Only the failed suite's cases are read; the passed suite's are not.
        Assert.DoesNotContain(reads, r => r.Symbol.Contains("TestSuiteResults[2].TestCaseResults", StringComparison.Ordinal));
        Assert.Contains(reads, r => r.Symbol.EndsWith("TestSuiteResults[1].Name", StringComparison.Ordinal) && r.PlcType == "STRING(255)");
        Assert.Contains("net=project, port=851", fake.CallLog[0]);
    }

    [Fact]
    public void IncludePassed_ListsEveryCase()
    {
        var (fake, _) = Fake(FinishedRun());

        using var doc = JsonDocument.Parse(Coord(fake).Collect("5.6.7.8.1.1", 852, 5, includePassed: true));
        var suites = doc.RootElement.GetProperty("suites");

        Assert.Equal(2, suites[0].GetProperty("cases").GetArrayLength());
        Assert.Equal("Joins", suites[1].GetProperty("cases")[0].GetProperty("name").GetString());
        Assert.Contains("net=5.6.7.8.1.1, port=852", fake.CallLog[0]);
    }

    [Fact]
    public void PassedRun_IsPassed()
    {
        var values = FinishedRun();
        values[Totals + ".NumberOfFailedTestCases"] = (ushort)0;
        values[Totals + ".TestSuiteResults[1].NumberOfFailedTests"] = (ushort)0;
        var (fake, _) = Fake(values);

        using var doc = JsonDocument.Parse(Coord(fake).Collect(null, null, 5, includePassed: false));

        Assert.Equal("passed", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("TcUnit: 3 tests in 2 suites, 0 failed.", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void NoSuites_IsNoTests()
    {
        var values = FinishedRun();
        values[Totals + ".NumberOfTestSuites"] = (ushort)0;
        values[Totals + ".NumberOfTestCases"] = (ushort)0;
        var (fake, _) = Fake(values);

        using var doc = JsonDocument.Parse(Coord(fake).Collect(null, null, 5, includePassed: false));

        Assert.Equal("no_tests", doc.RootElement.GetProperty("verdict").GetString());
        Assert.StartsWith("[Warn]", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void OnlineChangesSinceTheDownload_WarnThatTheTestsDidNotRunAgain()
    {
        var (fake, _) = Fake(FinishedRun(onlineChanges: 2));

        using var doc = JsonDocument.Parse(Coord(fake).Collect(null, null, 5, includePassed: false));

        Assert.Equal(2, doc.RootElement.GetProperty("onlineChangeCnt").GetInt32());
        Assert.Contains("[Warn] The application took 2 online changes", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void RunnerStillStoring_IsWaitedFor()
    {
        var values = FinishedRun();
        int polls = 0;
        var fake = new FakeTwinCatSession
        {
            TypedValueBySymbol = r => r.Symbol.EndsWith("StoredTestSuiteResults", StringComparison.Ordinal)
                ? (object)(++polls >= 3)
                : values.TryGetValue(r.Symbol, out var v) ? v : null,
        };

        using var doc = JsonDocument.Parse(Coord(fake).Collect(null, null, 5, includePassed: false));

        Assert.Equal("failed", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(3, polls);
    }

    [Fact]
    public void RunnerNeverFinishing_TimesOutNamingTheFlag()
    {
        var values = FinishedRun();
        values["GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished"] = false;
        var (fake, _) = Fake(values);
        var coord = new TcUnitResultsCoordinator(fake) { PollInterval = TimeSpan.FromMilliseconds(100) };

        string result = coord.Collect(null, null, 1, includePassed: false);

        Assert.StartsWith("[Error] TcUnit did not finish within 1s", result);
        Assert.Contains("GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished not TRUE", result);
    }

    [Fact]
    public void NoTcUnitInThePlc_IsAnErrorThatSaysWhatToCheck()
    {
        var (fake, _) = Fake([]);

        string result = Coord(fake).Collect(null, null, 5, includePassed: false);

        Assert.StartsWith("[Error] TcUnit results not readable: GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished", result);
        Assert.Contains("TcUnit.RUN()", result);
        Assert.Equal(1, fake.ReadAdsValuesTypedCalls);
    }

    [Fact]
    public void ConnectionFailure_IsAnErrorWithTheTarget()
    {
        var fake = new FakeTwinCatSession { ReadAdsValuesTypedFails = "ReadAdsValuesTyped failed: no route" };

        string result = Coord(fake).Collect(null, null, 5, includePassed: false);

        Assert.Equal("[Error] ReadAdsValuesTyped failed: no route (target 1.2.3.4.1.1:851)", result);
    }

    [Theory]
    [InlineData("STRING(255)", 255)]
    [InlineData("string(80)", 80)]
    [InlineData("STRING", null)]
    [InlineData("STRING(x)", null)]
    [InlineData("UINT", null)]
    public void TypedRead_KnowsTheStringLength(string plcType, int? expected) =>
        Assert.Equal(expected, new AdsTypedRead("x", plcType).StringLength);
}
