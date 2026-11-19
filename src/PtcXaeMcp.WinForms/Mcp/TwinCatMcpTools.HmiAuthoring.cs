using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

// hmi-authoring — see, check and extend an HMI project: components with their parameters, binding check,
// localized texts, placing user controls, scaffolding a page for a symbol.
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_hmi_list_components", ReadOnly = true)]
    [Description("List the pages, user controls, functions and languages of an HMI project. Read from the saved files: " +
                 "views, contents, user controls WITH their parameters (propertyName, attribute, type, required), functions " +
                 "with arguments, languages, themes, and which page uses which user control/content/function. Use it " +
                 "before twincat_hmi_place_user_control.")]
    public string HmiListComponents(
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_components", new { hmiProject }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ListComponents(hmiProject))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_check_bindings", ReadOnly = true)]
    [Description("Check that every binding and reference in HMI pages resolves. Saved files: %s% server symbols against the mapped symbols, %i% " +
                 "internal symbols, %pp% user-control parameters, %ctrl% control ids, %f%/trigger functions, %l% texts in " +
                 "every language, user-control hosts (target exists, required parameters), region targets, duplicate ids. " +
                 "Findings with file, line, severity (error/warning/info) and a 'did you mean'.")]
    public string HmiCheckBindings(
        [Description("Pages to check, project-relative (.view/.content/.usercontrol). Default: all.")] string[]? paths = null,
        [Description("Also list info findings (framework functions and domain symbols that are not checked).")] bool includeInfo = false,
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_check_bindings", new { hmiProject, paths, includeInfo }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).CheckBindings(hmiProject, paths, includeInfo))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_list_localization", ReadOnly = true)]
    [Description("List the localized texts of an HMI project and what is missing. Saved files: languages with their texts, keys missing per " +
                 "language, keys used (%l%Key%/l%) but not defined, keys defined but unused.")]
    public string HmiListLocalization(
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_localization", new { hmiProject }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ListLocalization(hmiProject))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_set_localized_texts", ReadOnly = false, Destructive = true)]
    [Description("Set or remove localized texts in existing languages. One confirmation for all: " +
                 "{ \"de\": { \"Start\": \"Start\", \"Old\": null }, \"en\": { \"Start\": \"Start\" } } (null removes). Existing " +
                 "keys keep their place, new keys are appended. Use in a page as %l%Key%/l%. " + ConfirmNote)]
    public string HmiSetLocalizedTexts(
        [Description("JSON object: locale → { key → text | null }.")] string texts,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_set_localized_texts", new { hmiProject, textsLength = texts?.Length, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .SetLocalizedTexts(hmiProject, texts, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_place_user_control", ReadOnly = false, Destructive = false)]
    [Description("Place a project user control on a view or content. Adds a TcHmiUserControlHost as last child of the " +
                 "page's root (or replaces the host with the same id when replace=true). parameters by propertyName from " +
                 "twincat_hmi_list_components, e.g. { \"Safety\": \"%s%PLC1.GVL.Safety%/s%\", \"Window\": 40 }; objects/arrays " +
                 "are written as JSON. Unknown or missing required parameters are refused. Size defaults to the control's " +
                 "design size. " + ConfirmNote)]
    public string HmiPlaceUserControl(
        [Description("Target page, project-relative, e.g. 'Desktop.view' or 'Contents/Main.content'.")] string page,
        [Description("The user control, e.g. 'UserControls/X/X.usercontrol'.")] string userControl,
        [Description("Control id of the host, unique in the page.")] string id,
        [Description("Left in px.")] int left = 0,
        [Description("Top in px.")] int top = 0,
        [Description("Width; default: design width of the user control.")] int? width = null,
        [Description("Height; default: design height of the user control.")] int? height = null,
        [Description("'px' (default) or '%'.")] string? widthUnit = null,
        [Description("'px' (default) or '%'.")] string? heightUnit = null,
        [Description("JSON object propertyName → value (string, number, bool, object, array; a binding as string).")] string? parameters = null,
        [Description("Replace the existing host with this id instead of refusing.")] bool replace = false,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_place_user_control",
            new { hmiProject, page, userControl, id, left, top, width, height, widthUnit, heightUnit, parameters, replace, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .PlaceUserControl(hmiProject, page, userControl, id, left, top, width, height, widthUnit, heightUnit,
                        parameters, replace, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_check_liveview", ReadOnly = false, Destructive = false)]
    [Description("Load the HMI live view in a headless browser and report what broke. Reports JS exceptions, console " +
                 "errors/warnings, failed requests, whether the framework shows a view, and the rendered controls (count, " +
                 "controls without size). Optional: switch to another view, take a PNG screenshot, evaluate a JavaScript " +
                 "expression after load (result as JSON; may write symbols through the server, so not read-only). Run " +
                 "twincat_hmi_build first. Needs Edge, Chrome or a Playwright Chromium.")]
    public async Task<string> HmiCheckLiveView(
        [Description("Live-view URL. Default: http://127.0.0.1:<HmiCommunicationServerPort>/<newest liveview_*.html> of the project.")] string? url = null,
        [Description("View to load after start, project-relative, e.g. 'Views/Test.view'. Default: the startup view.")] string? view = null,
        [Description("Max wait for the framework and view in ms, 1000-120000. Default 30000.")] int waitMs = HmiLiveViewCheck.DefaultWaitMs,
        [Description("Viewport width in px. Default 1280.")] int width = 1280,
        [Description("Viewport height in px. Default 800.")] int height = 800,
        [Description("Absolute path of a PNG screenshot to write.")] string? screenshotPath = null,
        [Description("JavaScript evaluated after load (promises awaited); its value is returned as JSON.")] string? expression = null,
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => await bridge.Activity.TraceAsync("twincat_hmi_check_liveview",
            new { hmiProject, url, view, waitMs, width, height, screenshotPath, expressionLength = expression?.Length }, async () =>
        {
            if (waitMs < 1000 || waitMs > HmiLiveViewCheck.MaxWaitMs)
                return $"[Error] waitMs must be between 1000 and {HmiLiveViewCheck.MaxWaitMs}.";
            if (width is < 320 or > 3840 || height is < 240 or > 2160)
                return "[Error] width must be 320-3840 and height 240-2160.";
            if (screenshotPath is { Length: > 0 } && (!Path.IsPathFullyQualified(screenshotPath) ||
                    !screenshotPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                return "[Error] screenshotPath must be an absolute path ending in .png.";
            if (expression is { Length: > HmiLiveViewCheck.MaxExpressionLength })
                return $"[Error] expression is longer than {HmiLiveViewCheck.MaxExpressionLength} characters.";
            string? v = null;
            if (!string.IsNullOrWhiteSpace(view))
            {
                v = HmiPaths.Normalize(view);
                if (v is null || HmiComponents.PageKind(v) != "view")
                    return "[Error] view must be a project-relative .view path, e.g. 'Views/Test.view'.";
            }

            string target;
            if (!string.IsNullOrWhiteSpace(url))
            {
                if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                    return "[Error] url must be an absolute http(s) URL.";
                target = u.AbsoluteUri;
            }
            else
            {
                string? found = null;
                string error = bridge.Dispatcher.Run(() =>
                {
                    var (u, e) = new HmiCoordinator(bridge.Dispatcher.Session).LiveViewUrl(hmiProject);
                    found = u;
                    return e ?? "";
                });
                if (found is null) return error;
                target = found;
            }

            var (summary, json) = await HmiLiveViewCheck.RunAsync(new HmiLiveViewOptions(target, v, waitMs,
                HmiLiveViewCheck.DefaultSettleMs, width, height, screenshotPath, expression), cancellationToken);
            return HmiResult((summary, json));
        }, cancellationToken);

    [McpServerTool(Name = "twincat_hmi_describe_controls", ReadOnly = true)]
    [Description("List the controls of the project's HMI packages, or one control's attributes. Read from each control's " +
                 "Description.json in the restored NuGet packages (framework and Beckhoff controls): type, container or not, " +
                 "design size; with type all attributes incl. the base chain (attribute name, propertyName, type, bindable, " +
                 "read-only, declaring control). Use it before twincat_hmi_place_control.")]
    public string HmiDescribeControls(
        [Description("Control type, full (TcHmi.Controls.Beckhoff.TcHmiButton) or short (TcHmiButton). Default: list all controls.")] string? type = null,
        [Description("Substring filter: on control names (list) or attribute name/propertyName/category (with type).")] string? filter = null,
        [Description("Also list controls/attributes the designer hides.")] bool includeHidden = false,
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_describe_controls", new { hmiProject, type, filter, includeHidden }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).DescribeControls(hmiProject, type, filter, includeHidden))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_place_control", ReadOnly = false, Destructive = false)]
    [Description("Place any control of the project's packages on a view, content or user control. Adds it as last child of the " +
                 "page root or of parentId (must be a container: TcHmiContainer, TcHmiGrid, …), or replaces the control with " +
                 "the same id and type. attributes by propertyName, displayName or attribute name, checked against the " +
                 "control's description: unknown, read-only, layout ones and bindings on non-bindable ones are refused; " +
                 "objects/arrays as JSON. E.g. a region: type TcHmiRegion, { \"TargetContent\": \"Contents/A.content\" }. " + ConfirmNote)]
    public string HmiPlaceControl(
        [Description("Target page, project-relative, e.g. 'Desktop.view' or 'Contents/Main.content'.")] string page,
        [Description("Control type, full or short, e.g. 'TcHmiButton', 'TcHmi.Controls.System.TcHmiRegion'.")] string type,
        [Description("Control id, unique in the page.")] string id,
        [Description("Id of the container control to place into. Default: the page's root control.")] string? parentId = null,
        [Description("Left in px.")] int left = 0,
        [Description("Top in px.")] int top = 0,
        [Description("Width; default: the control's design width.")] int? width = null,
        [Description("Height; default: the control's design height.")] int? height = null,
        [Description("'px' (default) or '%'.")] string? widthUnit = null,
        [Description("'px' (default) or '%'.")] string? heightUnit = null,
        [Description("JSON object attribute → value, e.g. { \"Text\": \"Start\", \"StateSymbol\": \"%s%PLC1.MAIN.bStart%/s%\" }.")] string? attributes = null,
        [Description("Replace the existing control with this id (same type) instead of refusing.")] bool replace = false,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_place_control",
            new { hmiProject, page, type, id, parentId, left, top, width, height, widthUnit, heightUnit, attributes, replace, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .PlaceControl(hmiProject, page, type, id, parentId, left, top, width, height, widthUnit, heightUnit,
                        attributes, replace, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_check_access", ReadOnly = true)]
    [Description("Check that the user groups may read and write the symbols the HMI pages bind. Saved files: every %s% use against " +
                 "the group rights of the server configuration (SYMBOLACCESS default, explicit symbol entries). A symbol a " +
                 "page writes (TwoWay binding, state symbol, WriteToSymbol action) needs write access. In the engineering " +
                 "server everything runs as administrator; on a TF2000 runtime a missing right leaves the control empty or " +
                 "drops the write. Default groups: enabled groups with users, except administrators and guests.")]
    public string HmiCheckAccess(
        [Description("User groups to check, e.g. ['Operators']. Default: every enabled group with users except __SystemAdministrators/__SystemGuests.")] string[]? groups = null,
        [Description("Pages to check, project-relative (.view/.content/.usercontrol). Default: all.")] string[]? paths = null,
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_check_access", new { hmiProject, groups, paths }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).CheckAccess(hmiProject, groups, paths))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_server_diagnostics", ReadOnly = true)]
    [Description("Diagnose a TF2000 or engineering HMI server through its WebSocket API. " +
                 "Reports version, license state with used clients/targets, extensions and their state, outdated symbols, " +
                 "sessions, event-log counts per severity, active alarms and the newest events. Default target: the " +
                 "engineering server of the HMI project. For a runtime pass endpoint (e.g. https://192.168.1.10:2020) and, " +
                 "if it requires login, user plus the NAME of an environment variable of the MCP host holding the password.")]
    public async Task<string> HmiServerDiagnostics(
        [Description("Server URL, e.g. https://192.168.1.10:2020. Default: http://127.0.0.1:<HmiCommunicationServerPort> of the HMI project.")] string? endpoint = null,
        [Description("User to log in as. Default: no login (local engineering server).")] string? user = null,
        [Description("Name of an environment variable of the MCP host that holds the password (the password itself is never a parameter).")] string? passwordVariable = null,
        [Description("Accept a self-signed server certificate (https/wss). Default false.")] bool trustServerCertificate = false,
        [Description("Lowest event severity to list: verbose, info, warning (default), error, critical.")] string? minSeverity = null,
        [Description("Max events to list, 0-200. Default 20.")] int maxEvents = 20,
        [Description("Timeout in ms, 1000-60000. Default 15000.")] int timeoutMs = 15000,
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => await bridge.Activity.TraceAsync("twincat_hmi_server_diagnostics",
            new { hmiProject, endpoint, user, passwordVariable, trustServerCertificate, minSeverity, maxEvents, timeoutMs }, async () =>
        {
            if (maxEvents is < 0 or > 200) return "[Error] maxEvents must be 0-200.";
            if (timeoutMs is < 1000 or > 60000) return "[Error] timeoutMs must be 1000-60000.";
            int? level = string.IsNullOrWhiteSpace(minSeverity) ? 2 : HmiServerApi.SeverityLevel(minSeverity);
            if (level is null) return "[Error] minSeverity must be verbose, info, warning, error or critical.";
            string? password = null;
            if (!string.IsNullOrWhiteSpace(passwordVariable))
            {
                if (string.IsNullOrEmpty(user)) return "[Error] passwordVariable needs user.";
                password = Environment.GetEnvironmentVariable(passwordVariable.Trim());
                if (password is null) return $"[Error] environment variable '{passwordVariable}' is not set in the MCP host process.";
            }

            string target;
            string? note = null;
            if (!string.IsNullOrWhiteSpace(endpoint)) target = endpoint.Trim();
            else
            {
                string? found = null;
                string error = bridge.Dispatcher.Run(() =>
                {
                    var (e, err) = new HmiCoordinator(bridge.Dispatcher.Session).EngineeringEndpoint(hmiProject);
                    found = e;
                    return err ?? "";
                });
                if (found is null) return error;
                target = found;
                note = "Engineering server: local clients get every right and the license state is 'Engineering'; a runtime may differ.";
            }
            var result = await HmiServerApi.RunDiagnosticsAsync(target, user, password, trustServerCertificate, level.Value, maxEvents,
                timeoutMs, note, HmiCoordinator.ToolJsonOptions, cancellationToken);
            return HmiResult(result);
        }, cancellationToken);

    [McpServerTool(Name = "twincat_hmi_publish", ReadOnly = false, Destructive = true)]
    [Description("Publish the HMI project to a TF2000 server with a saved publish profile. It replaces the project there. " +
                 "The profile, created once in the XAE, holds target, user and the password (the XAE keeps it encrypted; " +
                 "the MCP never passes one). Reads the target before and after: success only when it then reports this project. user + " +
                 "passwordVariable (NAME of an environment variable of the MCP host) are only for these reads. " + ConfirmNote)]
    public async Task<string> HmiPublish(
        [Description("Name of a publish profile saved in the project (create it once in the XAE's Publish dialog, password saved).")] string profile,
        [Description("Optional check: the TF2000 server URL the profile must point at, e.g. https://192.168.1.10:2020.")] string? targetUrl = null,
        [Description("User for reading the target before and after (default: the profile's user).")] string? user = null,
        [Description("Name of an environment variable of the MCP host holding that user's password (the password itself is never a parameter).")] string? passwordVariable = null,
        [Description("Accept a self-signed certificate for the reads before and after (https). Default false.")] bool trustServerCertificate = false,
        [Description("Max seconds to wait for the publish, 30-1800. Default 300.")] int timeoutSeconds = 300,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = 120,
        CancellationToken cancellationToken = default)
        => await bridge.Activity.TraceAsync("twincat_hmi_publish",
            new { hmiProject, profile, targetUrl, user, passwordVariable, trustServerCertificate, timeoutSeconds },
            async () =>
        {
            if (timeoutSeconds is < HmiCoordinator.MinPublishTimeoutSeconds or > HmiCoordinator.MaxPublishTimeoutSeconds)
                return $"[Error] timeoutSeconds must be {HmiCoordinator.MinPublishTimeoutSeconds}-{HmiCoordinator.MaxPublishTimeoutSeconds}.";
            if (string.IsNullOrWhiteSpace(profile))
                return "[Error] pass profile: the name of a publish profile saved in the XAE (Publish dialog of the HMI project, " +
                       "password saved). The Automation Interface publishes only with such a profile.";
            string profileName = profile.Trim();
            HmiCoordinator.HmiPublishProfileInfo? found = null;
            string findError = bridge.Dispatcher.Run(() =>
            {
                var (p, e) = new HmiCoordinator(bridge.Dispatcher.Session).FindPublishProfile(hmiProject, profileName);
                found = p;
                return e ?? "";
            });
            if (found is null) return findError;
            if (!string.IsNullOrWhiteSpace(targetUrl) && !SameServer(targetUrl, found.Url))
                return $"[Error] targetUrl '{targetUrl}' is not the target of profile '{profileName}' ({found.Url}).";
            string? profileUser = found.User;
            if (!Uri.TryCreate(found.Url, UriKind.Absolute, out Uri? url) || url.Scheme is not ("http" or "https"))
                return $"[Error] profile '{profileName}' points at '{found.Url}', not an http(s) URL of a TF2000 server.";
            string target = url.GetLeftPart(UriPartial.Authority);
            string? readUser = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
            string? password = null;
            if (!string.IsNullOrWhiteSpace(passwordVariable))
            {
                readUser ??= profileUser;
                if (string.IsNullOrEmpty(readUser)) return "[Error] passwordVariable needs user.";
                password = Environment.GetEnvironmentVariable(passwordVariable.Trim());
                if (password is null) return $"[Error] environment variable '{passwordVariable}' is not set in the MCP host process.";
            }

            // An unreachable or refusing target stops here; missing read rights only make the result unverifiable.
            const int readTimeoutMs = 15000;
            HmiPublishTargetState before = await HmiCoordinator.ReadPublishTargetAsync(target, readUser, password, trustServerCertificate,
                readTimeoutMs, cancellationToken);
            if (before.Error is { } readError && !readError.StartsWith(HmiCoordinator.NoRightsPrefix, StringComparison.Ordinal))
                return $"[Error] {readError} Nothing was published.";

            HmiPublishRun? run = null;
            string dispatched = bridge.Dispatcher.Run(() =>
            {
                run = new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer).PublishProject(hmiProject, profileName, target,
                    profileUser, timeoutSeconds, confirmTimeoutSeconds, before);
                return run.Refusal ?? "published";
            });
            if (run is null) return dispatched;
            HmiPublishTargetState? after = run.Outcome is { Started: true, StillRunning: false }
                ? await HmiCoordinator.ReadPublishTargetAsync(target, readUser, password, trustServerCertificate, readTimeoutMs, cancellationToken)
                : null;
            return HmiResult(HmiCoordinator.PublishReport(target, before, run, after));
        }, cancellationToken);

    private static bool SameServer(string a, string b) =>
        Uri.TryCreate(a.Trim(), UriKind.Absolute, out Uri? x) && Uri.TryCreate(b.Trim(), UriKind.Absolute, out Uri? y)
        && string.Equals(x.GetLeftPart(UriPartial.Authority), y.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    [McpServerTool(Name = "twincat_hmi_scaffold_symbol_view", ReadOnly = false, Destructive = false)]
    [Description("Generate a .content with one row per field of a server symbol. Label + checkbox (BOOL), " +
                 "numeric input (number), textbox (string), or read-only textblocks; nested structs get a header. The shape " +
                 "comes from the symbol's current value read through the HMI server, or from sampleValue. Arrays are listed " +
                 "as skipped. dryRun returns the markup without writing. " + ConfirmNote)]
    public string HmiScaffoldSymbolView(
        [Description("Server symbol, e.g. 'PLC1.MAIN.stAxis' (mapped name, without %s%).")] string symbol,
        [Description("Target .content, project-relative, e.g. 'Contents/Axis.content'.")] string path,
        [Description("Only display values (textblocks, disabled checkboxes).")] bool readOnly = false,
        [Description("Struct nesting depth, 1-6. Default 3.")] int maxDepth = 3,
        [Description("JSON value of the symbol's shape, used instead of reading the server.")] string? sampleValue = null,
        [Description("Return the markup only, write nothing.")] bool dryRun = false,
        [Description("Replace an existing file.")] bool overwrite = false,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_scaffold_symbol_view",
            new { hmiProject, symbol, path, readOnly, maxDepth, sampleValueLength = sampleValue?.Length, dryRun, overwrite, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .ScaffoldSymbolView(hmiProject, symbol, path, readOnly, maxDepth, sampleValue, dryRun, overwrite,
                        confirmTimeoutSeconds))), cancellationToken);
}
