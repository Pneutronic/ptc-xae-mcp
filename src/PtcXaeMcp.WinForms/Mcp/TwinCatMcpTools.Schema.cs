using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_get_status", ReadOnly = true)]
    [Description("Get TwinCAT XAE environment status: IDE connectivity, the active project and PLC, the TwinCAT system service state " +
                 "and the access policy in force (read / engineer / runtime, blocked and allowed NetIds; set only in the window or by " +
                 "environment variables, never by a tool).")]
    public string GetStatus(CancellationToken cancellationToken = default) =>
        bridge.Activity.Trace("twincat_get_status", null,
            () => host.GetStatus() + $"\nAccess policy: {bridge.Policy.Current.Summary()}", cancellationToken);

    [McpServerTool(Name = "twincat_get_log", ReadOnly = true)]
    [Description("Get the log output visible in the Pneutronic XAE MCP window. The log grows for the whole host session, " +
                 "so only the newest maxChars characters are returned (default 20000) behind a note of how much was omitted; " +
                 "pass maxChars=0 for the full log.")]
    public string GetLog(
        [Description("Maximum number of log characters returned, newest kept (default 20000). 0 returns the full log.")] int maxChars = 20000,
        CancellationToken cancellationToken = default) =>
        bridge.Activity.Trace("twincat_get_log", new { maxChars }, () => host.RunLog.Tail(maxChars), cancellationToken);

    [McpServerTool(Name = "twincat_stop", ReadOnly = false, Destructive = true)]
    [Description("Stop the currently running TwinCAT operation (equivalent to clicking the Stop button).")]
    public string Stop(CancellationToken cancellationToken = default) =>
        // Control command: bypass the gate so it can never deadlock the suite.
        bridge.Activity.Trace("twincat_stop", null, () =>
        {
            host.RequestStop();
            return "Stop signal sent.";
        }, cancellationToken, bypassGate: true);

    [McpServerTool(Name = "twincat_validate_schema", ReadOnly = true)]
    [Description("Validate a JSON IO schema file without applying it. Returns validation result with any issues found. Optionally pre-validates a brownfield targetMasterPath.")]
    public string ValidateSchema(
        [Description("Absolute path to the JSON schema file")] string schemaPath,
        [Description("brownfield-guard: optional exact target EtherCAT master tree path (e.g. 'TIID^Device 1 (EtherCAT)'). When supplied, its format is validated.")] string? targetMasterPath = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_validate_schema", new { schemaPath, targetMasterPath }, () =>
        {
            var result = SchemaValidation.Validate(schemaPath);
            var brownfieldIssues = BrownfieldGuard.ValidateBrownfieldRequest(targetMasterPath);
            bool valid = result.IsValid && brownfieldIssues.Count == 0;

            var allIssues = result.Issues.Select(i => i.Format())
                .Concat(brownfieldIssues)
                .ToList();
            string issues = allIssues.Count > 0
                ? "\n" + string.Join("\n", allIssues.Select(i => "  - " + i))
                : string.Empty;
            return $"{(valid ? "Valid" : "Invalid")}: {result.Text}{issues}";
        }, cancellationToken);

    [McpServerTool(Name = "twincat_apply_schema", ReadOnly = false, Destructive = true)]
    [Description("Apply a JSON IO schema to the active TwinCAT XAE project. Builds EtherCAT tree, creates GVL, links variables, and exports. Progress is visible in the UI window. " +
        "Fail-closed by default (failClosed=true): the PLC project and EtherCAT master must match exactly (no fallback to the first PLC/master), the target master must be provably empty, and an existing GVL of the same name at the target path (TIPC^<plc>^<plc> Project^GVLs^<gvlName>) is an [Error] — nothing is deleted — unless overwriteExistingGvl=true. " +
        "Links use the exact process-image symbol <gvlName>.<varName>; a same-named variable of another GVL is never linked. " +
        "During the build the XAE's 'Sync Master' and 'Double use of the same Ethernet interface' boxes are answered with OK and the " +
        "'unrestored variable links' question with 'Ja' (keep the link information); every answered dialog is listed in the result, " +
        "and any other dialog that blocks the build ends the run with an [Error] naming it. " +
        "The GUI's Apply button keeps the lenient greenfield behaviour.")]
    public async Task<string> ApplySchema(
        [Description("Absolute path to the JSON schema file")] string schemaPath,
        [Description("PLC project name override. Leave empty to use the schema's twincat.plcProjectName (the GUI selection is never used).")] string? plcProject = null,
        [Description("EtherCAT master name override. Leave empty to use the schema's twincat.deviceName (the GUI selection is never used).")] string? etherCatMaster = null,
        [Description("brownfield-guard: exact target EtherCAT master tree path (e.g. 'TIID^Device 1 (EtherCAT)'). When set, the master is resolved exactly (no fuzzy fallback) and fail-closed mode is activated.")] string? targetMasterPath = null,
        [Description("Fail-closed mode (default true). The PLC project and EtherCAT master must match exactly, and the empty-check aborts on any COM uncertainty. Pass false for the lenient greenfield resolution (only PLC/first master fallback, StartsWith master match) the GUI uses.")] bool failClosed = true,
        [Description("brownfield-guard: when false, skip the full productive SolutionBuild.Build(true) so a brownfield apply does not re-bind the existing PLC project (new IO is translated by a later build_plc). Default true preserves current behaviour.")] bool autoBuild = true,
        [Description("Replace an existing GVL of the same name at the exact target path (TIPC^<plc>^<plc> Project^GVLs^<gvlName>); its declarations and links are lost. Default false: such a collision is an [Error] and nothing is deleted. Same as the schema's twincat.overwriteExistingGvl. Never deletes a same-named GVL anywhere else.")] bool overwriteExistingGvl = false,
        CancellationToken cancellationToken = default)
        => await bridge.Activity.TraceAsync("twincat_apply_schema",
            new { schemaPath, plcProject, etherCatMaster, targetMasterPath, failClosed, autoBuild, overwriteExistingGvl }, async () =>
        {
            try
            {
                var brownfieldIssues = BrownfieldGuard.ValidateBrownfieldRequest(targetMasterPath);
                if (brownfieldIssues.Count > 0)
                {
                    return "[Error] " + string.Join("; ", brownfieldIssues);
                }

                // Overrides travel as RunnerOptions; the runner reads the real schema file, so
                // relative export paths resolve next to it (issue #155 E-25, no temp copy).
                var log = await host.RunApplySchemaAsync(
                    schemaPath, plcProject, etherCatMaster, cancellationToken,
                    targetMasterPath, failClosed, autoBuild, overwriteExistingGvl);
                return string.Join("\n", log);
            }
            catch (InvalidOperationException ex)
            {
                return $"[Error] {ex.Message}";
            }
            catch (OperationCanceledException)
            {
                return McpErrors.Cancelled("apply schema cancelled");
            }
            catch (Exception ex)
            {
                return $"[Error] {ex.Message}";
            }
        }, cancellationToken);
}
