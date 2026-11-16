using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.TwinCat;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

// signal testbench: wait for, check and briefly record live PLC values from ADS notifications. All four are
// read-only: they never write to the PLC (whoever stimulates calls twincat_write_ads_variable first and so passes the
// access policy). The long calls hold the dispatcher lock only while the notifications are registered.
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_ads_wait_until", ReadOnly = true)]
    [Description("Wait until live PLC values meet conditions (ADS notifications). Short pulses are not missed. " +
                 "Read-only. Conditions are texts: '<symbol> <op> <value>' with op == != < <= > >=, or '<symbol> changed' " +
                 "(changed = differs from the value when the wait began, stays true once it happened), e.g. 'MAIN.nStep == 20', " +
                 "'GVL.bDone == true', 'MAIN.fbCyl.State == E_STATE_RUN' (an ENUM by member name or number). Up to 8 conditions " +
                 "on up to 8 symbols (BOOL, numbers, time types in their raw unit, STRING, ENUM). A condition that already holds " +
                 "ends the wait at once (elapsedMs 0). Returns JSON { success, met, timedOut, elapsedMs, timeoutMs, combine, " +
                 "conditions: [{ condition, met }], values: { symbol: value }, amsNetId, adsPort }; a timeout is met=false " +
                 "with the last values, not an error. [Error] when a symbol is missing, is not a scalar, or the runtime stops " +
                 "while waiting. Changes are seen at the resolution of the PLC task cycle.")]
    public string AdsWaitUntil(
        [Description("1 to 8 conditions, e.g. ['MAIN.nCount > 5', 'MAIN.bDone == true'].")] string[] conditions,
        [Description("'all' (default: every condition holds) or 'any' (one holds).")] string? combine = null,
        [Description("How long to wait in ms: default 10000, at most 300000 (a fixed limit).")] int? timeoutMs = null,
        [Description("AMS NetId of the target. Omit for the local runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ads_wait_until", new { conditions, combine, timeoutMs, amsNetId, adsPort }, () =>
            new SignalTestbenchCoordinator(bridge.Dispatcher.Session, run => bridge.Dispatcher.Run(run))
                .WaitUntil(conditions, combine, timeoutMs, amsNetId, adsPort, cancellationToken), cancellationToken);

    [McpServerTool(Name = "twincat_ads_expect", ReadOnly = true)]
    [Description("Check live PLC values over a time window (ADS notifications). Read-only. " +
                 "mode 'stable': none of the symbols changes for durationMs. mode 'within': the conditions hold at some point " +
                 "inside durationMs (already true counts). mode 'never': the conditions do not hold at any time of the window, " +
                 "the opening state included. Conditions as in twincat_ads_wait_until ('<symbol> <op> <value>' or '<symbol> changed'). " +
                 "A violation ends the check at once. Returns JSON { success, mode, passed, elapsedMs, durationMs, firstViolation: " +
                 "{ symbol, atMs, reason, from, to } | null, combine, conditions, values, amsNetId, adsPort }; a failed expectation " +
                 "is passed=false, not an error. [Error] when a symbol is missing or not a scalar, or the runtime stops during " +
                 "the window (a stopped PLC would otherwise look stable). Up to 8 symbols, window at most 300000 ms.")]
    public string AdsExpect(
        [Description("'stable', 'within' or 'never'.")] string mode,
        [Description("Length of the observation window in ms, at most 300000 (a fixed limit).")] int durationMs,
        [Description("Conditions for 'within' and 'never', e.g. ['MAIN.nStep == 20'].")] string[]? conditions = null,
        [Description("Symbol paths for 'stable', e.g. ['GVL.bReady', 'MAIN.nMode'].")] string[]? symbols = null,
        [Description("'all' (default) or 'any', for 'within' and 'never'.")] string? combine = null,
        [Description("AMS NetId of the target. Omit for the local runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ads_expect", new { mode, durationMs, conditions, symbols, combine, amsNetId, adsPort }, () =>
            new SignalTestbenchCoordinator(bridge.Dispatcher.Session, run => bridge.Dispatcher.Run(run))
                .Expect(mode, durationMs, conditions, symbols, combine, amsNetId, adsPort, cancellationToken), cancellationToken);

    [McpServerTool(Name = "twincat_ads_record", ReadOnly = true)]
    [Description("Record live PLC values briefly (ADS notifications) and report statistics. Read-only on the PLC; " +
                 "writes only a local CSV of the changes. 1 to 32 scalar symbols (BOOL, numbers, time types, STRING, ENUM), " +
                 "durationMs at most 120000, cycleMs 1 to 1000 (the notification cycle). Times are the runtime's own time stamps, " +
                 "so pulse widths and periods are exact to a task cycle. Returns JSON { success, endedBy: completed | changeLimit | " +
                 "fault, durationMs, windowMs, timeBase, changes, csv: { path, rows, bytes }, symbols: [{ symbol, plcType, changes, " +
                 "first, last, min, max, mean (time-weighted), rising, falling, highMs / lowMs / periodMs: { count, min, max, avg } " +
                 "(BOOL: complete pulses only), frequencyHz, changeIntervalMs }] }. At most 200000 changes are kept; the run then " +
                 "ends with endedBy changeLimit. The CSV (t_ms;symbol;value;name, one row per change, opening values at 0) goes to " +
                 "%TEMP%\\ptc-xae-mcp\\rec unless csvPath is given; recordings there older than 7 days are removed. A pulse " +
                 "shorter than one PLC task cycle is not visible.")]
    public string AdsRecord(
        [Description("1 to 32 symbol paths, e.g. ['MAIN.bRun', 'GVL.nCycleCount'].")] string[] symbols,
        [Description("Recording time in ms: default 5000, at most 120000 (a fixed limit).")] int? durationMs = null,
        [Description("Notification cycle in ms, 1 to 1000. Default 1 (every task cycle); a slower cycle only loses changes.")] int? cycleMs = null,
        [Description("Target .csv file. Default %TEMP%\\ptc-xae-mcp\\rec\\<utc>_<netId>.csv. Must be inside the solution " +
                     "directory, TEMP, %LOCALAPPDATA%\\ptc-xae-mcp or PTC_XAE_MCP_ALLOWED_PATHS.")] string? csvPath = null,
        [Description("Write the CSV (default true). false: statistics only.")] bool writeCsv = true,
        [Description("AMS NetId of the target. Omit for the local runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ads_record", new { symbols, durationMs, cycleMs, csvPath, writeCsv, amsNetId, adsPort }, () =>
            new SignalTestbenchCoordinator(bridge.Dispatcher.Session, run => bridge.Dispatcher.Run(run))
                .Record(symbols, durationMs, cycleMs, csvPath, writeCsv, amsNetId, adsPort, cancellationToken), cancellationToken);

    [McpServerTool(Name = "twincat_ads_snapshot_compare", ReadOnly = true)]
    [Description("Compare live values of any symbols with a baseline, or capture them. Read-only on " +
                 "the PLC; savePath writes only a local JSON file. A STRUCT or ARRAY symbol is read leaf by leaf; input-mapped and " +
                 "read-only variables are read too. The baseline is baselinePath (a file written by savePath of this tool or by " +
                 "twincat_persistent_snapshot_save) or baseline (JSON object { \"<leaf symbol>\": <value> }, typed like the PLC symbol). " +
                 "Without symbols, the baseline's leaves are read. savePath alone captures the current values. Returns JSON " +
                 "{ success, equal, equalCount, differenceCount, onlyInBaselineCount, notInBaselineCount, typeMismatchCount, " +
                 "missingOnPlcCount, differences: [{ symbol, type, baseline, live }], onlyInBaseline, notInBaseline, typeMismatches, " +
                 "missingOnPlc, skipped, saved: { path, leafCount } | null }; lists show at most 200 entries. Up to 256 symbols " +
                 "and 5000 leaves per call. For the remanent (PERSISTENT / RETAIN) data and its restore use the " +
                 "twincat_persistent_snapshot_* tools.")]
    public string AdsSnapshotCompare(
        [Description("Symbol paths to read (up to 256), e.g. ['GVL_Settings', 'MAIN.nMode']. Omit to read the baseline's leaves.")] string[]? symbols = null,
        [Description("Snapshot file to compare with.")] string? baselinePath = null,
        [Description("Inline baseline as JSON, e.g. {\"MAIN.nMode\": 2, \"GVL.bReady\": true}. Not together with baselinePath.")] string? baseline = null,
        [Description("Write the current values to this .json file (inside the solution directory, TEMP, " +
                     "%LOCALAPPDATA%\\ptc-xae-mcp or PTC_XAE_MCP_ALLOWED_PATHS).")] string? savePath = null,
        [Description("Numbers differing by at most this much count as equal. Default 0.")] double? tolerance = null,
        [Description("AMS NetId of the target. Omit for the local runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ads_snapshot_compare", new { symbols, baselinePath, baseline, savePath, tolerance, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new SignalTestbenchCoordinator(bridge.Dispatcher.Session)
                    .SnapshotCompare(symbols, baselinePath, baseline, savePath, tolerance, amsNetId, adsPort)), cancellationToken);
}
