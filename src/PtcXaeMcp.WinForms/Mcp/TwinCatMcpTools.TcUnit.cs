using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_tcunit_results", ReadOnly = true)]
    [Description("Read TcUnit test results from a running PLC over ADS. Waits until the TcUnit runner " +
                 "(GVL_TcUnit.TcUnitRunner) has finished and stored its results, then reads totals, every suite and the cases of " +
                 "failed suites. Does not deploy: build first (twincat_build_plc expectSymbols=['GVL_TcUnit.TcUnitRunner'] proves the " +
                 "runner is compiled in), start the tests with twincat_activate_config(autoConfirmRestart=true) or " +
                 "twincat_login(mode='download'), then call this. Tests do not rerun after an online change; the result then warns " +
                 "with OnlineChangeCnt. Returns JSON with success, verdict (passed | failed | no_tests | unknown), message, totals, " +
                 "suites with their cases, and junitFile when junitPath is given; [Error] when TcUnit is missing or does not finish " +
                 "within timeoutSeconds.")]
    public string TcUnitResults(
        [Description("AMS NetId of the PLC. Default: the open project's target (twincat_set_target).")] string? amsNetId = null,
        [Description("ADS port of the PLC application. Default 851.")] int? adsPort = null,
        [Description("Seconds to wait for TcUnit to finish. Default 60, max 600.")] int? timeoutSeconds = null,
        [Description("Also list passed cases and passed suites' cases (JSON and JUnit file). Default false: only failed and skipped.")] bool includePassed = false,
        [Description("Optional JUnit XML file to write the results to, e.g. for CI.")] string? junitPath = null,
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
