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
    [Description("Read-only browse of the ONLINE symbol table of a running PLC / UMR via ADS (no values -- " +
                 "use twincat_read_ads_variables_batch for those). Returns JSON { success, count, truncated, " +
                 "symbols: [{ path, type, size, isPersistent, readOnly, comment }], amsNetId, adsPort } or " +
                 "[Error]. isPersistent is the runtime's IsPersistent flag (VAR_GLOBAL PERSISTENT); RETAIN " +
                 "variables are NOT flagged over ADS. Default amsNetId = the local AMS router's NetId, port 851.")]
    public string ListAdsSymbols(
        [Description("Symbol to list the children of, e.g. 'GVL_Recipe'. Omit for the top level.")] string? root = null,
        [Description("How many levels below root to list. Default 1.")] int? maxDepth = null,
        [Description("Only symbols the runtime flags as persistent. Default false.")] bool persistentOnly = false,
        [Description("Maximum entries returned (default 500, max 5000).")] int? limit = null,
        [Description("Optional AMS Net ID. Omit for the local TwinCAT runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_ads_symbols", new { root, maxDepth, persistentOnly, limit, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new PersistentSnapshotCoordinator(bridge.Dispatcher.Session, bridge.SnapshotRestore)
                    .ListSymbols(root, maxDepth, persistentOnly, limit, amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_persistent_snapshot_save", ReadOnly = true)]
    [Description("Capture the remanent data of a running PLC into a snapshot file (format " +
                 "ptc-persistent-snapshot/1: a flat map leaf path -> { type, value }, arrays by declared index). " +
                 "READ-ONLY on the PLC (ADS sum reads); it only writes the local file. The remanent leaves come " +
                 "from the compiled .tmc of the open solution (PERSISTENT and RETAIN, as the compiler resolved " +
                 "them -- selectionSource 'tmc'; every TMC leaf must exist on the PLC with the same type, else " +
                 "[Error] TMC does not match the running PLC), else from the runtime's IsPersistent flags " +
                 "(selectionSource 'ads' -- RETAIN is not detectable that way). roots[] adds explicit subtrees. " +
                 "Leaves that cannot be captured (input-mapped, read-only, POINTER/REFERENCE, PROPERTY, " +
                 "unsupported type, unreadable) are listed in skipped with the reason, never dropped silently. " +
                 "Returns JSON { success, path, leafCount, selectionSource, skipped[], skippedCount, warnings[], " +
                 "amsNetId, adsPort } -- never the value map itself. Default amsNetId = the local AMS router's " +
                 "NetId, port 851.")]
    public string PersistentSnapshotSave(
        [Description("'persistent', 'retain' or 'both' (default). Which remanent leaves to select.")] string? kind = null,
        [Description("Extra symbol paths whose whole subtree is added, e.g. ['GVL_Settings.nRetainCounter'].")] string[]? roots = null,
        [Description("'auto' (default: TMC if found, else ADS), 'tmc', 'ads' or 'roots' (only roots[]).")] string? selection = null,
        [Description("Absolute path of the PLC .tmc. Omit to find it in the open solution.")] string? tmcPath = null,
        [Description("Target .json file. Omit for %LOCALAPPDATA%\\ptc-xae-mcp\\snapshots\\<utc>_<netId>.json. " +
                     "Must be inside the solution directory, TEMP, %LOCALAPPDATA%\\ptc-xae-mcp or PTC_XAE_MCP_ALLOWED_PATHS.")] string? path = null,
        [Description("Optional AMS Net ID. Omit for the local TwinCAT runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_persistent_snapshot_save", new { kind, roots, selection, tmcPath, path, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new PersistentSnapshotCoordinator(bridge.Dispatcher.Session, bridge.SnapshotRestore)
                    .Save(kind, roots, selection, tmcPath, path, amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_persistent_snapshot_compare", ReadOnly = true)]
    [Description("Compare a snapshot file leaf by leaf with the running PLC (or with another snapshot via " +
                 "otherPath -- then no PLC is needed). READ-ONLY. Returns JSON { success, equal, against, " +
                 "equalCount, differenceCount, differences[{ path, snapshotPath, type, snapshot, live }], " +
                 "onlyInSnapshot[], onlyOnPlc[], typeMismatches[], conflicts[], warnings[] } (lists capped by limit). " +
                 "onlyInSnapshot = orphans with no counterpart on the PLC after pathMap; onlyOnPlc = remanent " +
                 "leaves the snapshot does not have. Types are decided by the PLC as it is now (integer widths, " +
                 "REAL/LREAL, STRING/WSTRING are compatible; anything else is a type mismatch).")]
    public string PersistentSnapshotCompare(
        [Description("Snapshot file written by twincat_persistent_snapshot_save.")] string snapshotPath,
        [Description("Compare against this second snapshot file instead of the PLC.")] string? otherPath = null,
        [Description("JSON object of rename rules applied to the snapshot paths, e.g. " +
                     "{\"GVL_Old.stRecipe\":\"GVL_New.stRecipe\"} (exact path or prefix at a '.'/'[' boundary).")] string? pathMap = null,
        [Description("Absolute path of the PLC .tmc used to re-select the live remanent set. Omit to find it.")] string? tmcPath = null,
        [Description("Optional AMS Net ID. Omit for the local TwinCAT runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        [Description("Cap per result list (default 200).")] int? limit = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_persistent_snapshot_compare", new { snapshotPath, otherPath, pathMap, tmcPath, amsNetId, adsPort, limit }, () =>
            bridge.Dispatcher.Run(() =>
                new PersistentSnapshotCoordinator(bridge.Dispatcher.Session, bridge.SnapshotRestore)
                    .Compare(snapshotPath, otherPath, pathMap, tmcPath, amsNetId, adsPort, limit)), cancellationToken);

    [McpServerTool(Name = "twincat_persistent_snapshot_restore", ReadOnly = false, Destructive = true)]
    [Description("Restore a snapshot into the running PLC -- DESTRUCTIVE and human-gated. dryRun defaults to " +
                 "TRUE: it only returns the restore plan { planHash, plcState, writes[{ path, type, current, " +
                 "snapshot }], skips[{ path, reason }], unchanged } and writes nothing. With dryRun=false it " +
                 "requires the planHash of a dry run, re-plans, refuses on a different hash, then raises a " +
                 "restore proposal (modal popup in the host window showing target, PLC state RUN/STOP, the " +
                 "differences) and returns { restoreId, status: \"pending\" }. NOTHING is written until a human " +
                 "confirms; there is no MCP tool that confirms. After the confirm the host re-plans against the " +
                 "PLC (rejecting if it changed), writes with ADS sum commands and reads EVERY written leaf back. " +
                 "Poll twincat_persistent_snapshot_restore_status for the outcome and the per-leaf report. " +
                 "Only leaves whose live value differs are written; type changes, out-of-bounds indices, " +
                 "read-only and input-mapped leaves are skipped with a reason. A running program may overwrite " +
                 "a restored value; the read-back reports it.")]
    public string PersistentSnapshotRestore(
        [Description("Snapshot file written by twincat_persistent_snapshot_save.")] string snapshotPath,
        [Description("true (default): return the plan only. false: propose the restore for human confirmation.")] bool dryRun = true,
        [Description("The planHash returned by the dry run. Required when dryRun=false.")] string? planHash = null,
        [Description("JSON object of rename rules old -> new (see twincat_persistent_snapshot_compare).")] string? pathMap = null,
        [Description("Restrict the restore to these leaf paths (e.g. picked differences). Omit for all.")] string[]? include = null,
        [Description("Optional AMS Net ID. Omit for the local TwinCAT runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_persistent_snapshot_restore", new { snapshotPath, dryRun, planHash, pathMap, include, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new PersistentSnapshotCoordinator(bridge.Dispatcher.Session, bridge.SnapshotRestore)
                    .Restore(snapshotPath, dryRun, planHash, pathMap, include, amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_persistent_snapshot_restore_status", ReadOnly = true)]
    [Description("Read-only poll of a restore proposal: returns JSON { restoreId, status, reason, target, " +
                 "plcState, snapshot, createdUtc, expiresUtc, writeCount, report }. Status is one of " +
                 "pending | confirmed | executed | rejected | expired. Once executed, report carries " +
                 "{ written, failed, skipped, items[{ path, outcome, readBack, message }] } -- the read-back " +
                 "value, not the intended one. Reads only; writes nothing.")]
    public string PersistentSnapshotRestoreStatus(
        [Description("The restoreId returned by twincat_persistent_snapshot_restore.")] string restoreId,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_persistent_snapshot_restore_status", new { restoreId }, () =>
            PersistentSnapshotCoordinator.RestoreStatus(bridge.SnapshotRestore, restoreId), cancellationToken, bypassGate: true);
}
