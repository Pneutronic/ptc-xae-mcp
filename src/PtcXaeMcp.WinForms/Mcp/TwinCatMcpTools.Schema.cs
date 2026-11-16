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
    [Description("Validate a JSON IO schema file without applying it. Returns Valid/Invalid with the issues found. " +
                 "Checks ioPoints[].symbol (full PLC instance path, e.g. MAIN.aCyl[1].bOut) and refuses schema fields removed in 4.0.0 (the error names the migration).")]
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
        "Builds the EtherCAT tree, links IO points that name a PLC symbol and exports; progress shows in the server window. " +
        "Fail-closed by default: PLC project and EtherCAT master must match exactly (no fallback to the first one) and the target " +
        "master must be provably empty. An IO point links to the AT %I*/%Q* variable named by ioPoints[].symbol, the full PLC " +
        "instance path (MAIN.fbCyl.LimitSwitchWorkingPosition, MAIN.aCyl[1].bOut): looked up exactly in the process image, never a " +
        "same-named member of another instance. A point without symbol only builds hardware. " +
        "An analog channel is found from the terminal's PDOs (EL3174 channel 1 = 'AI Standard Channel 1^Value', the entry whose " +
        "width fits the point's type); set ioLeaf only for another entry (a status bit). " +
        "During the build the XAE's 'Sync Master' and 'Double use of the same Ethernet interface' boxes are answered with OK and " +
        "the 'unrestored variable links' question with 'Ja' (keep the link information); answered dialogs are listed in the " +
        "result, any other blocking dialog ends the run with an [Error] naming it.")]
    public async Task<string> ApplySchema(
        [Description("Absolute path to the JSON schema file")] string schemaPath,
        [Description("PLC project name. Empty = the schema's twincat.plcProjectName.")] string? plcProject = null,
        [Description("EtherCAT master name. Empty = the schema's twincat.deviceName.")] string? etherCatMaster = null,
        [Description("Brownfield: exact target EtherCAT master tree path (e.g. 'TIID^Device 1 (EtherCAT)'); resolved exactly and forces fail-closed mode.")] string? targetMasterPath = null,
        [Description("Default true: exact PLC/master match, empty-check aborts on any uncertainty. false = lenient greenfield resolution (single-PLC / first-master fallback, prefix match on the master name).")] bool failClosed = true,
        [Description("false skips the full solution build so a brownfield apply does not re-bind the existing PLC project; the symbols the schema names must then already be in the process image (twincat_build_plc). Default true.")] bool autoBuild = true,
        CancellationToken cancellationToken = default)
        => await bridge.Activity.TraceAsync("twincat_apply_schema",
            new { schemaPath, plcProject, etherCatMaster, targetMasterPath, failClosed, autoBuild }, async () =>
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
                    targetMasterPath, failClosed, autoBuild);
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
