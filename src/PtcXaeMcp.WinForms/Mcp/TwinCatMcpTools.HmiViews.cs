using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

// hmi-views — create and change files of a TwinCAT HMI project (user controls, contents,
// functions, imported documents) via the HMI Automation Interface.
// Paths are project-relative with '/', e.g. 'UserControls/PtcCamCurve/PtcCamCurve.usercontrol'.
internal sealed partial class TwinCatMcpTools
{
    private const string HmiPathParam =
        "Project-relative path with '/', e.g. 'UserControls/Foo/Foo.usercontrol'.";

    [McpServerTool(Name = "twincat_hmi_read_file", ReadOnly = true)]
    [Description("Read the source text of a file in an HMI project.")]
    public string HmiReadFile(
        [Description(HmiPathParam)] string path,
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_read_file", new { hmiProject, path }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ReadFile(hmiProject, path))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_add_item", ReadOnly = false, Destructive = false)]
    [Description("Create a file in an HMI project from a TE2000 item template. Refuses an existing file. kind: " +
                 "view, content, usercontrol, function_js, function_ts, codebehind_js, codebehind_ts, css, html, js, " +
                 "json, ts, text, xml (usercontrol and function_* also create their .json). " + ConfirmNote)]
    public string HmiAddItem(
        [Description("Template kind (see the tool description).")] string kind,
        [Description("Target folder, project-relative, e.g. 'UserControls/Foo'; empty = project root.")]
        string? folder,
        [Description("File name without extension.")] string name,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_add_item", new { hmiProject, kind, folder, name, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .AddItem(hmiProject, folder, name, kind, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_write_file", ReadOnly = false, Destructive = true)]
    [Description("Replace the source text of an existing file in an HMI project. Identical content = 'Nothing to " +
                 "do'. " + ConfirmNote)]
    public string HmiWriteFile(
        [Description(HmiPathParam)] string path,
        [Description("Complete new content. Or sourceFile.")] string? source = null,
        [Description("Absolute path of a local file (max 5 MB) to write. Or source.")] string? sourceFile = null,
        [Description("Keep a JSON file's existing \"$schema\".")] bool keepSchema = true,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_write_file",
            new { hmiProject, path, sourceLength = source?.Length, sourceFile, keepSchema, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .WriteFile(hmiProject, path, source, sourceFile, keepSchema, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_import_file", ReadOnly = false, Destructive = false)]
    [Description("Import an external file (HTML, image, PDF, JS library) into an HMI project. Refuses an existing " +
                 "target. For .js/.css run twincat_hmi_build afterwards so the live view loads it. " + ConfirmNote)]
    public string HmiImportFile(
        [Description("Absolute path of the file to import.")] string sourcePath,
        [Description("Target folder, project-relative; empty = project root.")] string? folder = null,
        [Description("Target file name. Default: the source name.")] string? name = null,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_import_file", new { hmiProject, sourcePath, folder, name, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .ImportFile(hmiProject, sourcePath, folder, name, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_build", ReadOnly = false, Destructive = false)]
    [Description("Build an HMI project so changes reach the server's live view. Needed for new user controls, " +
                 "functions, contents. On failure see twincat_get_error_list.")]
    public string HmiBuild(
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description("Solution configuration. Default 'Debug'.")] string? configuration = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_build", new { hmiProject, configuration }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).Build(hmiProject, configuration))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_apply_bundle", ReadOnly = false, Destructive = true)]
    [Description("Write a package of HMI files from a manifest with one confirmation. Manifest: { \"name\": \"…\", " +
                 "\"entries\": [ { \"path\": \"UserControls/X/X.usercontrol\", \"kind\": \"usercontrol\", \"source\": " +
                 "\"X.usercontrol\" } ] }; 'source' is relative to the manifest. Existing files are replaced, missing " +
                 "ones created from 'kind' (not needed for a file an earlier entry's template creates). Validated " +
                 "before the popup; stops at the first error. " + ConfirmNote)]
    public string HmiApplyBundle(
        [Description("Absolute path of the bundle manifest (JSON).")] string manifestPath,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_apply_bundle", new { hmiProject, manifestPath, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .ApplyBundle(hmiProject, manifestPath, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_delete_item", ReadOnly = false, Destructive = true)]
    [Description("Delete a file or folder of an HMI project from project AND disk. No recycle bin. Refuses " +
                 "project infrastructure (Server, Properties, Packages) and the startup view. " + ConfirmNote)]
    public string HmiDeleteItem(
        [Description(HmiPathParam)] string path,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_delete_item", new { hmiProject, path, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .DeleteItem(hmiProject, path, confirmTimeoutSeconds))), cancellationToken);
}
