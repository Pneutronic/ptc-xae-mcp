using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-authoring: twincat_hmi_place_user_control — put a TcHmiUserControlHost for a project user control into
// a view/content. Parameters are addressed by their propertyName (".usercontrol.json") and written as the
// markup attribute that file names; objects/arrays go into a target-attribute <script>, as TE2000 writes them.

internal sealed record HmiLayout(int Left, int Top, int? Width, int? Height, string WidthUnit, string HeightUnit);

internal sealed record HmiPlacementResult(string Text, string Markup, IReadOnlyList<string> Attributes, bool Replaced, int Line);

internal static partial class HmiPlacement
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\-]*$")]
    private static partial Regex IdPattern();

    internal const int DefaultWidth = 400;
    internal const int DefaultHeight = 300;

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>Parses the tool input { "Safety": "%s%PLC1.GVL.Safety%/s%", "BdcWindow": 40 }.</summary>
    public static (Dictionary<string, JsonElement>? Values, string? Error) ParseParameters(string? json)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return (d, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, "parameters must be a JSON object: { \"<propertyName>\": value, … }.");
            foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.Clone();
            return (d, null);
        }
        catch (JsonException ex) { return (null, $"parameters is not valid JSON: {ex.Message}"); }
    }

    /// <summary>
    /// Builds the host and inserts it as the last child of the page's root control, or — with
    /// <paramref name="replace"/> — in place of the existing host with the same id. Everything is validated first;
    /// null + error when anything does not fit.
    /// </summary>
    public static (HmiPlacementResult? Result, string? Error) Place(string pageText, HmiUserControlInfo uc, string id,
        HmiLayout layout, IReadOnlyDictionary<string, JsonElement> values, bool replace)
    {
        if (!IsValidId(id)) return (null, $"id '{id}' is not a valid control id (letters, digits, '_' and '-', not starting with a digit).");
        if (uc.Problem is not null) return (null, $"the parameters of '{uc.Path}' are unknown: {uc.Problem}");
        if (layout.WidthUnit is not ("px" or "%") || layout.HeightUnit is not ("px" or "%"))
            return (null, "widthUnit and heightUnit must be 'px' or '%'.");
        if (layout.Width is <= 0 || layout.Height is <= 0) return (null, "width and height must be positive.");

        var doc = HmiMarkup.Parse(pageText);
        if (doc.Root is null || doc.Root.CloseStart < 0)
            return (null, "the page has no closed root control to place the host in.");
        if (doc.Problems.Count > 0) return (null, "the page markup is not well-formed: " + string.Join("; ", doc.Problems));

        HmiElement? existing = doc.Elements.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (existing is not null && !replace)
            return (null, $"a control with id '{id}' already exists on line {existing.Line} — choose another id or pass replace=true.");
        if (existing is not null && !string.Equals(existing.Type, HmiMarkup.UserControlHostType, StringComparison.Ordinal))
            return (null, $"'{id}' on line {existing.Line} is a {existing.Type}, not a user-control host — replace refused.");
        if (existing is not null && ReferenceEquals(existing, doc.Root))
            return (null, $"'{id}' is the page's root control — replace refused.");
        if (replace && existing is null)
            return (null, $"replace=true, but there is no control with id '{id}' to replace.");

        // parameters by propertyName (also accepted: displayName or the attribute itself)
        var attrs = new List<(string Attribute, JsonElement Value)>();
        foreach (var (name, value) in values)
        {
            HmiUcParameter? p = uc.Parameters.FirstOrDefault(x => x.PropertyName.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? uc.Parameters.FirstOrDefault(x => string.Equals(x.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                ?? uc.Parameters.FirstOrDefault(x => x.Attribute.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p is null)
                return (null, $"'{name}' is not a parameter of '{uc.Path}'. Parameters: " +
                              (uc.Parameters.Count == 0 ? "(none)" : string.Join(", ", uc.Parameters.Select(x => x.PropertyName))) + ".");
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (!p.Bindable && value.ValueKind == JsonValueKind.String && value.GetString()!.Contains("%s%", StringComparison.Ordinal))
                return (null, $"parameter '{p.PropertyName}' is not bindable — pass a value, not a symbol binding.");
            attrs.Add((p.Attribute, value));
        }
        var missing = uc.Parameters.Where(p => p.RequiredOnCompile && p.DefaultJson is null &&
                                               !attrs.Any(a => a.Attribute.Equals(p.Attribute, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
            return (null, $"required parameter(s) not set: {string.Join(", ", missing.Select(p => p.PropertyName))}.");

        int width = layout.Width ?? (layout.WidthUnit == "%" ? 100 : uc.DesignWidth ?? DefaultWidth);
        int height = layout.Height ?? (layout.HeightUnit == "%" ? 100 : uc.DesignHeight ?? DefaultHeight);

        var (result, markup, line) = Insert(pageText, doc.Root, existing, HmiMarkup.UserControlHostType, id, layout, width, height,
            [(HmiMarkup.TargetUserControlAttribute, Escape(uc.Path))], attrs);
        return (new HmiPlacementResult(result, markup, attrs.Select(a => a.Attribute).ToList(), existing is not null, line), null);
    }

    // ---- any control (hmi-controls) ------------------------------------------------

    // Set by the layout parameters, not through 'attributes'.
    private static readonly HashSet<string> LayoutAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "data-tchmi-type", "data-tchmi-left", "data-tchmi-left-unit", "data-tchmi-top", "data-tchmi-top-unit",
        "data-tchmi-width", "data-tchmi-width-unit", "data-tchmi-height", "data-tchmi-height-unit",
    };

    /// <summary>
    /// Puts any control of the project's packages on a page: as last child of <paramref name="parentId"/> (a container
    /// control; default the page's root) or in place of the control with the same id and type. Attributes are named
    /// like in the control's description (attribute, propertyName or displayName) and checked against it.
    /// </summary>
    public static (HmiPlacementResult? Result, string? Error) PlaceControl(string pageText, HmiControlCatalog catalog, HmiControlType type,
        string id, HmiLayout layout, IReadOnlyDictionary<string, JsonElement> values, string? parentId, bool replace)
    {
        if (!IsValidId(id)) return (null, $"id '{id}' is not a valid control id (letters, digits, '_' and '-', not starting with a digit).");
        if (layout.WidthUnit is not ("px" or "%") || layout.HeightUnit is not ("px" or "%"))
            return (null, "widthUnit and heightUnit must be 'px' or '%'.");
        if (layout.Width is <= 0 || layout.Height is <= 0) return (null, "width and height must be positive.");

        var doc = HmiMarkup.Parse(pageText);
        if (doc.Root is null || doc.Root.CloseStart < 0) return (null, "the page has no closed root control to place the control in.");
        if (doc.Problems.Count > 0) return (null, "the page markup is not well-formed: " + string.Join("; ", doc.Problems));

        HmiElement? existing = doc.Elements.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (existing is not null && !replace)
            return (null, $"a control with id '{id}' already exists on line {existing.Line} — choose another id or pass replace=true.");
        if (replace && existing is null) return (null, $"replace=true, but there is no control with id '{id}' to replace.");
        if (existing is not null && ReferenceEquals(existing, doc.Root)) return (null, $"'{id}' is the page's root control — replace refused.");
        if (existing is not null && !string.Equals(existing.Type, type.FullName, StringComparison.OrdinalIgnoreCase))
            return (null, $"'{id}' on line {existing.Line} is a {existing.Type}, not a {type.FullName} — replace refused.");

        HmiElement parent = doc.Root;
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            HmiElement? p = doc.Elements.FirstOrDefault(e => string.Equals(e.Id, parentId, StringComparison.Ordinal));
            if (p is null) return (null, $"there is no control with id '{parentId}' on the page to place the control in.");
            if (existing is not null && !ReferenceEquals(existing.Parent, p))
                return (null, $"'{id}' sits in '{existing.Parent?.Id}', not in '{parentId}' — replace keeps the place; omit parentId.");
            if (!ReferenceEquals(p, doc.Root))
            {
                var (pt, _) = p.Type is null ? (null, null) : catalog.Find(p.Type);
                if (pt is null) return (null, $"'{parentId}' is a {p.Type ?? "(no type)"}, which the project's packages do not describe — cannot tell whether it takes children.");
                if (!pt.Container) return (null, $"'{parentId}' is a {pt.FullName}, which takes no child controls (not a container). Use a TcHmiContainer, TcHmiGrid or TcHmiFlexContainer.");
            }
            if (p.CloseStart <= p.Start) return (null, $"'{parentId}' is written as an empty element without a closing tag.");
            parent = p;
        }

        var all = catalog.AllAttributes(type);
        var attrs = new List<(string Attribute, JsonElement Value)>();
        foreach (var (key, value) in values)
        {
            HmiControlAttribute? a = HmiControlCatalog.Attribute(all, key);
            if (a is null)
            {
                string bare = key.Replace("data-tchmi-", "", StringComparison.OrdinalIgnoreCase);
                var names = all.Where(x => x.Visible).Select(x => x.PropertyName ?? x.Name).ToList();
                string near = HmiBindingCheck.Near(bare, names);
                var hint = near.Length > 0 ? [] : names.Where(n => n.Contains(bare, StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
                return (null, $"'{key}' is not an attribute of {type.FullName}" + (near.Length > 0 ? near
                    : hint.Count > 0 ? $" — did you mean {string.Join(", ", hint)}?" : " — twincat_hmi_describe_controls lists them."));
            }
            if (LayoutAttributes.Contains(a.Name)) return (null, $"'{key}' is set by the left/top/width/height parameters, not through attributes.");
            if (a.ReadOnly) return (null, $"'{a.PropertyName ?? a.Name}' of {type.Name} is read-only (the control sets it).");
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (!a.Bindable && value.ValueKind == JsonValueKind.String && value.GetString()!.Contains("%s%", StringComparison.Ordinal))
                return (null, $"'{a.PropertyName ?? a.Name}' of {type.Name} is not bindable — pass a value, not a symbol binding.");
            if (attrs.Any(x => x.Attribute.Equals(a.Name, StringComparison.OrdinalIgnoreCase)))
                return (null, $"'{a.PropertyName ?? a.Name}' is given twice.");
            attrs.Add((a.Name, value));
        }

        int width = layout.Width ?? (layout.WidthUnit == "%" ? 100 : type.DesignWidth ?? 100);
        int height = layout.Height ?? (layout.HeightUnit == "%" ? 100 : type.DesignHeight ?? 50);
        var (result, markup, line) = Insert(pageText, parent, existing, type.FullName, id, layout, width, height, [], attrs);
        return (new HmiPlacementResult(result, markup, attrs.Select(a => a.Attribute).ToList(), existing is not null, line), null);
    }

    // ---- markup + insertion ----------------------------------------------------------

    /// <summary>Builds the control's markup and puts it in place of <paramref name="existing"/>, or on its own line
    /// before the closing tag of <paramref name="parent"/> with the indentation of its last child.</summary>
    private static (string Text, string Markup, int Line) Insert(string pageText, HmiElement parent, HmiElement? existing, string type,
        string id, HmiLayout layout, int width, int height, IReadOnlyList<(string Name, string EscapedValue)> fixedAttributes,
        IReadOnlyList<(string Attribute, JsonElement Value)> attrs)
    {
        int insertAt;
        string indent;
        if (existing is not null)
        {
            insertAt = existing.Start;
            indent = HmiMarkup.IndentAt(pageText, existing.Start);
        }
        else
        {
            insertAt = parent.CloseStart;
            HmiElement? sibling = parent.Children.LastOrDefault();
            indent = sibling is not null ? HmiMarkup.IndentAt(pageText, sibling.Start) : HmiMarkup.IndentAt(pageText, parent.Start) + "  ";
        }

        string nl = pageText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var sb = new StringBuilder();
        sb.Append("<div id=\"").Append(id).Append("\" data-tchmi-type=\"").Append(type).Append('"');
        foreach (var (name, v) in fixedAttributes) sb.Append(' ').Append(name).Append("=\"").Append(v).Append('"');
        sb.Append(CultureInfo.InvariantCulture, $" data-tchmi-left=\"{layout.Left}\" data-tchmi-left-unit=\"px\" data-tchmi-top=\"{layout.Top}\" data-tchmi-top-unit=\"px\"")
          .Append(CultureInfo.InvariantCulture, $" data-tchmi-width=\"{width}\" data-tchmi-width-unit=\"{layout.WidthUnit}\"")
          .Append(CultureInfo.InvariantCulture, $" data-tchmi-height=\"{height}\" data-tchmi-height-unit=\"{layout.HeightUnit}\"");
        var scripts = new List<string>();
        foreach (var (attribute, value) in attrs)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object or JsonValueKind.Array:
                    scripts.Add($"<script data-tchmi-target-attribute=\"{attribute}\" type=\"application/json\">{nl}{indent}  " +
                                JsonSerializer.Serialize(value) + $"{nl}{indent}  </script>");
                    break;
                case JsonValueKind.String:
                    sb.Append(' ').Append(attribute).Append("=\"").Append(Escape(value.GetString()!)).Append('"');
                    break;
                default:
                    sb.Append(' ').Append(attribute).Append("=\"").Append(value.GetRawText()).Append('"');
                    break;
            }
        }
        sb.Append('>');
        foreach (string s in scripts) sb.Append(nl).Append(indent).Append("  ").Append(s);
        sb.Append(nl).Append(indent).Append("</div>");
        string markup = sb.ToString();

        if (existing is not null)
            return (pageText[..existing.Start] + markup + pageText[existing.End..], markup, existing.Line);

        // insert on its own line before the parent's closing tag, keeping the closing tag's indentation
        int lineStart = pageText.LastIndexOf('\n', Math.Max(0, insertAt - 1)) + 1;
        bool closeOnOwnLine = pageText[lineStart..insertAt].Trim().Length == 0;
        string before = closeOnOwnLine ? pageText[..lineStart] : pageText[..insertAt] + nl;
        string after = closeOnOwnLine ? pageText[lineStart..] : pageText[insertAt..];
        if (before.Length > 0 && !before.EndsWith('\n')) before += nl;
        return (before + indent + markup + nl + after, markup, HmiMarkup.LineStarts(before).Length);
    }

    // Only what breaks a double-quoted attribute; umlauts stay readable as TE2000 writes them.
    internal static string Escape(string s) => s
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
