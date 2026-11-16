using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_get_status", ReadOnly = true)]
    [Description("Get XAE status: IDE connection, active project/PLC, TwinCAT service, policy. " +
                 "The access policy (read / engineer / runtime, blocked and allowed NetIds) is set only in the window or by " +
                 "environment variables, never by a tool.")]
    public string GetStatus(CancellationToken cancellationToken = default) =>
        bridge.Activity.Trace("twincat_get_status", null,
            () => host.GetStatus() + $"\nAccess policy: {bridge.Policy.Current.Summary()}", cancellationToken);

    [McpServerTool(Name = "twincat_get_log", ReadOnly = true)]
    [Description("Get the log shown in the MCP server window. " +
                 "The log grows for the whole host session; only the newest maxChars characters are returned, behind a " +
                 "note of how much was omitted.")]
    public string GetLog(
        [Description("Max log characters returned, newest kept (default 20000); 0 = full log.")] int maxChars = 20000,
        CancellationToken cancellationToken = default) =>
        bridge.Activity.Trace("twincat_get_log", new { maxChars }, () => host.RunLog.Tail(maxChars), cancellationToken);

    [McpServerTool(Name = "twincat_stop", ReadOnly = false, Destructive = true)]
    [Description("Stop the currently running TwinCAT operation (like clicking the Stop button).")]
    public string Stop(CancellationToken cancellationToken = default) =>
        // Control command: bypass the gate so it can never deadlock the suite.
        bridge.Activity.Trace("twincat_stop", null, () =>
        {
            host.RequestStop();
            return "Stop signal sent.";
        }, cancellationToken, bypassGate: true);

    [McpServerTool(Name = "twincat_validate_schema", ReadOnly = true)]
    [Description("Validate a JSON IO schema file without applying it. Returns Valid/Invalid with the issues found.")]
    public string ValidateSchema(
        [Description("Absolute path to the JSON schema file")] string schemaPath,
        [Description("Optional brownfield target EtherCAT master tree path (e.g. 'TIID^Device 1 (EtherCAT)'); its format is validated too.")] string? targetMasterPath = null,
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
    [Description("Apply a JSON IO schema to the active XAE project. " +
        "Builds the EtherCAT tree, creates the GVL, links variables and exports; progress shows in the server window. " +
        "Fail-closed by default: PLC project and EtherCAT master must match exactly (no fallback to the first one), the target " +
        "master must be provably empty, and an existing same-named GVL at the target path is an [Error] (nothing deleted) " +
        "unless overwriteExistingGvl. Links use the exact symbol <gvlName>.<varName>, never a same-named variable of another GVL. " +
        "During the build the XAE's 'Sync Master' and 'Double use of the same Ethernet interface' boxes are answered with OK and " +
        "the 'unrestored variable links' question with 'Ja' (keep the link information); answered dialogs are listed in the " +
        "result, any other blocking dialog ends the run with an [Error] naming it.")]
    public async Task<string> ApplySchema(
        [Description("Absolute path to the JSON schema file")] string schemaPath,
        [Description("PLC project name. Empty = the schema's twincat.plcProjectName.")] string? plcProject = null,
        [Description("EtherCAT master name. Empty = the schema's twincat.deviceName.")] string? etherCatMaster = null,
        [Description("Brownfield: exact target EtherCAT master tree path (e.g. 'TIID^Device 1 (EtherCAT)'); resolved exactly and forces fail-closed mode.")] string? targetMasterPath = null,
        [Description("Default true: exact PLC/master match, empty-check aborts on any uncertainty. false = lenient greenfield resolution (single-PLC / first-master fallback, prefix match on the master name).")] bool failClosed = true,
        [Description("false skips the full solution build so a brownfield apply does not re-bind the existing PLC project (build later with twincat_build_plc). Default true.")] bool autoBuild = true,
        [Description("Replace an existing same-named GVL at the exact target path (TIPC^<plc>^<plc> Project^GVLs^<gvlName>); its declarations and links are lost. Default false: collision = [Error], nothing deleted. Same as the schema's twincat.overwriteExistingGvl. Never deletes a same-named GVL elsewhere.")] bool overwriteExistingGvl = false,
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
