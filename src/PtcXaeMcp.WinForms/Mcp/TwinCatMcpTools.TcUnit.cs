using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_tcunit_results", ReadOnly = true)]
    [Description("Read the results of the TcUnit tests in a RUNNING PLC over ADS (read-only). TcUnit, a PLC library the project " +
                 "references, runs its test suites once after the application starts and keeps the outcome in " +
                 "GVL_TcUnit.TcUnitRunner. This waits until the runner has finished and stored the results (AllTestSuitesFinished, " +
                 "then StoredGeneralTestResults and StoredTestSuiteResults), then reads the totals, every suite and the cases of the " +
                 "failed suites. It does not deploy: build first (twincat_build_plc with expectSymbols=['GVL_TcUnit.TcUnitRunner'] " +
                 "proves the runner is compiled in), then start the tests with twincat_activate_config(autoConfirmRestart=true) or " +
                 "twincat_login(mode='download') and call this. After an online change the tests do not run again — the result " +
                 "then warns with the PLC's OnlineChangeCnt. Returns JSON { success, verdict: passed | failed | no_tests | unknown, " +
                 "message, totals { suites, tests, passed, failed, duration }, onlineChangeCnt, suites [{ name, tests, failed, duration, " +
                 "cases [{ name, failed, skipped, message }] }], unreadableSymbols, amsNetId, adsPort }, or an [Error] when TcUnit is " +
                 "not there or does not finish within timeoutSeconds. junitPath also writes the result as a JUnit XML file for a CI " +
                 "server (the JSON then names it in junitFile); with includePassed=true the file lists every case.")]
    public string TcUnitResults(
        [Description("AMS NetId of the PLC. Default: the open project's target (what twincat_set_target set).")] string? amsNetId = null,
        [Description("ADS port of the PLC application. Default 851.")] int? adsPort = null,
        [Description("Seconds to wait for TcUnit to finish. Default 60, at most 600.")] int? timeoutSeconds = null,
        [Description("Also list the passed cases, and the cases of passed suites. Default false: only failed and skipped cases.")] bool includePassed = false,
        [Description("Optional path of a JUnit XML file to write the results to, e.g. for a CI server.")] string? junitPath = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_tcunit_results", new { amsNetId, adsPort, timeoutSeconds, includePassed, junitPath }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var pathError = McpPathGuard.CheckOptionalFile(junitPath, bridge.Dispatcher.Session, "junitPath");
                if (pathError is not null) return pathError;
                string result = new TcUnitResultsCoordinator(bridge.Dispatcher.Session)
                    .Collect(amsNetId, adsPort, timeoutSeconds, includePassed, cancellationToken);
                return junitPath is null || McpErrors.IsError(result) ? result : TcUnitJUnit.WithJUnitFile(result, junitPath);
            }), cancellationToken);
}
