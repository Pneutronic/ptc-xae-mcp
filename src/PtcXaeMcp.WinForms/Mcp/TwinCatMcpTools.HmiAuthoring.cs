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
