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

        int insertAt;
        string indent;
        if (existing is not null)
        {
            insertAt = existing.Start;
            indent = HmiMarkup.IndentAt(pageText, existing.Start);
        }
        else
        {
            insertAt = doc.Root.CloseStart;
            HmiElement? sibling = doc.Root.Children.LastOrDefault();
            indent = sibling is not null ? HmiMarkup.IndentAt(pageText, sibling.Start) : HmiMarkup.IndentAt(pageText, doc.Root.Start) + "  ";
        }

        string nl = pageText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var sb = new StringBuilder();
        sb.Append("<div id=\"").Append(id).Append("\" data-tchmi-type=\"").Append(HmiMarkup.UserControlHostType).Append('"')
          .Append(" data-tchmi-target-user-control=\"").Append(Escape(uc.Path)).Append('"')
          .Append(CultureInfo.InvariantCulture, $" data-tchmi-left=\"{layout.Left}\" data-tchmi-left-unit=\"px\" data-tchmi-top=\"{layout.Top}\" data-tchmi-top-unit=\"px\"")
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

        string result;
        int line;
        if (existing is not null)
        {
            result = pageText[..existing.Start] + markup + pageText[existing.End..];
            line = existing.Line;
        }
        else
        {
            // insert on its own line before the root's closing tag, keeping the closing tag's indentation
            int lineStart = pageText.LastIndexOf('\n', Math.Max(0, insertAt - 1)) + 1;
            bool closeOnOwnLine = pageText[lineStart..insertAt].Trim().Length == 0;
            string before = closeOnOwnLine ? pageText[..lineStart] : pageText[..insertAt] + nl;
            string after = closeOnOwnLine ? pageText[lineStart..] : pageText[insertAt..];
            if (before.Length > 0 && !before.EndsWith('\n')) before += nl;
            result = before + indent + markup + nl + after;
            line = HmiMarkup.LineStarts(before).Length;
        }
        return (new HmiPlacementResult(result, markup, attrs.Select(a => a.Attribute).ToList(), existing is not null, line), null);
    }

    // Only what breaks a double-quoted attribute; umlauts stay readable as TE2000 writes them.
    internal static string Escape(string s) => s
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
