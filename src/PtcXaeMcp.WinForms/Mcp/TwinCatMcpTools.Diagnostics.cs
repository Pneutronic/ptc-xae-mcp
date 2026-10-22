using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace PtcXaeMcp.WinForms;

/// <summary>
/// Diagnostics/research/test-support MCP tools (issue #157 item 1): never registered against a
/// production MCP client. Only started when the host is launched with the <c>--diagnostics</c>
/// command-line flag or the <c>PTC_XAE_MCP_DIAGNOSTICS=1</c> environment variable is set — see
/// <see cref="McpBridge"/>'s server setup and <c>Program.cs</c>. Covers: the live-integration-suite
/// test marker (<c>ptc_test_marker</c>), demo/showcase editor navigation (<c>twincat_show_step</c>),
/// research spikes against the TwinCAT Safety project (<c>twincat_safety_recon</c>,
/// <c>twincat_safety_probe_create_alias</c>), an NC-tree research dump (<c>twincat_dump_nc_tree</c>),
/// the deprecated PLC-object-open/close aliases kept only for back-compat
/// (<c>twincat_open_plc_object</c>/<c>twincat_close_plc_object</c>), and the IBN Live arm
/// tool the live suite needs (<c>twincat_ibn_arm</c>; production arming happens only in the
/// host window, issue #272).
/// <para>
/// These tools are fully functional (not stubs) — they are simply not part of the public,
/// documented tool surface a released MCP client should depend on.
/// </para>
/// </summary>
[McpServerToolType]
internal sealed class TwinCatMcpToolsDiagnostics(McpBridge bridge)
{
    // ---- ptc_test_marker -----------------------------------------------------

    /// <summary>
    /// TEST-ONLY narration marker for the live integration suite. The suite
    /// fires this (via an assembly-wide <c>BeforeAfterTest</c> attribute) at the
    /// start and end of every test so the host's MCP Activity tab can show
    /// "▶ Running (i/N): test — description" live.
    /// <para>
    /// Deliberately NOT a <c>twincat_*</c> tool: it performs no TwinCAT/XAE
    /// automation, is NOT counted as an XAE operation (it bypasses the activity
    /// log entirely), and bypasses the step/pause gate so single-step mode never
    /// stalls on a marker. Real MCP clients should ignore it. Best-effort: a
    /// marker must never affect a test run, so every failure is swallowed into an
    /// <c>[Error]</c> string.
    /// </para>
    /// </summary>
    [McpServerTool(Name = "ptc_test_marker", ReadOnly = false, Destructive = false)]
    [Description(
        "TEST-ONLY narration marker for the live integration suite. Signals which " +
        "test is currently running so the host UI can show it live. NOT a TwinCAT/XAE " +
        "operation: it performs no automation, is not counted as a tool call, and is " +
        "not subject to the step/pause gate. Real MCP clients should ignore this tool. " +
        "phase='start' marks a test as current (and increments the running index); " +
        "phase='end' closes it; phase='current' returns the current test as JSON. " +
        "Only registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.")]
    public string TestMarker(
        [Description("Lifecycle phase: 'start', 'end', or 'current'.")] string phase,
        [Description("The test method name (e.g. Read_BOOL_GvlSafetyDriveReleased). Ignored for phase='current'.")] string testName = "",
        [Description("Human-readable narration of the test, from its [Trait(\"Description\")]. Optional.")] string? description = null,
        CancellationToken cancellationToken = default)
    {
        // Best-effort by contract: never throw into the suite. This path never
        // calls bridge.Activity.Trace, so a marker is not logged, not counted as
        // an XAE operation, and not gated.
        try
        {
            var current = bridge.Activity.CurrentTest;

            switch ((phase ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "start":
                    current.Start(testName ?? string.Empty, description, bridge.Activity.Log.Count);
                    var started = current.Current;
                    return $"[marker] start #{started?.Index} {started?.Name}" +
                           (string.IsNullOrWhiteSpace(description) ? "" : $" — {description}");

                case "end":
                    current.End(testName ?? string.Empty);
                    return $"[marker] end {testName}";

                case "current":
                    var c = current.Current;
                    return JsonSerializer.Serialize(new
                    {
                        running = current.Running,
                        index = c?.Index ?? 0,
                        name = c?.Name,
                        description = c?.Description,
                    });

                default:
                    return $"[Error] unknown phase '{phase}' (expected 'start', 'end', or 'current').";
            }
        }
        catch (Exception ex)
        {
            return $"[Error] {ex.Message}";
        }
    }

    // ---- twincat_show_step ----------------------------------------------------

    [McpServerTool(Name = "twincat_show_step", ReadOnly = true)]
    [Description(
        "SHOWCASE composite: locate a step's CASE branch (as twincat_locate_step) and move the " +
        "visible XAE editor cursor to its label line (as twincat_goto_line, implementation pane). " +
        "Typical flow: read Step / LastSteps via twincat_read_ads_variables_batch → " +
        "twincat_show_step(treePath, step) → the audience sees the fault branch in XAE, Online values " +
        "included. Refuses to touch the editor when the step cannot be located (numeric literal " +
        "labels only — see twincat_locate_step). Returns the located line/label, the branch source, " +
        "and the cursor confirmation. Only registered when the host is started with " +
        "--diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.")]
    public string ShowStep(
        [Description("Caret-separated tree path of the POU to search, as used by twincat_read_pou.")] string treePath,
        [Description("Numeric step value to locate and show (as read live via ADS).")] int step,
        [Description("Name of the CASE selector variable. Defaults to 'Step' when omitted.")] string? stepVariable = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_show_step", new { treePath, step, stepVariable }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, loc, _) = new EditorNavigationCoordinator(bridge.Dispatcher.Session)
                    .ShowStep(treePath, step, stepVariable);
                return loc is null ? summary : summary + "\n" + TwinCatMcpTools.FormatBranch(loc);
            }), cancellationToken);

    // ---- twincat_safety_recon / twincat_safety_probe_create_alias -------------

    [McpServerTool(Name = "twincat_safety_recon", ReadOnly = true)]
    [Description("Read-only reconnaissance spike: map HOW the TwinCAT Safety project is reachable via " +
                 "the Automation Interface (Beckhoff documents it as NOT addressable — empirically false). " +
                 "Enumerates dte.Solution.Projects (the safety .splcproj is a SEPARATE VS project, not a " +
                 "child of the PLC system manager), probes each project's COM Object for tree-item-ness and " +
                 "ITcSysManager root shortcuts, and deep-walks any safety-relevant root emitting " +
                 "name/ItemType/ItemSubType/childCount/path + ProduceXml(false). No mutation, no save. " +
                 "Requires XAE running with the safety solution open. Only registered when the host is " +
                 "started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.")]
    public string SafetyRecon(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_safety_recon", null, () =>
            bridge.Dispatcher.Run(() => bridge.Dispatcher.Session.SafetyRecon()), cancellationToken);

    [McpServerTool(Name = "twincat_safety_probe_create_alias", ReadOnly = false, Destructive = true)]
    [Description("WRITE PROBE for the TwinCAT Safety project (DEFAULT read-only, NEVER saves). Resolves the " +
                 "safety root (TISC) and the itemType-800 'Safety Project' node and dumps ProduceXml(false) AND " +
                 "ProduceXml(true) — the key question being whether the alias-device list appears in the COM tree " +
                 "at all. Only when attemptWrite=true does it MUTATE in-memory: a CreateChild subType sweep + a " +
                 "ConsumeXml round-trip, each attempt's HRESULT captured and any created node rolled back. " +
                 "File.SaveAll is NEVER called — nothing is persisted. Requires XAE running with the safety " +
                 "solution open. Only registered when the host is started with --diagnostics / " +
                 "PTC_XAE_MCP_DIAGNOSTICS=1.")]
    public string SafetyProbeCreateAlias(
        [Description("Optional caret-path of the safety node to target (e.g. 'TISC^TwinSAFE_PLC'). " +
                     "Omit to auto-discover the safety root and the itemType-800 node.")]
        string? targetPath = null,
        [Description("Name stem for the probe node. Default 'zz_AItest_Probe'.")] string? aliasName = null,
        [Description("Optional CSV of candidate ItemSubType ints (e.g. '48,800,505'). Default sweeps a set.")]
        string? subTypes = null,
        [Description("When true, attempt the mutating CreateChild/ConsumeXml sweep (still never saves). Default false = read-only.")]
        bool attemptWrite = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_safety_probe_create_alias",
            new { targetPath, aliasName, subTypes, attemptWrite }, () =>
            bridge.Dispatcher.Run(() =>
                bridge.Dispatcher.Session.SafetyProbeCreateAliasDevice(targetPath, aliasName, subTypes, attemptWrite)), cancellationToken);

    // ---- twincat_scope_probe ----------------------------------------------------

    [McpServerTool(Name = "twincat_scope_probe", ReadOnly = false, Destructive = true)]
    [Description("Research probe for the TwinCAT Scope (measurement) project, used to verify the undocumented " +
                 "Scope View property keys and CreateChild element ids live (issue #153). action: 'dump' " +
                 "(default; every node under nodePath with every property key, value and value type), 'create' " +
                 "(IMeasurementScope.CreateChild(name, elementType) on nodePath; reports the HRESULT and the new " +
                 "node), 'set' (assign value to the property whose key equals `key` exactly, then read it back " +
                 "from a freshly resolved node), 'delete' (ProjectItem.Delete/Remove on nodePath). nodePath is a " +
                 "'^'-separated chain of child names from the scope root (empty = root). Never saves. Only " +
                 "registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.")]
    public string ScopeProbe(
        [Description("'dump' (default), 'create', 'set' or 'delete'.")] string action = "dump",
        [Description("'^'-separated child names from the scope root, e.g. 'Trigger' or 'Chart1^Axis1'. Empty = root.")] string? nodePath = null,
        [Description("create: name of the new child.")] string? name = null,
        [Description("create: IMeasurementScope element-type id.")] int? elementType = null,
        [Description("set: exact property key.")] string? key = null,
        [Description("set: value, converted to the property's current value type.")] string? value = null,
        [Description("Measurement project name when the solution has more than one.")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_scope_probe",
            new { action, nodePath, name, elementType, key, value, projectName }, () =>
            bridge.Dispatcher.Run(() =>
            {
                try { return bridge.Dispatcher.Session.ScopeProbe(action, nodePath, name, elementType, key, value, projectName); }
                catch (Exception ex) { return McpErrors.EnsureError(ex.Message); }
            }), cancellationToken);

    // ---- twincat_dump_nc_tree ---------------------------------------------------

    [McpServerTool(Name = "twincat_dump_nc_tree", ReadOnly = true)]
    [Description("Read-only diagnostic: dump the TwinCAT NC (TINC/Motion) tree — for every node " +
                 "its name, ItemType, ItemSubType, child count, path, and full ProduceXml(false). " +
                 "Requires XAE running with a solution open. Mirrors the PLC TIPC dump; used to " +
                 "discover NC-Task/axes/axis type discriminators and the axis parameter schema. Only " +
                 "registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.")]
    public string DumpNcTree(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_dump_nc_tree", null, () =>
            bridge.Dispatcher.Run(() => bridge.Dispatcher.Session.DumpTincTree()), cancellationToken);

    // ---- deprecated aliases: twincat_open_plc_object / twincat_close_plc_object ----
    // Kept so existing MCP clients that call twincat_open_plc_object /
    // twincat_close_plc_object do not break. They DELEGATE to the generalized
    // OpenObject / CloseObject (which for a PLC path produce byte-identical output).
    // Prefer twincat_open_object / twincat_close_object in new callers.

    [McpServerTool(Name = "twincat_open_plc_object", ReadOnly = true)]
    [Description(
        "DEPRECATED — use twincat_open_object (this is a thin alias kept for back-compat). " +
        "Opens a PLC source object (POU/GVL/DUT/INTERFACE) in the VISIBLE TwinCAT XAE editor and " +
        "focuses it. Delegates to twincat_open_object, which also handles system-manager nodes. Only " +
        "registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.")]
    public string OpenPlcObject(
        [Description("Caret-separated tree path of the PLC source object to open (POU/GVL/DUT/INTERFACE).")] string treePath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_open_plc_object", new { treePath }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, _) = new PlcCodeCoordinator(bridge.Dispatcher.Session).OpenObject(treePath);
                return summary;
            }), cancellationToken);

    [McpServerTool(Name = "twincat_close_plc_object", ReadOnly = false, Destructive = false)]
    [Description(
        "DEPRECATED — use twincat_close_object (this is a thin alias kept for back-compat). " +
        "Closes the VISIBLE TwinCAT XAE editor tab for a PLC source object (saves first). " +
        "Delegates to twincat_close_object. Only registered when the host is started with " +
        "--diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.")]
    public string ClosePlcObject(
        [Description("Caret-separated tree path of the PLC source object to close, same as passed to twincat_open_plc_object.")] string treePath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_close_plc_object", new { treePath }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcCodeCoordinator(bridge.Dispatcher.Session).CloseObject(treePath)), cancellationToken);

    // ---- twincat_ibn_arm (test support) ----------------------------------------

    // Decision 2026-10-19 (issue #272): IBN Live is armed only by a human in the host window
    // (Approvals view); the production surface (TwinCatMcpTools.IbnLive.cs) may only propose.
    // The live suite (IbnLiveIntegrationTests, run against a --diagnostics host) still arms the
    // fixture's harmless probe GVL over MCP, so the tool lives on here — same name, same
    // behaviour, never registered for released clients.
    [McpServerTool(Name = "twincat_ibn_arm", ReadOnly = false, Destructive = true)]
    [Description("TEST SUPPORT ONLY — never registered for released clients; in production IBN Live is " +
                 "armed only by a human in the host window (Approvals view). " +
                 "Open a time-boxed IBN control window (auto-expiry, ≤120 s, renewable). " +
                 "Loads the actuator allowlist and starts the heartbeat that writes the test " +
                 "PRG's bEnable bit — without an active arm window every twincat_ibn_propose is " +
                 "refused, and without the heartbeat the PRG falls back to its home position. " +
                 "Returns JSON { armed, armToken, expiresUtc, ttlSeconds, gvlPath, allowlist }. " +
                 "Arming does NOT move anything. Only registered when the host is started with " +
                 "--diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.")]
    public string IbnArm(
        [Description("Control-window lifetime in seconds (default 60, max 120). The window " +
                     "auto-expires; re-arm to renew.")] int? ttlSeconds = null,
        [Description("Actuator ids permitted to be addressed by a proposal during this window. " +
                     "A proposal naming an actuator not on this list is refused.")] string[]? actuatorAllowlist = null,
        [Description("Base symbol path of the test PRG control/status struct. Default 'GVL_IBN'.")] string? gvlPath = null,
        [Description("Optional AMS Net ID of the target ADS server. Omit for the local TwinCAT runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ibn_arm", new { ttlSeconds, actuatorAllowlist, gvlPath, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                bridge.LiveCoordinator.Arm(ttlSeconds, actuatorAllowlist, gvlPath, amsNetId, adsPort)), cancellationToken);
    // ---- twincat_persistent_snapshot_test_confirm (test support) ----------------------

    // A restore executes only after a human confirms the host popup (SnapshotRestoreCoordinator.ConfirmProposal).
    // The live suite has no human, so a --diagnostics host offers this hook - same name pattern and policy as
    // twincat_ibn_arm: never registered for released clients (pinned by PersistentSnapshotSurfaceTests).
    [McpServerTool(Name = "twincat_persistent_snapshot_test_confirm", ReadOnly = false, Destructive = true)]
    [Description("TEST SUPPORT ONLY - never registered for released clients; in production a restore is " +
                 "confirmed only by a human in the host popup (Approvals view). Confirms the pending restore " +
                 "proposal restoreId as the popup's OK would: the host re-plans, writes and reads back. " +
                 "Returns JSON { restoreId, status, reason }. Only registered when the host is started with " +
                 "--diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.")]
    public string PersistentSnapshotTestConfirm(
        [Description("The restoreId returned by twincat_persistent_snapshot_restore.")] string restoreId,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_persistent_snapshot_test_confirm", new { restoreId }, () =>
            bridge.Dispatcher.Run(() =>
            {
                if (bridge.SnapshotRestore.Get(restoreId) is null) return $"[Error] Unknown restoreId '{restoreId}'.";
                var done = bridge.SnapshotRestore.ConfirmProposal(restoreId.Trim());
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    restoreId = done.RestoreId,
                    status = TwinCat.SnapshotRestoreCoordinator.StateName(done.State),
                    reason = done.Reason,
                });
            }), cancellationToken);
}
