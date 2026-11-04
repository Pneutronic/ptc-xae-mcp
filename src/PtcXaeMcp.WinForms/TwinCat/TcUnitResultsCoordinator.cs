using System.Globalization;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Reads the outcome of a TcUnit run from the running PLC over ADS. TcUnit (a PLC library the project
/// references) runs its test suites once after the application starts and keeps the results in its runner
/// instance <c>GVL_TcUnit.TcUnitRunner</c>. This waits until the runner has finished and stored them, then
/// reads the totals, every suite and — for failed suites, or all with includePassed — every test case.
/// Deploying the test application stays with the caller (build, then activate or log in with download).
/// All reads go by variable handle with known types: the runner holds arrays of 1000 suites × 100 cases,
/// which a symbol-table search per symbol cannot walk in reasonable time.
/// </summary>
internal sealed class TcUnitResultsCoordinator(ITwinCatSession session)
{
    internal const string Runner = "GVL_TcUnit.TcUnitRunner";
    internal const string FinishedSymbol = Runner + ".AllTestSuitesFinished";
    private const string Results = Runner + ".TestResults";
    private const string Totals = Results + ".TestSuiteResults";
    private const string MaxString = "STRING(255)"; // TcUnit's T_MaxString

    // The bounds of TcUnit's result arrays (ST_TestSuiteResults / ST_TestSuiteResult).
    internal const int MaxSuites = 1000;
    internal const int MaxCasesPerSuite = 100;

    private const int DefaultAdsPort = 851;
    internal const int DefaultTimeoutSeconds = 60;
    private const int MaxTimeoutSeconds = 600;

    // The runner sets AllTestSuitesFinished first and copies the results over the following cycles.
    private static readonly string[] DoneFlags =
        [FinishedSymbol, Results + ".StoredGeneralTestResults", Results + ".StoredTestSuiteResults"];

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Gap between two polls of the runner's flags; tests set zero.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public string Collect(string? amsNetId, int? adsPort, int? timeoutSeconds, bool includePassed,
        CancellationToken cancellationToken = default)
    {
        string net = string.IsNullOrWhiteSpace(amsNetId) ? AdsTargetNetId.ProjectKeyword : amsNetId.Trim();
        int port = adsPort ?? DefaultAdsPort;
        int timeout = timeoutSeconds is null or <= 0 ? DefaultTimeoutSeconds : Math.Min(timeoutSeconds.Value, MaxTimeoutSeconds);

        string? error = WaitUntilStored(net, port, TimeSpan.FromSeconds(timeout), cancellationToken);
        if (error is not null) return error;

        var head = Read([
            new(Totals + ".NumberOfTestSuites", "UINT"),
            new(Totals + ".NumberOfTestCases", "UINT"),
            new(Totals + ".NumberOfSuccessfulTestCases", "UINT"),
            new(Totals + ".NumberOfFailedTestCases", "UINT"),
            new(Totals + ".Duration", "LREAL"),
            new(OnlineChangeEvidence.CounterSymbol, "UDINT"),
        ], net, port, out error);
        if (head is null) return error!;
        int unreadable = head.Results.Count(r => !r.Success);

        int? suiteCount = Int(head.Results[0]), tests = Int(head.Results[1]), passed = Int(head.Results[2]), failed = Int(head.Results[3]);
        double? duration = Real(head.Results[4]);
        long? onlineChanges = Int(head.Results[5]);
        int suitesToRead = Math.Clamp(suiteCount ?? 0, 0, MaxSuites);

        var suiteFields = new List<AdsTypedRead>(suitesToRead * 4);
        for (int i = 1; i <= suitesToRead; i++)
        {
            string s = Inv($"{Totals}.TestSuiteResults[{i}]");
            suiteFields.Add(new(s + ".Name", MaxString));
            suiteFields.Add(new(s + ".NumberOfTests", "UINT"));
            suiteFields.Add(new(s + ".NumberOfFailedTests", "UINT"));
            suiteFields.Add(new(s + ".Duration", "LREAL"));
        }
        IReadOnlyList<AdsBatchReadItem>? suiteItems = suiteFields.Count == 0 ? [] : Read(suiteFields, net, port, out error)?.Results;
        if (suiteItems is null) return error!;
        unreadable += suiteItems.Count(r => !r.Success);

        var suites = Enumerable.Range(0, suitesToRead).Select(i => new Suite(
            i + 1, Text(suiteItems[i * 4]), Int(suiteItems[i * 4 + 1]), Int(suiteItems[i * 4 + 2]), Real(suiteItems[i * 4 + 3]))).ToList();

        // Cases of the suites that failed (or every suite): four fields per case, one read for all of them.
        var caseOwners = new List<(Suite Suite, int Case)>();
        var caseFields = new List<AdsTypedRead>();
        foreach (var suite in suites.Where(s => includePassed || s.Failed is null or > 0))
        {
            for (int j = 1; j <= Math.Clamp(suite.Tests ?? 0, 0, MaxCasesPerSuite); j++)
            {
                string c = Inv($"{Totals}.TestSuiteResults[{suite.Index}].TestCaseResults[{j}]");
                caseOwners.Add((suite, j));
                caseFields.Add(new(c + ".TestName", MaxString));
                caseFields.Add(new(c + ".TestIsFailed", "BOOL"));
                caseFields.Add(new(c + ".TestIsSkipped", "BOOL"));
                caseFields.Add(new(c + ".FailureMessage", MaxString));
            }
        }
        IReadOnlyList<AdsBatchReadItem>? caseItems = caseFields.Count == 0 ? [] : Read(caseFields, net, port, out error)?.Results;
        if (caseItems is null) return error!;
        unreadable += caseItems.Count(r => !r.Success);
        for (int k = 0; k < caseOwners.Count; k++)
        {
            var c = new Case(Text(caseItems[k * 4]), Flag(caseItems[k * 4 + 1]), Flag(caseItems[k * 4 + 2]), Text(caseItems[k * 4 + 3]));
            if (includePassed || c.Failed != false || c.Skipped == true) caseOwners[k].Suite.Cases.Add(c);
        }

        string verdict = suiteCount is null || tests is null || failed is null ? "unknown"
            : suiteCount == 0 || tests == 0 ? "no_tests"
            : failed > 0 || suites.Any(s => s.Failed > 0) ? "failed"
            : "passed";

        return JsonSerializer.Serialize(new
        {
            success = true,
            verdict,
            message = Summary(verdict, suiteCount, tests, failed, suites, onlineChanges, unreadable),
            totals = new { suites = suiteCount, tests, passed, failed, duration },
            onlineChangeCnt = onlineChanges,
            suites = suites.Select(s => new
            {
                name = s.Name,
                tests = s.Tests,
                failed = s.Failed,
                duration = s.Duration,
                cases = s.Cases.Select(c => new { name = c.Name, failed = c.Failed, skipped = c.Skipped, message = c.Message }),
            }),
            unreadableSymbols = unreadable,
            amsNetId = head.AmsNetId,
            adsPort = port,
        }, JsonOpts);
    }

    private string? WaitUntilStored(string net, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var reads = DoneFlags.Select(s => new AdsTypedRead(s, "BOOL")).ToList();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var flags = Read(reads, net, port, out string? error);
            if (flags is null) return error;
            var finished = flags.Results[0];
            if (!finished.Success)
                return $"[Error] TcUnit results not readable: {FinishedSymbol} on {flags.AmsNetId}:{port} — {finished.Message}. " +
                       "Either no PLC application runs there, or it does not run TcUnit (the TcUnit library referenced and " +
                       "TcUnit.RUN() called from a program a task runs). twincat_build_plc(expectSymbols=['GVL_TcUnit.TcUnitRunner']) " +
                       "shows whether the runner is compiled in.";
            var pending = flags.Results.Where(r => !(r.Success && r.Value is true)).Select(r => r.Symbol).ToList();
            if (pending.Count == 0) return null;
            if (cancellationToken.IsCancellationRequested) return "[Error] Cancelled while waiting for TcUnit to finish.";
            if (DateTime.UtcNow >= deadline)
                return $"[Error] TcUnit did not finish within {timeout.TotalSeconds:F0}s on {flags.AmsNetId}:{port}: " +
                       $"{string.Join(", ", pending)} not TRUE. A test that never reaches TEST_FINISHED() keeps the run open; " +
                       "a stopped PLC (twincat_plc_start) never finishes. Pass a longer timeoutSeconds for slow tests.";
            if (PollInterval > TimeSpan.Zero) Thread.Sleep(PollInterval);
        }
    }

    private AdsBatchReadResult? Read(List<AdsTypedRead> reads, string net, int port, out string? error)
    {
        var r = session.ReadAdsValuesTyped(reads, net, port);
        error = r.Success ? null : AdsVariableCoordinator.FailedAt(r.Message, r.AmsNetId, r.AdsPort);
        return r.Success ? r : null;
    }

    private static string Summary(string verdict, int? suiteCount, int? tests, int? failed, List<Suite> suites,
        long? onlineChanges, int unreadable)
    {
        string text = verdict switch
        {
            "unknown" => "[Error] TcUnit finished, but its totals could not be read.",
            "no_tests" => "[Warn] TcUnit finished without a test: no FB extending TcUnit.FB_TestSuite is instantiated in a program a task runs.",
            _ => Inv($"TcUnit: {tests} test{(tests == 1 ? "" : "s")} in {suiteCount} suite{(suiteCount == 1 ? "" : "s")}, {failed} failed"),
        };
        if (verdict == "failed")
        {
            var firstFailures = suites.SelectMany(s => s.Cases.Where(c => c.Failed == true).Select(c => $"{s.Name}.{c.Name}: {c.Message}")).Take(3).ToList();
            text += firstFailures.Count > 0 ? $" — {string.Join("; ", firstFailures)}." : ".";
        }
        else if (verdict == "passed") text += ".";

        if (onlineChanges > 0)
            text += Inv($" [Warn] The application took {onlineChanges} online change{(onlineChanges == 1 ? "" : "s")} since its download, ") +
                    "but TcUnit runs its tests once at start: code changed online since is not covered. Deploy with " +
                    "twincat_login(mode='download') or an activation to run the tests on the current code.";
        if (unreadable > 0)
            text += Inv($" [Warn] {unreadable} result symbol{(unreadable == 1 ? "" : "s")} could not be read (shown as null).");
        return text;
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static int? Int(AdsBatchReadItem item)
    {
        if (!item.Success || item.Value is null) return null;
        try { return Convert.ToInt32(item.Value, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    private static double? Real(AdsBatchReadItem item)
    {
        if (!item.Success || item.Value is null) return null;
        try { return Convert.ToDouble(item.Value, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    private static bool? Flag(AdsBatchReadItem item) => item.Success && item.Value is bool b ? b : null;

    private static string? Text(AdsBatchReadItem item) => item.Success ? item.Value as string : null;

    private sealed record Suite(int Index, string? Name, int? Tests, int? Failed, double? Duration)
    {
        public List<Case> Cases { get; } = [];
    }

    private sealed record Case(string? Name, bool? Failed, bool? Skipped, string? Message);
}
