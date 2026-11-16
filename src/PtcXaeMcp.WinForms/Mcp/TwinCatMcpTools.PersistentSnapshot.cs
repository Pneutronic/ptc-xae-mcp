using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.TwinCat;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

// persistent-snapshot: capture, compare and restore the remanent (PERSISTENT / RETAIN) data of a
// running PLC over ADS.
//
// ASYMMETRIC SURFACE — the AI may browse, save, compare and PROPOSE a restore; the restore itself
// is executed only by the host, after a human confirms the popup in the Approvals view
// (SnapshotRestoreCoordinator.ConfirmProposal, host-only, no MCP binding). There is deliberately NO
// confirm/execute tool; guarded by PersistentSnapshotMcpTests.
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_list_ads_symbols", ReadOnly = true)]
    [Description("List the online symbol table of a running PLC or UMR via ADS (no values). " +
                 "For values use twincat_read_ads_variables_batch. Read-only. Returns JSON { success, total, offset, " +
                 "returned, has_more, next_offset, symbols: [{ path, type, size, isPersistent, readOnly, comment }], " +
                 "amsNetId, adsPort } or [Error]. Call again with offset=next_offset while has_more. total is null " +
                 "when the walk stopped at 100000 symbols (narrow root or maxDepth). isPersistent marks PERSISTENT " +
                 "only; RETAIN is NOT flagged over ADS.")]
    public string ListAdsSymbols(
        [Description("Symbol whose children to list, e.g. 'GVL_Recipe'. Omit for the top level.")] string? root = null,
        [Description("Levels below root to list. Default 1.")] int? maxDepth = null,
        [Description("Only symbols flagged persistent. Default false.")] bool persistentOnly = false,
        [Description("Symbols per page: default 500, at most 5000 (0 = 5000).")] int? limit = null,
        [Description("Symbols to skip (next_offset of the previous page). Default 0.")] int offset = 0,
        [Description("AMS NetId of the target. Omit for the local runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_ads_symbols", new { root, maxDepth, persistentOnly, limit, offset, amsNetId, adsPort }, () =>
            Mcp.McpPaging.Validate(limit, offset) ?? bridge.Dispatcher.Run(() =>
                new PersistentSnapshotCoordinator(bridge.Dispatcher.Session, bridge.SnapshotRestore)
                    .ListSymbols(root, maxDepth, persistentOnly, limit, amsNetId, adsPort, offset)), cancellationToken);

    [McpServerTool(Name = "twincat_persistent_snapshot_save", ReadOnly = true)]
    [Description("Save a running PLC's PERSISTENT/RETAIN data to a snapshot file. " +
                 "Read-only on the PLC; writes only the local JSON file (leaf path -> { type, value }). Leaves come " +
                 "from the open solution's compiled .tmc (selectionSource 'tmc'; [Error] if a TMC leaf is missing on " +
                 "the PLC or has another type), else from the runtime's persistent flags ('ads', no RETAIN). " +
                 "Leaves that cannot be captured (e.g. input-mapped, POINTER, PROPERTY) are listed in skipped with " +
                 "the reason. Returns JSON { success, path, leafCount, selectionSource, skipped[], skippedCount, " +
                 "warnings[], amsNetId, adsPort }, not the values.")]
    public string PersistentSnapshotSave(
        [Description("'persistent', 'retain' or 'both' (default).")] string? kind = null,
        [Description("Extra symbol paths whose whole subtree is added, e.g. ['GVL_Settings.nRetainCounter'].")] string[]? roots = null,
        [Description("'auto' (default: TMC if found, else ADS), 'tmc', 'ads' or 'roots' (only roots[]).")] string? selection = null,
        [Description("Absolute path of the PLC .tmc. Omit to find it in the open solution.")] string? tmcPath = null,
        [Description("Target .json file. Default %LOCALAPPDATA%\\ptc-xae-mcp\\snapshots\\<utc>_<netId>.json. " +
                     "Must be inside the solution directory, TEMP, %LOCALAPPDATA%\\ptc-xae-mcp or PTC_XAE_MCP_ALLOWED_PATHS.")] string? path = null,
        [Description("AMS NetId of the target. Omit for the local runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_persistent_snapshot_save", new { kind, roots, selection, tmcPath, path, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new PersistentSnapshotCoordinator(bridge.Dispatcher.Session, bridge.SnapshotRestore)
                    .Save(kind, roots, selection, tmcPath, path, amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_persistent_snapshot_compare", ReadOnly = true)]
    [Description("Compare a snapshot file leaf by leaf with the running PLC or another snapshot. " +
                 "Read-only; with otherPath no PLC is needed. Returns JSON { success, equal, against, equalCount, " +
                 "differenceCount, onlyInSnapshotCount, onlyOnPlcCount, typeMismatchCount, total, offset, returned, " +
                 "has_more, next_offset, differences[{ path, snapshotPath, type, snapshot, live }], onlyInSnapshot[], " +
                 "onlyOnPlc[], typeMismatches[], conflicts[], warnings[] }. limit/offset page the four leaf lists " +
                 "together; call again with offset=next_offset while has_more; *Count gives each list's size. " +
                 "equal = every snapshot leaf matches; onlyOnPlc (remanent leaves not in the snapshot) does not affect " +
                 "it. Integer widths, REAL/LREAL and STRING/WSTRING are compatible; other type changes are mismatches.")]
    public string PersistentSnapshotCompare(
        [Description("Snapshot file written by twincat_persistent_snapshot_save.")] string snapshotPath,
        [Description("Second snapshot file to compare against instead of the PLC.")] string? otherPath = null,
        [Description("JSON rename rules for the snapshot paths, e.g. " +
                     "{\"GVL_Old.stRecipe\":\"GVL_New.stRecipe\"} (exact path or prefix at a '.'/'[' boundary).")] string? pathMap = null,
        [Description("Absolute path of the PLC .tmc for the live remanent set. Omit to find it.")] string? tmcPath = null,
        [Description("AMS NetId of the target. Omit for the local runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        [Description("Items per list and page: default 200, at most 5000 (0 = 5000).")] int? limit = null,
        [Description("Items to skip in every list (next_offset of the previous page). Default 0.")] int offset = 0,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_persistent_snapshot_compare", new { snapshotPath, otherPath, pathMap, tmcPath, amsNetId, adsPort, limit, offset }, () =>
            Mcp.McpPaging.Validate(limit, offset) ?? bridge.Dispatcher.Run(() =>
                new PersistentSnapshotCoordinator(bridge.Dispatcher.Session, bridge.SnapshotRestore)
                    .Compare(snapshotPath, otherPath, pathMap, tmcPath, amsNetId, adsPort, limit, offset)), cancellationToken);

    [McpServerTool(Name = "twincat_persistent_snapshot_restore", ReadOnly = false, Destructive = true)]
    [Description("Restore a snapshot into the running PLC; DESTRUCTIVE and human-confirmed. " +
                 "A dry run only returns the plan { planHash, plcState, writes[{ path, type, current, snapshot }], " +
                 "skips[{ path, reason }], unchanged } and writes nothing. dryRun=false needs the dry run's planHash, " +
                 "refuses if the plan changed, raises a confirmation popup in the host window (target, PLC RUN/STOP, " +
                 "differences) and returns { restoreId, status: \"pending\" }. NOTHING is written until a human " +
                 "confirms; no tool can confirm. After confirmation the plan is rechecked (rejected if the PLC " +
                 "changed), only differing leaves are written and every written leaf is read back; poll " +
                 "twincat_persistent_snapshot_restore_status. Type changes, out-of-bounds indices, read-only and " +
                 "input-mapped leaves are skipped. A running program may overwrite a restored value; the read-back shows it.")]
    public string PersistentSnapshotRestore(
        [Description("Snapshot file written by twincat_persistent_snapshot_save.")] string snapshotPath,
        [Description("true (default): return the plan only. false: propose the restore for human confirmation.")] bool dryRun = true,
        [Description("planHash from the dry run. Required when dryRun=false.")] string? planHash = null,
        [Description("JSON rename rules old -> new (see twincat_persistent_snapshot_compare).")] string? pathMap = null,
        [Description("Restore only these leaf paths. Omit for all.")] string[]? include = null,
        [Description("AMS NetId of the target. Omit for the local runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_persistent_snapshot_restore", new { snapshotPath, dryRun, planHash, pathMap, include, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new PersistentSnapshotCoordinator(bridge.Dispatcher.Session, bridge.SnapshotRestore)
                    .Restore(snapshotPath, dryRun, planHash, pathMap, include, amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_persistent_snapshot_restore_status", ReadOnly = true)]
    [Description("Poll the status of a persistent-snapshot restore proposal. Read-only. Returns JSON " +
                 "{ restoreId, status, reason, target, plcState, snapshot, createdUtc, expiresUtc, writeCount, report }; " +
                 "status is pending | confirmed | executed | rejected | expired. Once executed, report has " +
                 "{ written, failed, skipped, items[{ path, outcome, readBack, message }] } with read-back values.")]
    public string PersistentSnapshotRestoreStatus(
        [Description("restoreId from twincat_persistent_snapshot_restore.")] string restoreId,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_persistent_snapshot_restore_status", new { restoreId }, () =>
            PersistentSnapshotCoordinator.RestoreStatus(bridge.SnapshotRestore, restoreId), cancellationToken, bypassGate: true);
}
