using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-controls: the control catalog of the project's packages (describe_controls) and placing any control of it
// (place_control) — into the page's root or a container control, checked against the control's description.
internal sealed partial class HmiCoordinator
{
    internal const int MaxListedControls = 400;

    public (string Summary, string Json) DescribeControls(string? hmiProject, string? type, string? filter, bool includeHidden)
    {
        var (project, snap, error) = LoadSnapshot(hmiProject);
        if (snap is null) return (error!, "{}");
        var catalog = HmiControlCatalog.Load(project!.ProjectDirectory);
        if (catalog.Types.Count == 0)
            return ($"[Error] no control descriptions found for '{project.Name}': {string.Join("; ", catalog.Problems.DefaultIfEmpty("the packages hold none"))}", "{}");

        if (string.IsNullOrWhiteSpace(type))
        {
            var list = catalog.Types
                .Where(t => includeHidden || t.Visible)
                .Where(t => string.IsNullOrWhiteSpace(filter) || t.FullName.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) ||
                            (t.DisplayName?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase).Take(MaxListedControls)
                .Select(t => new { type = t.FullName, t.DisplayName, container = t.Container, width = t.DesignWidth, height = t.DesignHeight, t.Package })
                .ToList();
            string summary = $"{list.Count} control type(s) in the packages of '{project.Name}'" +
                             (string.IsNullOrWhiteSpace(filter) ? "" : $" matching '{filter}'") +
                             $" ({string.Join(", ", catalog.Sources)}); {list.Count(c => c.container)} container(s). " +
                             "Pass type for its attributes.";
            return (summary, JsonSerializer.Serialize(new { hmiProject = project.Name, controls = list, problems = catalog.Problems }, JsonOpts));
        }

        var (t, findError) = catalog.Find(type);
        if (t is null) return ($"[Error] {findError}", "{}");
        var attrs = catalog.AllAttributes(t)
            .Where(a => includeHidden || a.Visible)
            .Where(a => string.IsNullOrWhiteSpace(filter) || a.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) ||
                        (a.PropertyName?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (a.Category?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(a => new
            {
                attribute = a.Name, a.PropertyName, a.Category, a.Type, a.Bindable, a.ReadOnly, a.DeclaredIn,
                description = a.Description is { Length: > 160 } d ? d[..157] + "…" : a.Description,
            }).ToList();
        string chain = string.Join(" → ", Chain(catalog, t));
        string s = $"{t.FullName} ({t.DisplayName ?? t.Name}, {t.Package}): {(t.Container ? "container — takes child controls" : "no children")}, " +
                   $"design size {t.DesignWidth?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}×{t.DesignHeight?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}; " +
                   $"{attrs.Count} attribute(s){(string.IsNullOrWhiteSpace(filter) ? "" : $" matching '{filter}'")}, {attrs.Count(a => a.Bindable && !a.ReadOnly)} bindable. Base chain: {chain}.";
        return (s, JsonSerializer.Serialize(new
        {
            hmiProject = project.Name, type = t.FullName, t.DisplayName, t.Description, container = t.Container,
            designWidth = t.DesignWidth, designHeight = t.DesignHeight, baseChain = Chain(catalog, t), attributes = attrs,
        }, JsonOpts));
    }

    private static List<string> Chain(HmiControlCatalog catalog, HmiControlType t)
    {
        var chain = new List<string>();
        for (HmiControlType? c = t; c is not null && chain.Count < 20; c = c.Base is null ? null : catalog.Find(c.Base).Type)
            chain.Add(c.FullName);
        return chain;
    }

    public (string Summary, string Json) PlaceControl(string? hmiProject, string? page, string? type, string? id, string? parentId,
        int left, int top, int? width, int? height, string? widthUnit, string? heightUnit, string? attributesJson, bool replace,
        int confirmTimeoutSeconds)
    {
        string? pagePath = HmiPaths.Normalize(page);
        if (pagePath is null || HmiComponents.PageKind(pagePath) is not ("view" or "content" or "usercontrol"))
            return ("[Error] page must be a project-relative .view, .content or .usercontrol path, e.g. 'Desktop.view'.", "{}");
        if (string.IsNullOrWhiteSpace(type)) return ("[Error] type is required, e.g. 'TcHmiButton' or 'TcHmi.Controls.System.TcHmiRegion'.", "{}");
        if (type.Trim().EndsWith("TcHmiUserControlHost", StringComparison.OrdinalIgnoreCase))
            return ("[Error] for a user control use twincat_hmi_place_user_control (it checks the user control's parameters).", "{}");
        string controlId = (id ?? "").Trim();
        if (!HmiPlacement.IsValidId(controlId))
            return ("[Error] id must be a control id: letters, digits, '_' and '-', not starting with a digit.", "{}");
        var (values, attrError) = HmiPlacement.ParseParameters(attributesJson);
        if (values is null) return ("[Error] " + attrError?.Replace("parameters", "attributes", StringComparison.Ordinal), "{}");
        if (left < 0 || top < 0) return ("[Error] left and top must not be negative.", "{}");
        var layout = new HmiLayout(left, top, width, height, (widthUnit ?? "px").Trim(), (heightUnit ?? "px").Trim());
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        var (project, snap, error) = LoadSnapshot(hmiProject);
        if (snap is null) return (error!, "{}");
        var catalog = HmiControlCatalog.Load(project!.ProjectDirectory);
        var (controlType, findError) = catalog.Find(type);
        if (controlType is null)
            return ($"[Error] {findError ?? "unknown control type"}" + (catalog.Problems.Count > 0 ? " " + string.Join("; ", catalog.Problems) : ""), "{}");

        HmiFileContent current;
        try
        {
            current = Session.ReadHmiFile(project.Name, pagePath);
            if (Session.IsHmiFileOpen(project.Name, pagePath)) return (OpenInEditorError(pagePath), "{}");
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }

        var (placed, placeError) = HmiPlacement.PlaceControl(current.Source, catalog, controlType, controlId, layout, values, parentId, replace);
        if (placed is null) return ($"[Error] {placeError} Nothing was written.", "{}");

        // a region pointing at a content that does not exist is the most common slip — say it before asking
        string? targetNote = null;
        if (values.FirstOrDefault(v => HmiControlCatalog.Attribute(catalog.AllAttributes(controlType), v.Key)?.Name
                .Equals(HmiMarkup.TargetContentAttribute, StringComparison.OrdinalIgnoreCase) ?? false).Value is { ValueKind: JsonValueKind.String } tc &&
            tc.GetString() is { } target && !target.Contains('%', StringComparison.Ordinal) &&
            !snap.Files.ContainsKey(HmiPaths.Normalize(target) ?? target))
            targetNote = $" Note: target content '{target}' does not exist on disk yet.";

        var request = new HmiWriteRequest(HmiWriteKind.WriteFile, project.Name, project.ProjectDirectory, pagePath, null,
            NewValue: $"{(placed.Replaced ? "replace" : "add")} {controlType.Name} '{controlId}'" +
                      (string.IsNullOrWhiteSpace(parentId) ? "" : $" in '{parentId}'") +
                      $" ({placed.Attributes.Count} attribute(s){(placed.Attributes.Count > 0 ? ": " + string.Join(", ", placed.Attributes) : "")})",
            CurrentValue: $"{current.Source.Length} characters", WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        string? writeError = WriteAndVerify(project.Name, pagePath, placed.Text);
        if (writeError is not null) return (writeError, "{}");
        string summary = $"{(placed.Replaced ? "Replaced" : "Placed")} {controlType.Name} '{controlId}' in '{pagePath}'" +
                         (string.IsNullOrWhiteSpace(parentId) ? "" : $" (inside '{parentId}')") + $" line {placed.Line} — " +
                         $"{Describe(outcome)}, verified by read-back.{targetNote} Run twincat_hmi_check_bindings, then twincat_hmi_build.";
        return (summary, JsonSerializer.Serialize(new
        {
            hmiProject = project.Name, page = pagePath, type = controlType.FullName, id = controlId, parentId, replaced = placed.Replaced,
            line = placed.Line, attributes = placed.Attributes, markup = placed.Markup, confirmation = ConfirmationWire(outcome),
        }, JsonOpts));
    }
}
