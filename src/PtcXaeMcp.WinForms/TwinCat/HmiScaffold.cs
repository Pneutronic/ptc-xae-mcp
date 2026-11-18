using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-authoring: twincat_hmi_scaffold_symbol_view — a .content with one row per field of a server symbol.
// The shape comes from the symbol's current VALUE (object → struct, true/false → BOOL, number, string),
// which the HMI server returns the same way for an ADS runtime and a USE_TMC runtime.

internal sealed record HmiScaffoldResult(string Text, int Rows, int Width, int Height, IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Bindings);

internal static partial class HmiScaffold
{
    // Beckhoff controls and the attribute each binds (Description.json of Beckhoff.TwinCAT.HMI.Controls 14.8.3, checked
    // 2026-11-18: TcHmiCheckbox data-tchmi-state-symbol, TcHmiNumericInput data-tchmi-value, TcHmiTextbox/-block data-tchmi-text).
    internal const string Content = "TcHmi.Controls.System.TcHmiContent";
    internal const string Textblock = "TcHmi.Controls.Beckhoff.TcHmiTextblock";
    internal const string NumericInput = "TcHmi.Controls.Beckhoff.TcHmiNumericInput";
    internal const string Textbox = "TcHmi.Controls.Beckhoff.TcHmiTextbox";
    internal const string Checkbox = "TcHmi.Controls.Beckhoff.TcHmiCheckbox";
    internal const string TextAttribute = "data-tchmi-text";
    internal const string ValueAttribute = "data-tchmi-value";
    internal const string StateSymbolAttribute = "data-tchmi-state-symbol";
    internal const string VerticalAlignment = "data-tchmi-text-vertical-alignment";
    internal const string FontWeightAttribute = "data-tchmi-text-font-weight";
    // BindingMode 'OneWay' | 'TwoWay' (framework _Types.d.ts); live 2026-11-18: NumericInput.setValue(42.25) reached the
    // server symbol with TwoWay; a checkbox writes through its state-symbol without a binding mode.
    internal const string TwoWay = "|BindingMode=TwoWay";

    internal const int RowHeight = 30;
    internal const int RowStep = 40;
    internal const int Margin = 20;
    internal const int LabelWidth = 260;
    internal const int ValueWidth = 220;
    internal const int Indent = 20;
    internal const int MaxRows = 300;

    [GeneratedRegex(@"[^A-Za-z0-9_]")]
    private static partial Regex IdUnsafe();

    /// <summary>The HMI server reports a value as JSON — except a BOOL, live "True"/"False". Normalise to JSON.</summary>
    public static JsonElement? ParseValue(string? raw)
    {
        if (raw is null) return null;
        string t = raw.Trim();
        if (t.Equals("True", StringComparison.Ordinal)) t = "true";
        else if (t.Equals("False", StringComparison.Ordinal)) t = "false";
        try
        {
            using var doc = JsonDocument.Parse(t);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(raw));
            return doc.RootElement.Clone();
        }
    }

    public static string ContentId(string fileName) =>
        IdUnsafe().Replace(Path.GetFileNameWithoutExtension(fileName), "_");

    public static HmiScaffoldResult Build(string symbol, JsonElement value, string contentId, bool readOnly, int maxDepth)
    {
        var rows = new StringBuilder();
        var skipped = new List<string>();
        var bindings = new List<string>();
        int y = Margin, count = 0, maxRight = Margin + LabelWidth + ValueWidth;

        void Row(string label, string path, JsonElement v, int depth)
        {
            if (count >= MaxRows) { skipped.Add($"{(path.Length == 0 ? symbol : path)}: more than {MaxRows} rows"); return; }
            string idBase = contentId + "_" + IdUnsafe().Replace(path.Length == 0 ? "Value" : path.Replace("::", "_", StringComparison.Ordinal), "_");
            string expr = path.Length == 0 ? symbol : symbol + "::" + path;
            int left = Margin + depth * Indent;

            switch (v.ValueKind)
            {
                case JsonValueKind.Object:
                    if (depth >= maxDepth) { skipped.Add($"{expr}: deeper than maxDepth {maxDepth}"); return; }
                    if (path.Length > 0)
                    {
                        AppendTextblock(rows, idBase + "_Header", left, y, LabelWidth + ValueWidth, label, bold: true);
                        y += RowStep; count++;
                    }
                    foreach (var p in v.EnumerateObject())
                        Row(p.Name, path.Length == 0 ? p.Name : path + "::" + p.Name, p.Value, path.Length == 0 ? depth : depth + 1);
                    return;
                case JsonValueKind.Array:
                    skipped.Add($"{expr}: array — not scaffolded (bind it to a datagrid or a user control)");
                    return;
            }

            AppendTextblock(rows, idBase + "_Label", left, y, LabelWidth - depth * Indent, label, bold: false);
            int vx = Margin + LabelWidth + Indent;
            string binding = "%s%" + expr + "%/s%";
            string writable = "%s%" + expr + TwoWay + "%/s%";
            switch (v.ValueKind)
            {
                case JsonValueKind.True or JsonValueKind.False:
                    AppendControl(rows, idBase, Checkbox, vx, y, RowHeight, RowHeight, (StateSymbolAttribute, binding),
                        readOnly ? [("data-tchmi-is-enabled", "false")] : []);
                    break;
                case JsonValueKind.Number when !readOnly:
                    AppendControl(rows, idBase, NumericInput, vx, y, ValueWidth, RowHeight, (ValueAttribute, writable), []);
                    binding = writable;
                    break;
                case JsonValueKind.String when !readOnly:
                    AppendControl(rows, idBase, Textbox, vx, y, ValueWidth, RowHeight, (TextAttribute, writable), []);
                    binding = writable;
                    break;
                default:   // read-only number/string, null
                    AppendControl(rows, idBase, Textblock, vx, y, ValueWidth, RowHeight, (TextAttribute, binding),
                        [(VerticalAlignment, "Center")]);
                    break;
            }
            bindings.Add(binding);
            y += RowStep; count++;
        }

        Row(symbol, "", value, 0);
        int height = Math.Max(y + Margin - (RowStep - RowHeight), 100);
        int width = maxRight + Margin * 2;
        string text =
            $"<div id=\"{contentId}\" data-tchmi-type=\"{Content}\" data-tchmi-top=\"0\" data-tchmi-left=\"0\" " +
            $"data-tchmi-width=\"{width}\" data-tchmi-height=\"{height}\">\n" + rows + "</div>\n";
        return new HmiScaffoldResult(text, count, width, height, skipped, bindings);
    }

    // Labels sit in the middle of the 30 px row like the inputs next to them (live 2026-11-18: top-aligned looked off).
    private static void AppendTextblock(StringBuilder sb, string id, int left, int top, int width, string text, bool bold) =>
        AppendControl(sb, id, Textblock, left, top, width, RowHeight, (TextAttribute, text),
            bold ? [(VerticalAlignment, "Center"), (FontWeightAttribute, "Bold")] : [(VerticalAlignment, "Center")]);

    private static void AppendControl(StringBuilder sb, string id, string type, int left, int top, int width, int height,
        (string Name, string Value) main, (string Name, string Value)[] extras)
    {
        sb.Append("  <div id=\"").Append(id).Append("\" data-tchmi-type=\"").Append(type).Append('"')
          .Append(CultureInfo.InvariantCulture, $" data-tchmi-left=\"{left}\" data-tchmi-left-unit=\"px\" data-tchmi-top=\"{top}\" data-tchmi-top-unit=\"px\"")
          .Append(CultureInfo.InvariantCulture, $" data-tchmi-width=\"{width}\" data-tchmi-width-unit=\"px\" data-tchmi-height=\"{height}\" data-tchmi-height-unit=\"px\"")
          .Append(' ').Append(main.Name).Append("=\"").Append(HmiPlacement.Escape(main.Value)).Append('"');
        foreach (var (name, value) in extras) sb.Append(' ').Append(name).Append("=\"").Append(HmiPlacement.Escape(value)).Append('"');
        sb.Append(">\n  </div>\n");
    }
}
